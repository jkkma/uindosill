using Parakeet.Core.Audio;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Diarisation;

/// <summary>
/// Puts speaker turns onto a transcript: every word gets the speaker whose turn it falls in, and
/// every segment is cut at the points where that speaker changes, so what the formatters print
/// under a name is only what that person said. A pure function of two lists — no audio, no model —
/// which is what keeps it testable to the word.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attribution.</b> A word goes to the turn that overlaps it most; when no turn overlaps it at
/// all, to the nearest turn within <see cref="SpeakerLabellingOptions.AttributionTolerance"/>;
/// otherwise it stays unattributed. That is one speaker per word even inside crosstalk, and not
/// two: the transcript is a single line of text with one name in front of it, and a word said by
/// two people at once is still one word in that text. The word-level choice the study left open is
/// taken here, and it is the simpler one; dual attribution needs a transcript shape that does not
/// exist yet.
/// </para>
/// <para>
/// <b>Crosstalk ties, and why the tie-break is the turn that ends later.</b> "Overlaps it most"
/// decides nothing while two turns both contain the word — the overlap is the word's own length
/// for each, so every word inside a crosstalk stretch ties, and the tie-break alone chooses the
/// name. Two shapes reach it. In a <i>back-channel</i> one speaker's "yeah" lands inside another's
/// turn, and the words are the interrupted speaker's; in a <i>handoff</i> one speaker finishes
/// while the next is already under way, and the words run on into what the next speaker says.
/// Ending later separates them, because the container of a back-channel also outlasts it: the
/// interrupted speaker keeps their words, and at a handoff the incoming speaker takes the
/// overlapped ones, so the name changes where the crosstalk starts rather than where it ends.
/// Preferring the turn that <i>started</i> earlier — what this did until 2026-08-20 — is right for
/// the first shape and wrong for the second, and it holds the outgoing name across the whole
/// overlap: measured on a 2 h 37 m two-host podcast, 465 of 26,105 attributed words (1.78%),
/// touching 138 of 1,874 segments. What no measurement here settles is whether the new name is the
/// <i>correct</i> one — both people really are talking, this repository holds no
/// speaker-attributed reference transcript, and diarisation error rate scores turns rather than
/// attribution. See <c>docs/UNPROVEN.md</c>.
/// </para>
/// <para>
/// <b>Splitting.</b> A segment is cut between two consecutive words of different speakers when the
/// segment's words, joined by single spaces, reproduce its text — the case for every segment the
/// real engine has ever produced here (1,378 of 1,378 in one three-hour transcript) and for the
/// fake. When they do not, the segment is left whole and carries the speaker of most of its words
/// by duration, because cutting text at a guessed position would print words under the wrong name
/// to fix a bookkeeping mismatch. A segment with no word timings cannot be cut and takes the
/// speaker who talks most during it. Splitting never merges: it only ever makes segments smaller.
/// </para>
/// </remarks>
public static class SpeakerAssignment
{
    public static IReadOnlyList<TranscriptSegment> Apply(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<SpeakerTurn> turns,
        SpeakerLabellingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(turns);
        options ??= SpeakerLabellingOptions.Default;
        options.Validate();

        if (turns.Count == 0)
        {
            return segments;
        }

        var sorted = turns.OrderBy(t => t.Start).ThenBy(t => t.End).ToArray();
        var result = new List<TranscriptSegment>(segments.Count);

        foreach (var segment in segments)
        {
            if (segment.Words.Count == 0)
            {
                result.Add(segment with { Speaker = MostByDuration(sorted, segment.Start, segment.End) });
                continue;
            }

            var words = segment.Words
                .Select(w => w with { Speaker = Dominant(sorted, w.Start, w.End, options) })
                .ToList();

            if (!JoinReproducesText(words, segment.Text))
            {
                result.Add(segment with { Words = words, Speaker = MostByDuration(words) });
                continue;
            }

            var runStart = 0;
            for (var i = 1; i <= words.Count; i++)
            {
                if (i < words.Count && string.Equals(words[i].Speaker, words[runStart].Speaker, StringComparison.Ordinal))
                {
                    continue;
                }

                var run = words.GetRange(runStart, i - runStart);
                result.Add(segment with
                {
                    Start = runStart == 0 ? segment.Start : run[0].Start,
                    End = i == words.Count ? segment.End : run[^1].End,
                    Text = string.Join(' ', run.Select(w => w.Text.Trim())),
                    Words = run,
                    Speaker = run[0].Speaker,
                });

                runStart = i;
            }
        }

        return result;
    }

