using System.Text;
using Parakeet.Core.Text;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Translation;

/// <summary>
/// Finds the numbers a translation lost, so a segment that quietly changed a date or a quantity is
/// flagged rather than left to be believed.
/// </summary>
/// <remarks>
/// <para>
/// Dates and figures are what a listener checks a transcript for, and they are where a cascade of
/// two models meets worst: the recogniser writes a year the way it was said and the translator, not
/// having seen that spelling, produces fluent English with a different number in it or with none.
/// <c>GermanNumberWords</c> repairs one shape of that, in one language, before the translator reads
/// anything. This is the other half and it needs no per-language grammar at all: **if the source
/// carries a numeral and the English does not, say so.** That works for all twenty-four sources,
/// including the twenty-three nobody here has ever put audio through.
/// </para>
/// <para>
/// <b>It compares digits, not text, and the English side is normalised first.</b> A translator that
/// renders <c>12</c> as <i>twelve</i> has not lost anything, and flagging it would bury the real
/// case in false alarms — so the English is put through
/// <see cref="TranscriptNormalizer.WordErrorRateTokens"/>, whose number rule already turns runs of
/// English cardinal words into digits for exactly this reason on the word-error-rate side. Reusing
/// it rather than writing a second one is deliberate: two number rules would be two calibrations to
/// keep in step.
/// </para>
/// <para>
/// <b>Separators are dropped on both sides</b>, because they are the one thing that reliably differs
/// between a source language and English and never carries meaning: German <c>1.000</c> and English
/// <c>1,000</c> both reduce to <c>1000</c>, and German <c>3,2</c> and English <c>3.2</c> both to
/// <c>32</c>. The cost is that <c>3.2</c> and <c>32</c> cannot be told apart, which is the right
/// trade for a flag whose job is to point a human at a segment rather than to score one.
/// </para>
/// <para>
/// It is a <b>flag and not a refusal</b>, and it is one-directional: a number in the English that
/// was not in the source is not reported here. That would be a different defect — invention rather
/// than loss — and it has never been observed, so no rule is written for it.
/// </para>
/// </remarks>
public static class TranslationNumerals
{
    /// <summary>
    /// The numerals <paramref name="source"/> carries that <paramref name="translated"/> does not,
    /// in the order the source has them, counting repeats.
    /// </summary>
    /// <remarks>
    /// <paramref name="source"/> is the text as the caller holds it — the transcript as the engine
    /// wrote it — and it is normalised here by <see cref="GermanNumberWords"/> before its numerals
    /// are read, because that is what <see cref="TranslationRequest.Mark"/> did to it on the way
    /// into the model. Comparing the raw text instead would make the one case this whole pair of
    /// features exists for invisible: <c>neunzehnhundertneunundzwanzig</c> carries no digit, so a
    /// translation that lost the year would have no numeral to be missing.
    /// </remarks>
    public static IReadOnlyList<string> Missing(string source, string translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);

        var wanted = Numerals(GermanNumberWords.ToDigits(source));
        if (wanted.Count == 0)
        {
            return [];
        }

        // A multiset, because "5 people in 5 rooms" losing one of the fives is a loss. Present-or-
        // absent would report nothing.
        var available = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var numeral in Numerals(translated))
        {
            available[numeral] = available.GetValueOrDefault(numeral) + 1;
        }

        var missing = new List<string>();
        foreach (var numeral in wanted)
        {
            if (available.TryGetValue(numeral, out var count) && count > 0)
            {
                available[numeral] = count - 1;
                continue;
            }

            missing.Add(numeral);
        }

        return missing;
    }

    /// <summary>
    /// The sentence a caller owes the user when a translated transcript has lost numbers, naming
    /// the segments and what went missing. Null when nothing did.
    /// </summary>
    /// <param name="source">The transcript as it was before the pass. The caller keeps it.</param>
    /// <param name="translated">The same transcript after it.</param>
    /// <param name="limit">How many segments to name before summarising the rest.</param>
    public static string? Describe(
        IReadOnlyList<TranscriptSegment> source,
        IReadOnlyList<TranscriptSegment> translated,
        int limit = 5)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // A mismatched pair is the driver's business and it refuses one; here it means there is
        // nothing sensible to compare, which is not the same as nothing being wrong.
        if (source.Count != translated.Count)
        {
            return null;
        }

        var hits = new List<(TimeSpan Start, IReadOnlyList<string> Missing)>();
        for (var i = 0; i < source.Count; i++)
        {
            var missing = Missing(source[i].Text, translated[i].Text);
            if (missing.Count > 0)
            {
                hits.Add((source[i].Start, missing));
            }
        }

        if (hits.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append(hits.Count == 1 ? "1 segment carries" : $"{hits.Count} segments carry");
        builder.Append(" a number the English does not: ");

        for (var i = 0; i < Math.Min(limit, hits.Count); i++)
        {
            if (i > 0)
            {
                builder.Append("; ");
            }

            var (start, missing) = hits[i];
            builder.Append(Timestamp(start)).Append(' ').Append(string.Join(", ", missing));
        }

        if (hits.Count > limit)
        {
            builder.Append("; and ").Append(hits.Count - limit).Append(" more");
        }

        builder.Append(". Check them against the audio: a date or a quantity that changed in "
            + "translation reads as confidently as one that did not.");
        return builder.ToString();
    }

    /// <summary>
    /// Every numeral in <paramref name="text"/>, reduced to its digits.
    /// </summary>
    /// <remarks>
    /// Tokenised by the word-error-rate normaliser rather than by a regular expression over the raw
    /// string, because that is what applies the English number-word rule — the whole reason a
    /// translation rendering <c>12</c> as <i>twelve</i> does not show up here. Fillers are kept:
    /// dropping them is a scoring convention and has nothing to do with numbers.
    /// </remarks>
    private static List<string> Numerals(string text)
    {
        var found = new List<string>();
        foreach (var token in TranscriptNormalizer.WordErrorRateTokens(text, keepFillers: true))
        {
            var digits = new StringBuilder(token.Length);
            foreach (var character in token)
            {
                if (char.IsAsciiDigit(character))
                {
                    digits.Append(character);
                }
            }

            if (digits.Length > 0)
            {
                found.Add(digits.ToString());
            }
        }

        return found;
    }

    private static string Timestamp(TimeSpan at) =>
        at.TotalHours >= 1
            ? $"[{(int)at.TotalHours:00}:{at.Minutes:00}:{at.Seconds:00}]"
            : $"[{at.Minutes:00}:{at.Seconds:00}]";
}
