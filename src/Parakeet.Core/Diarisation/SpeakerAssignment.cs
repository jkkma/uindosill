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
/// otherwise it stays unattributed. That is one speaker per word even inside crosstalk — the
/// dominant one — and not two: the transcript is a single line of text with one name in front of
/// it, and a word said by two people at once is still one word in that text. The word-level
/// choice the study left open is taken here, and it is the simpler one; dual attribution needs a
/// transcript shape that does not exist yet.
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
    /// "the speaker" are the same question; ties go to the turn that started first, then to the
    /// label that sorts first, so the answer is deterministic. When nothing overlaps, the nearest
    /// turn within the tolerance; otherwise null.
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
            if (overlap > TimeSpan.Zero)
            {
                if (best is null || overlap > bestOverlap
                    || (overlap == bestOverlap && (turn.Start < best.Start
                        || (turn.Start == best.Start && string.CompareOrdinal(turn.Speaker, best.Speaker) < 0))))
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

    private static bool JoinReproducesText(List<TranscriptWord> words, string text) =>
        string.Equals(string.Join(' ', words.Select(w => w.Text.Trim())), text.Trim(), StringComparison.Ordinal);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>Drives a labeller over a source and puts the result onto a finished document.</summary>
public static class SpeakerLabelling
{
    /// <summary>
    /// Labels <paramref name="audio"/> with <paramref name="labeller"/>, renames the voices for
    /// display when the options ask for it, attributes the document's words and segments, and
    /// returns a document that also carries the raw turns and the labeller's model id as
    /// provenance. The caller opens <paramref name="audio"/> — a fresh source, because the one the
    /// transcript came from has been read to its end.
    /// </summary>
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
        var turns = options.DisplayNameFormat is { } format
            ? SpeakerTurns.RenameByFirstAppearance(raw, format)
            : raw;

        return document with
        {
            Segments = SpeakerAssignment.Apply(document.Segments, turns, options),
            SpeakerTurns = turns,
            SpeakerModelId = labeller.Capabilities.ModelId,
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
}