    /// <summary>
    /// The turn that overlaps <c>[start, end]</c> most — a word is short enough that "the turn" and
    /// "the speaker" are the same question; ties go to the turn that ends later, then to the label
    /// that sorts first, so the answer is deterministic. When nothing overlaps, the nearest turn
    /// within the tolerance; otherwise null. A word inside crosstalk ties by construction, so that
    /// first tie-break is the whole of the decision there; the class remarks say why it is the end.
    /// </summary>
    internal static string? Dominant(SpeakerTurn[] sortedTurns, TimeSpan start, TimeSpan end, SpeakerLabellingOptions options)
    {
        SpeakerTurn? best = null;
        var bestOverlap = TimeSpan.Zero;
        SpeakerTurn? nearest = null;
        var nearestGap = TimeSpan.MaxValue;

        foreach (var turn in sortedTurns)
        {
            var overlap = Min(turn.End, end) - Max(turn.Start, start);

            // A zero-length word — the decoder's end-before-start collapse — overlaps nothing, so it
            // is judged by containment: inside a turn it is that turn's, and inside two it takes the
            // tie-break every other word in the crosstalk takes. Until 2026-08-22 it fell to the gap
            // rule below with a negative gap, which the nearest-turn rule read as "closest", and went
            // to the turn that started earlier — the opposite of the tie-break, cutting B | A | B
            // around one word under the other name.
            var contained = start == end && overlap == TimeSpan.Zero;
            if (overlap > TimeSpan.Zero || contained)
            {
                if (best is null || overlap > bestOverlap
                    || (overlap == bestOverlap && (turn.End > best.End
                        || (turn.End == best.End && string.CompareOrdinal(turn.Speaker, best.Speaker) < 0))))
                {
                    best = turn;
                    bestOverlap = overlap;
                }

                continue;
            }

            var gap = turn.End <= start ? start - turn.End : turn.Start - end;
            if (gap < nearestGap)
            {
                nearest = turn;
                nearestGap = gap;
            }
        }

        if (best is not null)
        {
            return best.Speaker;
        }

        return nearest is not null && nearestGap <= options.AttributionTolerance ? nearest.Speaker : null;
    }

    /// <summary>
    /// The speaker with the most speech inside <c>[start, end]</c>, summed over every turn of
    /// theirs — a thirty-second segment can hold many short turns of one speaker, and the one who
    /// talks most is not always the one whose single longest turn is longest. Ties go to the label
    /// that sorts first. Null when nobody talks during it.
    /// </summary>
    internal static string? MostByDuration(SpeakerTurn[] sortedTurns, TimeSpan start, TimeSpan end)
    {
        var totals = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var turn in sortedTurns)
        {
            var overlap = Min(turn.End, end) - Max(turn.Start, start);
            if (overlap <= TimeSpan.Zero)
            {
                continue;
            }

            totals.TryGetValue(turn.Speaker, out var total);
            totals[turn.Speaker] = total + overlap;
        }

        return totals.Count == 0
            ? null
            : totals.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
    }

    private static string? MostByDuration(List<TranscriptWord> words)
    {
        var totals = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            if (word.Speaker is null)
            {
                continue;
            }

            totals.TryGetValue(word.Speaker, out var total);
            totals[word.Speaker] = total + (word.End > word.Start ? word.End - word.Start : TimeSpan.Zero);
        }

        return totals.Count == 0
            ? null
            : totals.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
    }

    // The definition lives on the segment, shared with SentenceSplitter, so the two cutters cannot
    // disagree about which segments may be cut.
    private static bool JoinReproducesText(List<TranscriptWord> words, string text) =>
        TranscriptSegment.WordsReproduceText(words, text);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>Drives a labeller over a source and puts the result onto a finished document.</summary>
