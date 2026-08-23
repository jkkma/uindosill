namespace Parakeet.Core.Transcription;

/// <summary>
/// Cuts a transcript segment into one segment per sentence, where its word timings allow.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A segment is whatever the voice-activity detector cut — up to the segment
/// cap, thirty seconds by default — and on audio with a bed under the speech the energy gate never
/// sees a pause, so a segment runs to the cap holding a dozen sentences. Measured on a broadcast
/// documentary on 2026-08-23: a 29.4 s segment of 389 characters and nine sentences, over a bed
/// sitting at −23 dBFS median against a gate that cannot close above −35, while the model's own word
/// timings showed pauses of 0.96 s, 0.96 s and 1.84 s after sentence-final words inside it. The
/// segment is the unit a citation points at and the unit every recorded figure counts, so the
/// segment itself is not changed; what reads by the sentence is whatever asks this for the pieces —
/// the Ask tab's lines. <c>docs/UNPROVEN.md</c> has the measurement.
/// </para>
/// <para>
/// <b>Every time on a piece is a time the engine reported.</b> A piece starts at its first word and
/// ends at its last, except that the first piece keeps the segment's start and the last keeps the
/// segment's end, so the pieces together span exactly what the segment did — the rule
/// <c>SpeakerAssignment</c> uses when it cuts a segment where the speaker changes, and the rule the
/// Ask tab holds itself to: the window never writes a timestamp of its own. A segment with no word
/// timings cannot be timed apart and is left whole, which is every translated segment; so is a
/// segment whose words, joined by single spaces, do not reproduce its text, because cutting text at
/// a guessed position is the silent failure this pipeline refuses everywhere else.
/// </para>
/// <para>
/// <b>What ends a sentence.</b> A word whose last character — after any closing quote or bracket —
/// is <c>.</c>, <c>!</c>, <c>?</c> or an ellipsis, when the word after it opens with an upper-case
/// letter, a digit, a quote or a bracket. The second half is what keeps <c>bzw. die</c> together.
/// Three shapes are refused outright because their stop is not a sentence's: a single letter
/// (<c>z. B.</c>, <c>u. a.</c>), digits alone (the ordinal in <c>am 3. Oktober</c>, at the cost of
/// <c>seit 1990. Dann</c>) and a word with a stop inside it (<c>z.B.</c>, <c>d.h.</c>, <c>e.g.</c>).
/// What that does not catch is an abbreviation before a capital or a number — <c>Dr. Müller</c>,
/// <c>ca. 40</c>, <c>Nr. 5</c> — which reads as a sentence end; a list of abbreviations per language
/// was declined, because twenty-five languages of them is a second thing to keep right and a wrong
/// cut costs one line break. A digit opens a sentence here because the measurement said so: on the
/// one file this was run against, every full stop the rule declined before a digit — four of its
/// five declines — was a real sentence end, and the <c>ca. 40</c> it would have protected did not
/// occur. The rule was measured on one German documentary and nowhere else: <c>docs/UNPROVEN.md</c>
/// says how often it fired and what it got wrong there.
/// </para>
/// <para>
/// Splitting never merges — it only ever makes segments smaller — and joining the pieces' texts with
/// single spaces gives the segment's text back. A segment nothing cuts comes back as itself, the
/// same object, so a caller can tell "left whole" from "cut into one".
/// </para>
/// </remarks>
public static class SentenceSplitter
{
    /// <summary>Every segment's pieces, in order.</summary>
    public static IReadOnlyList<TranscriptSegment> Split(IEnumerable<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var result = new List<TranscriptSegment>();
        foreach (var segment in segments)
        {
            result.AddRange(Split(segment));
        }

        return result;
    }

    /// <summary>
    /// One segment per sentence <paramref name="segment"/>'s words spell out, or the segment itself
    /// when they spell none apart — no words, words that do not reproduce the text, or one sentence.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Split(TranscriptSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var words = segment.Words;
        if (words.Count < 2 || !segment.WordsReproduceText())
        {
            return [segment];
        }

        var pieces = new List<TranscriptSegment>();
        var runStart = 0;

        for (var i = 0; i < words.Count; i++)
        {
            var last = i == words.Count - 1;
            if (!last && !EndsSentence(words[i].Text, words[i + 1].Text))
            {
                continue;
            }

            if (last && runStart == 0)
            {
                // Nothing cut: one sentence, or none the rule could see. The segment, not a copy.
                return [segment];
            }

            var run = new List<TranscriptWord>(i + 1 - runStart);
            for (var j = runStart; j <= i; j++)
            {
                run.Add(words[j]);
            }

            pieces.Add(segment with
            {
                Start = runStart == 0 ? segment.Start : run[0].Start,
                End = last ? segment.End : run[^1].End,
                Text = string.Join(' ', run.Select(w => w.Text.Trim())),
                Words = run,
            });

            runStart = i + 1;
        }

        return pieces;
    }

    /// <summary>
    /// Whether <paramref name="word"/> closes a sentence that <paramref name="nextWord"/> does not
    /// continue. The rule in the class remarks, exposed so it can be held to its examples.
    /// </summary>
    internal static bool EndsSentence(string word, string nextWord)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(nextWord);

        var token = word.AsSpan().Trim();

        // Judge `done."` and `fertig.«` by the mark in front of the quote.
        while (token.Length > 0 && IsClosing(token[^1]))
        {
            token = token[..^1];
        }

        if (token.Length == 0 || !IsTerminal(token[^1]))
        {
            return false;
        }

        // The word without its marks: "aussieht." is "aussieht", "Naja..." is "Naja".
        var core = token;
        while (core.Length > 0 && IsTerminal(core[^1]))
        {
            core = core[..^1];
        }

        var letters = 0;
        var digits = 0;
        var stopInside = false;
        foreach (var ch in core)
        {
            if (char.IsLetter(ch))
            {
                letters++;
            }
            else if (char.IsDigit(ch))
            {
                digits++;
            }
            else if (ch == '.')
            {
                stopInside = true;
            }
        }

        // Marks alone end nothing; a single letter is an abbreviation; digits alone are an ordinal;
        // a stop inside the word is a z.B. — each refused rather than guessed at.
        if (letters + digits == 0 || letters == 0 || (letters == 1 && digits == 0) || stopInside)
        {
            return false;
        }

        var next = nextWord.AsSpan().Trim();
        while (next.Length > 0 && IsOpening(next[0]))
        {
            next = next[1..];
        }

        // A capital or a number opens the next sentence; a lower-case word continues this one.
        return next.Length > 0 && (char.IsUpper(next[0]) || char.IsDigit(next[0]));
    }

    private static bool IsTerminal(char ch) => ch is '.' or '!' or '?' or '…' or '。' or '！' or '？';

    private static bool IsClosing(char ch) => ch is '"' or '\'' or '”' or '’' or '»' or '«' or ')' or ']' or '）';

    private static bool IsOpening(char ch) => ch is '"' or '\'' or '“' or '‘' or '„' or '«' or '»' or '(' or '[' or '（' or '¿' or '¡';
}
