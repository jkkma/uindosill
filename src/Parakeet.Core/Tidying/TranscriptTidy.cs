using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tidying;

/// <summary>What a tidy pass came to, beside the document it produced.</summary>
public sealed record TidySummary
{
    public required int Segments { get; init; }

    /// <summary>Segments whose rewrite was refused and left as spoken.</summary>
    public required int Refused { get; init; }

    /// <summary>Spoken words the accepted rewrites dropped, counted among words the normaliser can see.</summary>
    public required int DeletedWords { get; init; }

    /// <summary>Words that went through the low-confidence door.</summary>
    public required int ReplacedWords { get; init; }

    /// <summary>The sentence a surface prints about the pass, or null when there is nothing to say.</summary>
    public string? Describe()
    {
        if (Refused == 0 && ReplacedWords == 0)
        {
            return null;
        }

        var parts = new List<string>(2);
        if (Refused > 0)
        {
            parts.Add($"{Refused} of {Segments} line{(Segments == 1 ? string.Empty : "s")} kept as spoken because the rewrite changed or added words");
        }

        if (ReplacedWords > 0)
        {
            parts.Add($"{ReplacedWords} word{(ReplacedWords == 1 ? string.Empty : "s")} the recogniser doubted replaced by the model's guess");
        }

        return "Tidy: " + string.Join("; ", parts) + ".";
    }
}

/// <summary>
/// Drives a tidier over a transcript — every segment at once, the pass shape — and assembles
/// the tidied document with its provenance. The tandem shape enqueues as it goes and calls
/// <see cref="Assemble"/> at the end; both run through <see cref="TidyStage"/> and
/// <see cref="TidyContract"/>, so the two cannot disagree about what a line may become.
/// </summary>
public static class TranscriptTidy
{
    /// <summary>
    /// What goes between a tidied version's file name and its extension — <c>call.tidy.srt</c>
    /// beside <c>call.srt</c> — on the pattern the English pane's <c>.en</c> set and for the same
    /// reason: SubRip has no comment syntax, so the name is the only place a tidied file can say
    /// what it is, and the infix is what keeps it from overwriting the plain one.
    /// </summary>
    public const string FileInfix = ".tidy";

    /// <summary>
    /// The tidied document with the speaker labels of <paramref name="labelled"/> on it — the
    /// turns applied to the tidied segments and the speaker provenance carried across — or
    /// <paramref name="tidied"/> itself when the labelled document has no speakers.
    /// </summary>
    /// <remarks>
    /// The tidy runs beside the recogniser, on the segments as they arrive, and the speaker pass
    /// runs after the recogniser on the finished document; so the tidied document is assembled
    /// against the raw segments and the labels are put on it afterwards, from the turns the one
    /// diarisation produced. Running the labeller a second time over the tidied words would be a
    /// second read of the audio for the same answer. The tidied words keep the spoken words'
    /// times, so the per-word attribution lands where it did on the spoken transcript.
    /// </remarks>
    public static TranscriptDocument WithSpeakersOf(
        TranscriptDocument tidied, TranscriptDocument labelled, Diarisation.SpeakerLabellingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tidied);
        ArgumentNullException.ThrowIfNull(labelled);

        if (!labelled.HasSpeakers)
        {
            return tidied;
        }

        return tidied with
        {
            Segments = Diarisation.SpeakerAssignment.Apply(tidied.Segments, labelled.SpeakerTurns, options),
            SpeakerTurns = labelled.SpeakerTurns,
            SpeakerModelId = labelled.SpeakerModelId,
            SpeakerBackend = labelled.SpeakerBackend,
            SpeakerEmbeddingBackend = labelled.SpeakerEmbeddingBackend,
            RequestedSpeakerCount = labelled.RequestedSpeakerCount,
            SpeakerFolds = labelled.SpeakerFolds,
        };
    }

    /// <summary>
    /// Tidies <paramref name="document"/> a segment at a time and returns the tidied document.
    /// The source document is unchanged; the caller keeps it if it wants both.
    /// </summary>
    public static async Task<(TranscriptDocument Document, TidySummary Summary)> TidyAsync(
        TranscriptDocument document,
        ITranscriptTidier tidier,
        TidyOptions? options = null,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(tidier);
        options ??= TidyOptions.Default;
        options.Validate();

        var total = document.Segments.Count;
        var done = 0;

        await using var stage = new TidyStage(
            tidier,
            options,
            (_, _) =>
            {
                var completed = Interlocked.Increment(ref done);
                progress?.Report(new TranscriptionProgress
                {
                    Stage = TranscriptionStage.Tidying,
                    SegmentsCompleted = completed,
                    SegmentsTotal = total,
                });
            },
            ct);

        foreach (var segment in document.Segments)
        {
            stage.Enqueue(segment);
        }

        var outcomes = await stage.CompleteAsync().ConfigureAwait(false);
        return Assemble(document, outcomes, tidier.Capabilities);
    }

    /// <summary>
    /// The tidied document from a stage's outcomes, one per segment of <paramref name="source"/>
    /// in order, with the pass's provenance stamped on it.
    /// </summary>
    public static (TranscriptDocument Document, TidySummary Summary) Assemble(
        TranscriptDocument source,
        IReadOnlyList<TidyOutcome> outcomes,
        TidierCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (outcomes.Count != source.Segments.Count)
        {
            throw new InvalidOperationException(
                $"The tidy produced {outcomes.Count} outcomes for {source.Segments.Count} segments. A pass that loses " +
                "or invents entries loses or invents transcript, and neither is written.");
        }

        var segments = new List<TranscriptSegment>(outcomes.Count);
        var refused = 0;
        var deleted = 0;
        var replaced = 0;

        for (var i = 0; i < outcomes.Count; i++)
        {
            var outcome = outcomes[i];
            var spoken = source.Segments[i];

            // The contract keeps the timeline, the source index and the speaker; this holds it to
            // that rather than trusting it, because a tidied segment on the wrong span looks fine.
            if (outcome.Segment.Start != spoken.Start || outcome.Segment.End != spoken.End
                || outcome.Segment.SourceSegmentIndex != spoken.SourceSegmentIndex
                || !string.Equals(outcome.Segment.Speaker, spoken.Speaker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The tidy moved segment {i} or changed who said it. The pass writes words, never times or speakers.");
            }

            segments.Add(outcome.Segment);
            if (!outcome.Accepted)
            {
                refused++;
            }

            deleted += outcome.DeletedWords;
            replaced += outcome.Replacements.Count;
        }

        var document = source with
        {
            Segments = segments,
            TidyModelId = capabilities.ModelId,
            TidyBackend = capabilities.Backend,
            TidyRefusedSegments = refused,
        };

        return (document, new TidySummary
        {
            Segments = outcomes.Count,
            Refused = refused,
            DeletedWords = deleted,
            ReplacedWords = replaced,
        });
    }
}