public static class SpeakerLabelling
{
    /// <summary>
    /// Labels <paramref name="audio"/> with <paramref name="labeller"/>, renames the voices for
    /// display when the options ask for it, attributes the document's words and segments, and
    /// returns a document that also carries the raw turns, the labeller's model id and backend, and
    /// what the requested speaker count did to the labels, all as provenance. The caller opens
    /// <paramref name="audio"/> — a fresh source, because the one the transcript came from has been
    /// read to its end.
    /// </summary>
    /// <remarks>
    /// The folds come back on the returned document rather than through a collection the caller
    /// passes in, which is what this took until 2026-08-22. A caller that wanted to print them and
    /// a transcript that had to record them were two channels carrying one fact, and the transcript
    /// was the one that went unfilled — an archived run showed no trace of its own fold. One
    /// channel now, and every surface reads it off the document.
    /// </remarks>
    public static async Task<TranscriptDocument> LabelAsync(
        TranscriptDocument document,
        ISpeakerLabeller labeller,
        IAudioSource audio,
        SpeakerLabellingOptions? options = null,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(labeller);
        ArgumentNullException.ThrowIfNull(audio);
        options ??= SpeakerLabellingOptions.Default;
        options.Validate();

        var raw = await labeller.LabelAsync(audio, options, progress, ct).ConfigureAwait(false);

        // The user's count, applied where the model could not be told it. A labeller that estimates
        // its own count can over-segment one person into two labels on a long recording, and that
        // is the one failure a post-step can repair — two labels merge, one label cannot be split.
        // A no-op when the model already produced no more labels than were asked for.
        IReadOnlyList<SpeakerFold> folds = [];
        var counted = options.SpeakerCount is { } wanted ? SpeakerTurns.FoldDownTo(raw, wanted, out folds) : raw;

        var turns = options.DisplayNameFormat is { } format
            ? SpeakerTurns.RenameByFirstAppearance(counted, format)
            : counted;

        return document with
        {
            Segments = SpeakerAssignment.Apply(document.Segments, turns, options),
            SpeakerTurns = turns,
            SpeakerModelId = labeller.Capabilities.ModelId,

            // Read off the loaded labeller, which is the only thing that knows: since the diariser
            // moved out of process the provider is resolved inside the sidecar, so this is not a
            // value any caller could have supplied.
            SpeakerBackend = labeller.Capabilities.Backend,

            // The runtime beside the provider, because for the second diariser the provider alone is
            // ambiguous: torch and ONNX Runtime's CPU provider both report `cpu` and they do not
            // produce the same labels. Null-ed rather than stored when the labeller has one runtime,
            // so nothing is written into a transcript that has no question to answer.
            SpeakerEmbeddingBackend = labeller.Capabilities.EmbeddingBackend is { Length: > 0 } embedding
                ? embedding
                : null,

            // The count as asked for, and what honouring it cost. Both, because they answer
            // different questions and neither implies the other: a count that folded nothing still
            // constrained the run, and folds cannot occur without a count.
            RequestedSpeakerCount = options.SpeakerCount,
            SpeakerFolds = folds,
        };
    }

    /// <summary>
    /// The sentence a caller owes the user when the labeller has a speaker cap and the file
    /// reached it: at the cap, a further voice is merged into one of the others by construction,
    /// and labels that look complete are not. Null when there is no cap or it was not reached.
    /// </summary>
    public static string? DescribeLimit(ISpeakerLabeller labeller, TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(labeller);
        ArgumentNullException.ThrowIfNull(document);

        if (labeller.Capabilities.MaxSpeakers is not { } max)
        {
            return null;
        }

        var found = SpeakerTurns.Speakers(document.SpeakerTurns).Count;
        return found >= max
            ? $"{found} speakers were labelled, which is the most this labeller can tell apart; a further voice, if there is one, has been merged into one of them."
            : null;
    }

