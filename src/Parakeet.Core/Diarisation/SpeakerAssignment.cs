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
        return $"this recording is {length.TotalMinutes:F0} minutes and {who}'s speaker labels have only been "
            + $"established up to {bound.TotalMinutes:F0} minutes. Past that they are not known to be wrong so "
            + "much as not known to be right: on this project's own podcast material the speaker count came out "
            + "correct at every length up to that and wrong on every recording over two hours, where it reported "
            + "four speakers whether there were two or seven. Treat the speaker labels on a recording this long "
            + "as a guess; the words are unaffected.";
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

    /// <summary>
    /// What a backend means for the figures this project publishes, or null when it means nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than in the command line's factory because the window owes the same sentence, and
    /// a measured finding written down twice is a finding one of whose copies goes stale. What each
    /// surface adds is its own remedy — the command line can name a flag and the window cannot,
    /// since it chooses the backend itself — so the remedy is not part of this.
    /// </para>
    /// <para>
    /// <b>Null for CPU and for WebGPU, and that silence is a measurement.</b> On AMI test,
    /// 2026-08-21: cpu 16.3324%, webgpu 16.3319%, cuda 16.1021%, DirectML at its own defaults
    /// 53.15%. WebGPU lands 0.0005 points from the CPU — closer than this project's own
    /// C#-against-Python port managed — so the published figure describes it, and warning on every
    /// run about a backend that agrees would train people to ignore the line that matters.
    /// </para>
    /// </remarks>
    public static string? DescribeBackend(ComputeBackend backend) => backend switch
    {
        ComputeBackend.Cuda =>
            "Speaker labelling ran on cuda. It does not reproduce the CPU's probabilities — on AMI test it scores " +
            "16.10% against the CPU's 16.33%, and two CUDA runs on different driver and library versions have " +
            "differed by 0.40 points — so these labels are this machine's result rather than the published one.",
        ComputeBackend.DirectMl =>
            "WARNING: speaker labelling ran on DirectML, which has not passed parity here. At ONNX Runtime's " +
            "default settings it scores 53.15% diarisation error against the CPU's 16.33% while producing speaker " +
            "turns that look entirely normal. Treat these labels as unverified.",
        _ => null,
    };

    /// <summary>
    /// What a failed parity check means, given how far the probabilities were off.
    /// </summary>
    /// <remarks>
    /// Takes the two numbers rather than a result type, so that this project's one shared vocabulary
    /// for diarisation does not have to learn about the sidecar. The magnitude is in the sentence
    /// because "the check failed" with no number tells a user nothing they can act on: a stack
    /// sitting just past the tolerance and one scoring 53% diarisation error deserve different
    /// reactions.
    /// </remarks>
    public static string DescribeParityFailure(double maxAbsoluteDifference, double tolerance) =>
        // Invariant, as every user-facing figure in this repository is: on a comma-decimal machine
        // this read "8,143e-04" until 2026-08-22, and CA1305 cannot see an interpolated string.
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"WARNING: this machine's diariser does not reproduce the reference. Its probabilities differ by up to " +
            $"{maxAbsoluteDifference:0.###e+00} against a tolerance of {tolerance:0.###e+00}. The speaker labels are " +
            $"this machine's own result and no diarisation error rate published by this project describes them.");

    /// <summary>
    /// What a failed parity check means when the sidecar gave a reason rather than a magnitude —
    /// a shape that does not match the reference's, probabilities that are not finite.
    /// </summary>
    public static string DescribeParityFailure(string reason) =>
        $"WARNING: this machine's diariser does not reproduce the reference: {reason}. The speaker labels are " +
        "this machine's own result and no diarisation error rate published by this project describes them.";

    /// <summary>
    /// What it means that the parity check could not be run at all: not that the labels are wrong,
    /// and not that they are right — that the one check standing between a user and a silently
    /// wrong provider did not happen, and the labels are unverified.
    /// </summary>
    /// <remarks>
    /// A third state, and until 2026-08-22 a silent one: a check that crashed was reported exactly
    /// as a check that was never asked for, and the run went on with nothing said.
    /// </remarks>
    public static string DescribeParityNotRun(string reason) =>
        $"WARNING: the check that compares this machine's diariser against the reference could not be run: {reason}. " +
        "The speaker labels are unverified — not known to be wrong, and not known to be the ones any diarisation " +
        "error rate published by this project describes.";
}
