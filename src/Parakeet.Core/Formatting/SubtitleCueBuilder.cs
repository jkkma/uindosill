using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>One rendered subtitle cue: a time range and the lines to display.</summary>
public sealed record SubtitleCue
{
    public required TimeSpan Start { get; init; }

    public required TimeSpan End { get; init; }

    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>
    /// The words behind each line, aligned one-to-one with <see cref="Lines"/>: joining
    /// <c>LineWords[i]</c>'s trimmed texts with single spaces reproduces <c>Lines[i]</c> exactly.
    /// Empty — not a list of empty lines — when the engine reported no word timestamps for the
    /// segment this cue came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the times the model reported, not times derived from <see cref="Start"/> and
    /// <see cref="End"/>, and after <c>Tidy</c> has adjusted a cue for readability they can sit
    /// outside its range. A consumer that needs them inside the range has to enforce that itself;
    /// <c>WordTimedVttFormatter</c> is the worked example.
    /// </para>
    /// <para>
    /// Record equality compares this list by reference — synthesised record equality uses the
    /// default comparer, and for a list that means reference identity. It is a second member with
    /// that property rather than a change in what equality means: <see cref="Lines"/> has always
    /// behaved the same way. Nothing compares cues by equality today.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IReadOnlyList<TranscriptWord>> LineWords { get; init; } = [];

    public string Text => string.Join(" ", Lines);

    /// <summary>Every word of the cue in order, flattened across its lines.</summary>
    public IEnumerable<TranscriptWord> Words => LineWords.SelectMany(line => line);
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
        // One filter, applied once, feeding both the text and the word list. They have to agree
        // token for token or every timestamp lands on the wrong word, and that failure is
        // invisible in the output: each word is still there, each one just lights up early.
        var kept = new List<TranscriptWord>(words.Count);
        var tokens = new List<string>(words.Count);

        foreach (var word in words)
        {
            var token = word.Text.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            kept.Add(word);
            tokens.Add(token);
        }

        var text = string.Join(" ", tokens);
        var start = words[0].Start;
        var end = words[^1].End;

        // A zero-length or inverted range comes from an engine that reported the same frame
        // for start and end. Give it a nominal duration rather than emitting an invalid cue.
        if (end <= start)
        {
            end = start + options.MinCueDuration;
        }

        if (tokens.Count == 0)
        {
            return new SubtitleCue { Start = start, End = end, Lines = [string.Empty] };
        }

        var wrapped = WrapTokens([.. tokens], text.Length, options);
        var lines = new List<string>(wrapped.Count);
        var lineWords = new List<IReadOnlyList<TranscriptWord>>(wrapped.Count);
        var index = 0;

        foreach (var line in wrapped)
        {
            lines.Add(string.Join(" ", line));
            lineWords.Add(kept.GetRange(index, line.Length));
            index += line.Length;
        }

        return new SubtitleCue { Start = start, End = end, Lines = lines, LineWords = lineWords };
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

        return [.. WrapTokens(words, text.Length, options).Select(line => string.Join(" ", line))];
    }

    /// <summary>
    /// The wrap itself, returning each line's tokens rather than its joined string.
    /// </summary>
    /// <param name="length">
    /// Length of the string these tokens came from. Passed in rather than recomputed so this makes
    /// bit-for-bit the decision the string-based wrap has always made. Every caller's text is a
    /// single-space join of exactly these tokens, so the two values are equal — but "provably the
    /// same number" is worth more here than "equal by an argument", because the whole point of
    /// this refactor is that the plain <c>vtt</c> and <c>srt</c> output does not move.
    /// </param>
    /// <remarks>
    /// Splitting the wrap out is what makes word-level timings possible at all. The line contents
    /// used to be recoverable only by re-splitting a finished line on whitespace and trusting that
    /// the tokens still corresponded to the words they came from. They did — but only by
    /// coincidence of two filters agreeing, and nothing would have reported it when they stopped.
    /// </remarks>
    private static List<string[]> WrapTokens(string[] words, int length, SubtitleOptions options)
    {
        if (options.MaxLines == 1 || length <= options.MaxLineLength)
        {
            return [words];
        }

        if (options.MaxLines == 2)
        {
            var best = BalancedSplit(words);
            return best is null ? [words] : [words[..best.Value], words[best.Value..]];
        }

        var lines = new List<string[]>();
        var current = new List<string>();
        var running = 0;
        foreach (var word in words)
        {
            var added = current.Count == 0 ? word.Length : running + 1 + word.Length;
            if (current.Count > 0 && added > options.MaxLineLength && lines.Count < options.MaxLines - 1)
            {
                lines.Add([.. current]);
                current.Clear();
                added = word.Length;
            }

            current.Add(word);
            running = added;
        }

        lines.Add([.. current]);
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

            // A with-expression, so LineWords survives this untouched — deliberately, and it is
            // the trap in the feature rather than a convenience. Start and End here are a
            // readability decision: a 700 ms floor, a 1 ms gap, a clamp off the next cue's start.
            // The word times are what the model reported. After this runs the two can disagree,
            // and a word timestamp can sit outside the cue that carries it.
            //
            // They are not reconciled here, because the fix would have to be to move the word
            // times, and a measurement bent to fit a presentation decision is no longer a
            // measurement. WebVTT's rule — inline timestamps strictly inside the cue, strictly
            // increasing — is enforced where it applies, in WordTimedVttFormatter, which drops a
            // tag it cannot place legally rather than inventing one that fits.
            tidied.Add(cue with { Start = start, End = end });
        }

        return tidied;
    }
}