    /// <summary>
    /// The sentence a caller owes the user when the recording is longer than anything this
    /// labeller's output has been established on. Null when there is no such bound, the length is
    /// unknown, or the file is inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third of these sentences, and the one measured last. <see cref="DescribeUnreachableCount"/>
    /// is about a limit in the model's geometry; this is about where the evidence stops, which is a
    /// property of what has been scored rather than of what the model is. On 2026-08-20 the
    /// diariser's speaker count was measured across growing windows of this project's own podcast
    /// material and came out right up to an hour and wrong on every file past two — while the corpus
    /// its gate was passed on, AMI, has meetings averaging about half an hour. The gate could not
    /// have caught it, so the honest thing is to say so where somebody transcribing a three-hour
    /// recording will read it.
    /// </para>
    /// <para>
    /// It warns and continues, for the same reason the cap does: a long recording still transcribes
    /// correctly, and it is only the speaker labels that are unestablished.
    /// </para>
    /// </remarks>
    public static string? DescribeDurationRisk(SpeakerLabellerCapabilities capabilities, TimeSpan? duration)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return DescribeDurationRisk(capabilities.Limits, duration);
    }

    /// <summary>
    /// The same sentence, from what is known before anything is loaded.
    /// </summary>
    /// <remarks>
    /// Two overloads and one body, because the window has to say this while the weights are still on
    /// disk and the command line says it off a loaded labeller. Two bodies would be two sentences,
    /// and the one beside the field would drift from the one that stops the batch.
    /// </remarks>
    public static string? DescribeDurationRisk(SpeakerLabellerLimits limits, TimeSpan? duration)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (duration is not { } length || limits.ReliableUpTo is not { } bound || length <= bound)
        {
            return null;
        }

        var who = limits.Name;
        return $"this recording is {length.TotalMinutes:F0} minutes and {who}'s speaker labels are only reliable "
            + $"up to {bound.TotalMinutes:F0} minutes. On a recording this long it can report the wrong number "
            + "of speakers, so treat the names as a guess; the words are unaffected.";
    }

    /// <summary>
    /// The sentence a caller owes the user <i>before</i> the run, when they have asked for more
    /// speakers than the labeller can ever produce. Null when there is no cap, no count was asked
    /// for, or the count is within the cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of <see cref="DescribeLimit"/>, and the half that was missing. That one fires
    /// afterwards and reports what happened; this one fires before any audio is read and reports
    /// what cannot happen. The difference matters because the two sentences send a reader in
    /// opposite directions: <i>"4 speakers were labelled"</i> after a seven-voice recording reads as
    /// a fact about the recording, and only <i>"seven was never reachable"</i> reads as a fact about
    /// the tool. Someone who does not know the cap will believe the transcript.
    /// </para>
    /// <para>
    /// It warns and does not refuse, which was decided with the maintainer on 2026-08-20. Somebody
    /// with six speakers who knows they will get four still has a good transcript — the words are
    /// unaffected, only the labels are capped — and blocking that run would cost them something
    /// real to protect them from something they have just been told. It is also the house pattern:
    /// a count that cannot be honoured is reported as ignored rather than applied.
    /// </para>
    /// </remarks>
    public static string? DescribeUnreachableCount(SpeakerLabellerCapabilities capabilities, int? requested)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return DescribeUnreachableCount(capabilities.Limits, requested);
    }

    /// <summary>The same sentence, from what is known before anything is loaded.</summary>
    public static string? DescribeUnreachableCount(SpeakerLabellerLimits limits, int? requested)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (requested is not { } count || limits.MaxSpeakers is not { } max || count <= max)
        {
            return null;
        }

        var who = limits.Name;
        return $"{count} speakers were asked for and {who} can tell apart at most {max}, so {count} was never "
            + $"reachable. Voices past the {max} it finds are merged into those {max} rather than reported, and the "
            + "speaker labels will look complete when they are not. Continuing rather than refusing: everything "
            + "but the speaker labels is unaffected.";
    }

    // **Five helpers stood here and went with the diariser on 2026-08-27.** DescribeBackend named
    // what cuda and DirectML cost against the published figure; DescribeEmbeddingBackend named what
    // an ONNX speaker embedder cost against torch; DescribeParityFailure, its string overload and
    // DescribeParityNotRun said what a failed or unrun parity check meant.
    //
    // All five spoke about the ONNX diariser, and every number in them — 16.33%, 16.10%, 53.15% on
    // AMI test — was measured on it. It is in `attic/sortformer/`. The pipeline that ships now is
    // torch on both stages with no execution provider to choose and no second path to compare
    // against, so there is no parity check to report and no backend that has been shown to move the
    // answer. **That is a smaller claim than the old silence made.** These helpers returned null for
    // cpu and webgpu because those two had been *measured* to agree; nothing here has been measured
    // at all, and saying nothing now means nothing is known rather than that nothing is wrong.
    // `docs/UNPROVEN.md` is where that distinction is kept.
}
