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

    /// <summary>
    /// The one speaker this cue belongs to, or null when speakers were not labelled. A cue never
    /// carries two: the builder cuts a cue where the speaker changes, and the subtitle formatters
    /// print this once, in front of the first line.
    /// </summary>
    public string? Speaker { get; init; }

    public string Text => string.Join(" ", Lines);

    /// <summary>Every word of the cue in order, flattened across its lines.</summary>
    public IEnumerable<TranscriptWord> Words => LineWords.SelectMany(line => line);
}

public sealed record SubtitleOptions
{
    public static SubtitleOptions Default { get; } = new();

    /// <summary>Characters per line before wrapping. 42 is the usual broadcast limit.</summary>
    public int MaxLineLength { get; init; } = 42;

    /// <summary>
    /// Full-width characters per line for a script written without spaces between words — Japanese,
    /// Chinese, Korean. Applies only to text <see cref="CjkLineBreaking.ContainsBreakableScript"/>
    /// recognises; everything else uses <see cref="MaxLineLength"/> and is untouched by this.
    ///
    /// <para><b>13 is Netflix's number, not a measured universal.</b> Their Japanese Timed Text
    /// Style Guide specifies 13 full-width characters per line horizontally, two lines, and four
    /// characters per second. It is a defensible default and it is exposed here precisely because
    /// it is somebody's house style: no NHK or ARIB guideline was obtained, and this project has
    /// measured nothing about Japanese subtitle readability. `docs/UNPROVEN.md` says so.</para>
    ///
    /// <para>A half-width character counts half of one of these, which is what the guide means by
    /// counting them as 0.5 — the breaker works in columns, two per full-width character, so this
    /// limit is doubled on the way in.</para>
    /// </summary>
    public int MaxFullWidthCharactersPerLine { get; init; } = 13;

    /// <summary>Columns a line of a space-less script may occupy, two per full-width character.</summary>
    internal int MaxCjkColumns => Math.Max(2, MaxFullWidthCharactersPerLine * 2);

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

    /// <summary>
    /// How a labelled cue names its speaker, in front of its first line: <c>{0}</c> is the label.
    /// Plain text rather than WebVTT's <c>&lt;v&gt;</c> voice span, on purpose — the span carries a
    /// name for styling and nothing renders it as text without author CSS, while a prefix is
    /// visible in every player and editor, and SubRip has no voice markup at all.
    /// </summary>
    public string SpeakerPrefixFormat { get; init; } = "{0}: ";

    public int Capacity => Math.Max(1, MaxLineLength * MaxLines);

    /// <summary>The prefix a cue for <paramref name="speaker"/> carries, or empty when there is none.</summary>
    public string SpeakerPrefix(string? speaker) =>
        speaker is null ? string.Empty : string.Format(System.Globalization.CultureInfo.InvariantCulture, SpeakerPrefixFormat, speaker);

    /// <summary>
    /// The characters left for words once the speaker prefix has taken its share of the cue.
    /// Never below one line's worth: a very long name still leaves room to say something.
    /// </summary>
    internal int CapacityFor(string? speaker) =>
        speaker is null ? Capacity : Math.Max(MaxLineLength, Capacity - SpeakerPrefix(speaker).Length);

    /// <summary>
    /// <see cref="CapacityFor(string?)"/> in the unit the text is actually measured in: columns for
    /// a space-less script, characters for everything else. The two are not interchangeable — 84
    /// characters and 84 columns are different amounts of Japanese — so the caller that splits text
    /// asks with the text in hand rather than assuming.
    /// </summary>
    internal int CapacityFor(string? speaker, string text)
    {
        if (!CjkLineBreaking.ContainsBreakableScript(text))
        {
            return CapacityFor(speaker);
        }

        var full = MaxCjkColumns * MaxLines;
        return speaker is null
            ? full
            : Math.Max(MaxCjkColumns, full - CjkLineBreaking.Columns(SpeakerPrefix(speaker)));
    }

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

