using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>One rendered subtitle cue: a time range and the lines to display.</summary>
public sealed record SubtitleCue
{
    public required TimeSpan Start { get; init; }

    public required TimeSpan End { get; init; }

    public required IReadOnlyList<string> Lines { get; init; }

    public string Text => string.Join(" ", Lines);
}

public sealed record SubtitleOptions
{
    public static SubtitleOptions Default { get; } = new();

    /// <summary>Characters per line before wrapping. 42 is the usual broadcast limit.</summary>
    public int MaxLineLength { get; init; } = 42;

    public int MaxLines { get; init; } = 2;

    /// <summary>A cue longer than this is split even if it would fit on the lines.</summary>
    public TimeSpan MaxCueDuration { get; init; } = TimeSpan.FromSeconds(7);

    /// <summary>A cue shorter than this is extended, never past the following cue.</summary>
    public TimeSpan MinCueDuration { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Gap left between adjacent cues so players do not show them as one.</summary>
    public TimeSpan CueGap { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// A cue split off the end of a longer run must carry at least this many characters.
    /// </summary>
    /// <remarks>
    /// Greedy filling leaves widows: a segment of 167 characters at a capacity of 84 splits into
    /// 79, 82 and then a lone <c>"thing."</c> flashing on screen by itself. A segment that is
    /// genuinely that short — <c>"Mm-hmm."</c>, <c>"Um"</c> — is left alone, because it is a real
    /// utterance rather than a leftover.
    /// </remarks>
    public int MinTailCharacters { get; init; } = 16;

    public int Capacity => Math.Max(1, MaxLineLength * MaxLines);

    public void Validate()
    {
        if (MaxLineLength < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxLineLength), MaxLineLength, "Line length must be at least 8 characters.");
        }

        if (MaxLines < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxLines), MaxLines, "A cue needs at least one line.");
        }

        if (MaxCueDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCueDuration), MaxCueDuration, "Cue duration must be positive.");
        }
    }
}

/// <summary>
/// Turns transcript segments into readable subtitle cues.
/// </summary>
/// <remarks>
/// Word timestamps are used when the engine supplied them. When it did not, the segment is
/// still split for readability and each piece is timed by its share of the characters —
/// approximate, but a single 30-second wall of text is not a subtitle, and dropping the
/// text entirely is worse than approximate timing.
/// </remarks>
public static class SubtitleCueBuilder
{
    public static IReadOnlyList<SubtitleCue> Build(
        IEnumerable<TranscriptSegment> segments,
        SubtitleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        options ??= SubtitleOptions.Default;
        options.Validate();

        var cues = new List<SubtitleCue>();
        foreach (var segment in segments)
        {
            if (segment.IsEmpty)
            {
                continue;
            }

            if (segment.Words.Count > 0)
            {
                AppendWordTimedCues(cues, segment, options);
            }
            else
            {
                AppendProportionalCues(cues, segment, options);
            }
        }

        return Tidy(cues, options);
    }

    private static void AppendWordTimedCues(List<SubtitleCue> cues, TranscriptSegment segment, SubtitleOptions options)
    {
        var groups = new List<List<TranscriptWord>>();
        var pending = new List<TranscriptWord>();
        var pendingLength = 0;

        foreach (var word in segment.Words)
        {
            var wordText = word.Text.Trim();
            if (wordText.Length == 0)
            {
                continue;
            }

            var addedLength = pending.Count == 0 ? wordText.Length : pendingLength + 1 + wordText.Length;
            var wouldOverflowText = addedLength > options.Capacity;
            var wouldOverflowTime = pending.Count > 0 && word.End - pending[0].Start > options.MaxCueDuration;

            if (pending.Count > 0 && (wouldOverflowText || wouldOverflowTime))
            {
                groups.Add(pending);
                pending = [];
                pendingLength = 0;
                addedLength = wordText.Length;
            }

            pending.Add(word);
            pendingLength = addedLength;
        }

        if (pending.Count > 0)
        {
            groups.Add(pending);
        }

        RebalanceTail(groups, options);

        foreach (var group in groups)
        {
            cues.Add(CueFromWords(group, options));
        }
    }