        if (MaxFullWidthCharactersPerLine < 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxFullWidthCharactersPerLine),
                MaxFullWidthCharactersPerLine,
                "A line of a space-less script needs at least four full-width characters.");
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
/// <para>
/// Word timestamps are used when the engine supplied them. When it did not, the segment is
/// still split for readability and each piece is timed by its share of the characters —
/// approximate, but a single 30-second wall of text is not a subtitle, and dropping the
/// text entirely is worse than approximate timing.
/// </para>
/// <para>
/// When words carry speakers, a cue is cut wherever the speaker changes and never rebalanced
/// across that cut, so a name in front of a cue is true of every word in it. The prefix is charged
/// against the cue's character capacity, so a labelled cue holds fewer words than an unlabelled
/// one of the same length; it is not charged against the first line's balance point, so a first
/// line can run past the line limit by the width of the name. Documents without speakers take
/// exactly the path they always did.
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

            if (segment.Words.Count > 0 && !WordsAreTooCoarseToCutWith(segment))
            {
                AppendWordTimedCues(cues, segment, options);
            }
            else
            {
                AppendProportionalCues(cues, segment, options);
            }
        }

        return StripTrailingStops(Tidy(cues, options));
    }

    /// <summary>
    /// Takes the sentence-final full stop off the end of every cue — the last line, and the last
    /// word of <see cref="SubtitleCue.LineWords"/> with it, so the word-timed VTT writes the same
    /// text the plain one does. Asked for on 2026-08-23; <see cref="TrailingStop"/> has the rule.
    /// On the finished cue and never on the segment, because the segment's text is what the word
    /// times were located in; a stop inside a cue, between two sentences, stays.
    /// </summary>
    private static IReadOnlyList<SubtitleCue> StripTrailingStops(IReadOnlyList<SubtitleCue> cues)
    {
        var result = new List<SubtitleCue>(cues.Count);

        foreach (var cue in cues)
        {
            if (cue.Lines.Count == 0)
            {
                result.Add(cue);
                continue;
            }

            var lastLine = cue.Lines[^1];
            var stripped = TrailingStop.Strip(lastLine);
            if (ReferenceEquals(stripped, lastLine))
            {
                result.Add(cue);
                continue;
            }

            var lines = new List<string>(cue.Lines);
            lines[^1] = stripped;

            var lineWords = cue.LineWords;
            if (lineWords.Count > 0 && lineWords[^1].Count > 0)
            {
                var lastWords = new List<TranscriptWord>(lineWords[^1]);
                lastWords[^1] = TrailingStop.Strip(lastWords[^1]);
                var all = new List<IReadOnlyList<TranscriptWord>>(lineWords);
                all[^1] = lastWords;
                lineWords = all;
            }

            result.Add(cue with { Lines = lines, LineWords = lineWords });
        }

        return result;
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
            var wouldOverflowText = addedLength > options.CapacityFor(word.Speaker);
            var wouldOverflowTime = pending.Count > 0 && word.End - pending[0].Start > options.MaxCueDuration;

            // A cue names one speaker, so it ends where the speaker does — before any other rule
            // gets a say. Both null is the same speaker: an unlabelled document never breaks here.
            var speakerChanged = pending.Count > 0 && !string.Equals(word.Speaker, pending[0].Speaker, StringComparison.Ordinal);

            if (pending.Count > 0 && (wouldOverflowText || wouldOverflowTime || speakerChanged))
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
    /// <remarks>
    /// With speakers, "a segment" is each run of consecutive same-speaker groups: the tail of every
    /// such run is rebalanced within the run, and words are never traded across a speaker change —
    /// a short last cue that is somebody else's whole utterance is not a widow, and moving words
    /// into it would put them under the wrong name. Without speakers there is one run, and this is
    /// exactly what it always did.
    /// </remarks>
    private static void RebalanceTail(List<List<TranscriptWord>> groups, SubtitleOptions options)
    {
        var end = groups.Count;
        while (end >= 2)
        {
            var start = end - 1;
            while (start > 0 && string.Equals(groups[start - 1][0].Speaker, groups[end - 1][0].Speaker, StringComparison.Ordinal))
            {
                start--;
            }

            if (end - start >= 2)
            {
                RebalancePair(groups, end - 1, options);
            }

            end = start;
        }
    }

    /// <summary>Rebalances <c>groups[last - 1]</c> and <c>groups[last]</c>, which share a speaker.</summary>
    private static void RebalancePair(List<List<TranscriptWord>> groups, int last, SubtitleOptions options)
    {
        if (TextLength(groups[last]) >= options.MinTailCharacters)
        {
            return;
        }

        var combined = new List<TranscriptWord>(groups[last - 1]);
        combined.AddRange(groups[last]);
        var capacity = options.CapacityFor(combined[0].Speaker);

        var best = -1;
        var bestCost = int.MaxValue;

        for (var split = 1; split < combined.Count; split++)
        {
            var head = TextLength(combined, 0, split);
            var tail = TextLength(combined, split, combined.Count);

            // Both halves must still fit a cue, and the tail must clear the widow threshold.
            if (head > capacity || tail > capacity || tail < options.MinTailCharacters)
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

        groups[last - 1] = combined[..best];
        groups[last] = combined[best..];
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

        // Every word in the group carries the same speaker: the grouping above cuts on a change.
        var speaker = words[0].Speaker;

        if (tokens.Count == 0)
        {
            return new SubtitleCue { Start = start, End = end, Lines = [string.Empty], Speaker = speaker };
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

        return new SubtitleCue { Start = start, End = end, Lines = lines, LineWords = lineWords, Speaker = speaker };
    }

    private static void AppendProportionalCues(List<SubtitleCue> cues, TranscriptSegment segment, SubtitleOptions options)
    {
        var duration = segment.Duration > TimeSpan.Zero ? segment.Duration : options.MinCueDuration;
        var chunks = SplitByCapacityAndTime(segment.Text, options.CapacityFor(segment.Speaker, segment.Text), duration, options.MaxCueDuration);
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
                Speaker = segment.Speaker,
            });

            cursor += slice;
        }
    }

    /// <summary>
    /// Splits by character capacity, then tightens the capacity until no chunk's share of the
    /// segment's duration is past the cap. Time on this path is proportional to characters, so a
    /// chunk's duration is its length over the total times the segment's, and the cap bounds the
    /// length; a word is the unit, so one longer than the bound stands alone and runs over, which
    /// is the concession the word-timed path makes to a single very long word too.
    /// </summary>
    /// <remarks>
    /// Until 2026-08-22 this path split by characters only, so every cue of a segment that arrived
    /// without word timings — which is every cue of a translated subtitle — spanned as much of the
    /// segment as its text did: a 26 s cue out of a 30 s segment, against a documented 7 s cap that
    /// only the word-timed path enforced.
    /// </remarks>
    private static List<string> SplitByCapacityAndTime(string text, int capacity, TimeSpan duration, TimeSpan maxCue)
    {
        var chunks = SplitByCapacity(text, capacity);
        if (duration <= maxCue)
        {
            return chunks;
        }

        while (capacity > 1)
        {
            var total = chunks.Sum(c => c.Length);
            if (total == 0)
            {
                return chunks;
            }

            var longest = chunks.Max(c => c.Length);
            if ((long)(duration.Ticks * (longest / (double)total)) <= maxCue.Ticks)
            {
                return chunks;
            }

            // The length the cap allows, and strictly less than before so the loop cannot stall on a
            // single word longer than any capacity.
            var bound = (int)Math.Floor(total * (maxCue.Ticks / (double)duration.Ticks));
            capacity = Math.Min(capacity - 1, Math.Max(1, Math.Min(bound, longest - 1)));
            chunks = SplitByCapacity(text, capacity);
        }

        return chunks;
    }

    private static List<string> SplitByCapacity(string text, int capacity)
    {
        // Same reason as WrapLines: a space-less script offers this loop no word to break at, so
        // one chunk comes back however long it is and every cue built from it overflows. Cut it
        // between characters instead, to whatever a full cue of lines can hold.
        if (CjkLineBreaking.ContainsBreakableScript(text))
        {
            return CjkLineBreaking.SplitByColumns(text, capacity);
        }

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
    /// <summary>
    /// True when a segment's words are too coarse to cut a cue with — one "word" carrying a whole
    /// sentence of a script that has no spaces in it.
    ///
    /// <para><b>Why this is not a workaround.</b> The word path exists to cut cues at word
    /// boundaries and to hand the word-timed writer a word per span. On Japanese,
    /// <c>parakeet.cpp</c>'s <c>group_words</c> finds no boundary at all — measured 2026-09-04, six
    /// segments produced six "words", each the entire sentence, because the rule starts a word only
    /// at a SentencePiece meta-space and 30 of that model's 3,072 pieces carry one. A word that is
    /// the sentence is a segment-level timing wearing a word's name, and feeding it to the word
    /// path yields one cue, one line, and one span over eleven seconds.</para>
    ///
    /// <para>So it goes to the proportional path, which is this project's existing and honest
    /// handling of exactly this situation: split the text by capacity, time each piece by its share
    /// of the characters, and emit <b>no</b> word timings rather than character-share timings
    /// dressed up as measured ones. The word-timed writer already renders such a cue as plain
    /// text, so stripping its tags still reproduces the plain <c>vtt</c> byte for byte.</para>
    ///
    /// <para>The test is deliberately narrow — a space-less script, and a single word covering
    /// substantially the whole segment. A model that does report Japanese word boundaries produces
    /// more than one word and keeps the word path, which is the outcome to want.</para>
    /// </summary>
    private static bool WordsAreTooCoarseToCutWith(TranscriptSegment segment)
    {
        if (segment.Words.Count != 1) return false;
        if (!CjkLineBreaking.ContainsBreakableScript(segment.Text)) return false;

        var word = segment.Words[0].Text.Trim();
        return word.Length * 2 >= segment.Text.Trim().Length;
    }

    private static List<string> WrapLines(string text, SubtitleOptions options)
    {
        // A script with no spaces between its words has nothing for the token wrap below to break
        // at, so the whole line comes back whole however long it is. Break it between characters
        // instead, under the kinsoku rules. Text without such a character never reaches this and
        // takes the path it always took.
        if (CjkLineBreaking.ContainsBreakableScript(text))
        {
            return CjkLineBreaking.Wrap(text, options.MaxCjkColumns, options.MaxLines);
        }

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