    /// <summary>
    /// Evens out the last two cues of a segment when greedy filling has stranded a word or two on
    /// its own. Only ever touches a tail that came from a split, never a segment that was short to
    /// begin with.
    /// </summary>
    private static void RebalanceTail(List<List<TranscriptWord>> groups, SubtitleOptions options)
    {
        if (groups.Count < 2)
        {
            return;
        }

        if (TextLength(groups[^1]) >= options.MinTailCharacters)
        {
            return;
        }

        var combined = new List<TranscriptWord>(groups[^2]);
        combined.AddRange(groups[^1]);

        var best = -1;
        var bestCost = int.MaxValue;

        for (var split = 1; split < combined.Count; split++)
        {
            var head = TextLength(combined, 0, split);
            var tail = TextLength(combined, split, combined.Count);

            // Both halves must still fit a cue, and the tail must clear the widow threshold.
            if (head > options.Capacity || tail > options.Capacity || tail < options.MinTailCharacters)
            {
                continue;
            }

            // And both must still respect the duration cap. Checking only characters is not
            // enough: words either side of a long pause are cheap in characters and expensive in
            // seconds, so a purely textual rebalance can span a silence and park a subtitle on
            // screen for twelve seconds to save a one-word widow. That trade is the wrong way
            // round — an over-long cue is worse than a short one.
            if (Span(combined, 0, split) > options.MaxCueDuration
                || Span(combined, split, combined.Count) > options.MaxCueDuration)
            {
                continue;
            }

            var cost = Math.Abs(head - tail);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = split;
            }
        }

        if (best < 0)
        {
            // No legal rebalance — a word longer than the capacity, or a pause that no split can
            // straddle within the duration cap. Leaving the widow is the better outcome.
            return;
        }

        groups[^2] = combined[..best];
        groups[^1] = combined[best..];
    }

    private static TimeSpan Span(List<TranscriptWord> words, int from, int to) =>
        to <= from ? TimeSpan.Zero : words[to - 1].End - words[from].Start;

    private static int TextLength(List<TranscriptWord> words) => TextLength(words, 0, words.Count);

    private static int TextLength(List<TranscriptWord> words, int from, int to)
    {
        var length = 0;
        for (var i = from; i < to; i++)
        {
            var word = words[i].Text.Trim().Length;
            length += length == 0 ? word : word + 1;
        }

        return length;
    }

    private static SubtitleCue CueFromWords(List<TranscriptWord> words, SubtitleOptions options)
    {
        var text = string.Join(" ", words.Select(w => w.Text.Trim()).Where(t => t.Length > 0));
        var start = words[0].Start;
        var end = words[^1].End;

        // A zero-length or inverted range comes from an engine that reported the same frame
        // for start and end. Give it a nominal duration rather than emitting an invalid cue.
        if (end <= start)
        {
            end = start + options.MinCueDuration;
        }

        return new SubtitleCue { Start = start, End = end, Lines = WrapLines(text, options) };
    }

    private static void AppendProportionalCues(List<SubtitleCue> cues, TranscriptSegment segment, SubtitleOptions options)
    {
        var chunks = SplitByCapacity(segment.Text, options.Capacity);
        if (chunks.Count == 0)
        {
            return;
        }

        var totalCharacters = chunks.Sum(c => c.Length);
        if (totalCharacters == 0)
        {
            return;
        }

        var cursor = segment.Start;
        var duration = segment.Duration > TimeSpan.Zero ? segment.Duration : options.MinCueDuration;

        for (var i = 0; i < chunks.Count; i++)
        {
            var share = chunks[i].Length / (double)totalCharacters;
            var slice = i == chunks.Count - 1
                ? segment.Start + duration - cursor
                : TimeSpan.FromTicks((long)(duration.Ticks * share));

            if (slice <= TimeSpan.Zero)
            {
                slice = options.MinCueDuration;
            }

            cues.Add(new SubtitleCue
            {
                Start = cursor,
                End = cursor + slice,
                Lines = WrapLines(chunks[i], options),
            });

            cursor += slice;
        }
    }

    private static List<string> SplitByCapacity(string text, int capacity)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        var length = 0;

        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var added = current.Count == 0 ? word.Length : length + 1 + word.Length;
            if (current.Count > 0 && added > capacity)
            {
                chunks.Add(string.Join(" ", current));
                current.Clear();
                added = word.Length;
            }

            current.Add(word);
            length = added;
        }

        if (current.Count > 0)
        {
            chunks.Add(string.Join(" ", current));
        }

        return chunks;
    }

    /// <summary>
    /// Wraps into at most <see cref="SubtitleOptions.MaxLines"/> lines, balancing the break
    /// point rather than filling greedily — a two-line cue reads better split near the middle.
    /// A word longer than the line limit is left intact: hyphenating a URL or a long German
    /// compound does more damage than an over-long line.
    /// </summary>
    private static List<string> WrapLines(string text, SubtitleOptions options)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [string.Empty];
        }

        if (options.MaxLines == 1 || text.Length <= options.MaxLineLength)
        {
            return [string.Join(" ", words)];
        }

        if (options.MaxLines == 2)
        {
            var best = BalancedSplit(words);
            return best is null
                ? [string.Join(" ", words)]
                : [string.Join(" ", words[..best.Value]), string.Join(" ", words[best.Value..])];
        }

        var lines = new List<string>();
        var current = new List<string>();
        var length = 0;
        foreach (var word in words)
        {
            var added = current.Count == 0 ? word.Length : length + 1 + word.Length;
            if (current.Count > 0 && added > options.MaxLineLength && lines.Count < options.MaxLines - 1)
            {
                lines.Add(string.Join(" ", current));
                current.Clear();
                added = word.Length;
            }

            current.Add(word);
            length = added;
        }

        lines.Add(string.Join(" ", current));
        return lines;
    }

    private static int? BalancedSplit(string[] words)
    {
        if (words.Length < 2)
        {
            return null;
        }

        var total = words.Sum(w => w.Length) + words.Length - 1;
        var target = total / 2.0;

        var bestIndex = 1;
        var bestCost = double.MaxValue;
        var prefix = 0;

        for (var i = 0; i < words.Length - 1; i++)
        {
            prefix += i == 0 ? words[i].Length : words[i].Length + 1;
            var cost = Math.Abs(prefix - target);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestIndex = i + 1;
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// Enforces monotonic, non-overlapping cues. Overlaps make players drop cues silently,
    /// which is exactly the failure mode this codebase refuses to ship.
    /// </summary>
    private static List<SubtitleCue> Tidy(List<SubtitleCue> cues, SubtitleOptions options)
    {
        var tidied = new List<SubtitleCue>(cues.Count);

        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var start = cue.Start < TimeSpan.Zero ? TimeSpan.Zero : cue.Start;

            if (tidied.Count > 0)
            {
                var previousEnd = tidied[^1].End;
                if (start < previousEnd + options.CueGap)
                {
                    start = previousEnd + options.CueGap;
                }
            }

            var end = cue.End;
            if (end - start < options.MinCueDuration)
            {
                end = start + options.MinCueDuration;
            }

            // Never push a cue over the start of the next one.
            if (i + 1 < cues.Count)
            {
                var nextStart = cues[i + 1].Start;
                if (nextStart > start && end > nextStart - options.CueGap)
                {
                    end = nextStart - options.CueGap;
                }
            }

            if (end <= start)
            {
                end = start + TimeSpan.FromMilliseconds(1);
            }

            tidied.Add(cue with { Start = start, End = end });
        }

        return tidied;
    }
}
