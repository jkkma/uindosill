// Compiled twice, like WordAlignment.cs: by Parakeet.Core, and by `Add-Type -Path` from the
// comparison scripts, which load it from the source tree so that they need no build. Own usings,
// own `#nullable enable`, BCL only, nothing else from Parakeet.Core, conservative syntax.
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Parakeet.Core.Text;

/// <summary>
/// The two ways transcript text is normalised before tokens are compared, each named for what it
/// does so that a number computed under one is never quoted as though computed under the other.
///
/// <para><b>Neither is the normaliser the published leaderboards use.</b> The Open ASR Leaderboard
/// and the Whisper paper score with OpenAI's <c>EnglishTextNormalizer</c>, which additionally
/// expands contractions, spells out or digitises numbers, maps British spellings to American ones
/// and more. A word error rate from <see cref="WordErrorRateTokens"/> is therefore not comparable
/// to a leaderboard figure for the same model, and anything that quotes one must say so. What it is
/// comparable to is another figure computed the same way — which is what the quantisation ladder
/// needs, since the question there is how each variant does against the same reference under the
/// same rules.</para>
/// </summary>
public static class TranscriptNormalizer
{
    /// <summary>
    /// The filler tokens <see cref="WordErrorRateTokens"/> drops by default: the set OpenAI's
    /// English normaliser ignores, copied rather than invented, so that at least this one rule
    /// matches the leaderboards. Human transcripts vary in whether they write these down; the
    /// model writes them when it hears them; counting that as an error would measure the
    /// transcription convention rather than the recognition.
    /// </summary>
    public static IReadOnlyList<string> Fillers { get; } = new[] { "hmm", "mm", "mhm", "mmm", "uh", "um" };

    /// <summary>
    /// The per-token normalisation every divergence figure in <c>docs/UNPROVEN.md</c> was computed
    /// under, unchanged from <c>scripts/word-distance.ps1</c>: lower-cased, with everything that is
    /// not a letter or a digit removed. <c>"Hello,"</c> becomes <c>"hello"</c>; <c>"don't"</c>
    /// becomes <c>"dont"</c>; <c>"—"</c> becomes the empty string. Tokens are never split, so a
    /// hyphenated word stays one token with the hyphen gone.
    /// </summary>
    public static string AlphanumericToken(string token)
    {
        if (token is null) throw new ArgumentNullException(nameof(token));

        var builder = new StringBuilder(token.Length);
        foreach (var ch in token)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// <see cref="AlphanumericToken"/> over a sequence, dropping tokens that normalise to nothing.
    /// The result can be shorter than the input, which is why callers report its count beside the
    /// raw one.
    /// </summary>
    public static string[] AlphanumericTokens(IEnumerable<string> tokens)
    {
        if (tokens is null) throw new ArgumentNullException(nameof(tokens));

        var result = new List<string>();
        foreach (var token in tokens)
        {
            var normalised = AlphanumericToken(token);
            if (normalised.Length > 0)
            {
                result.Add(normalised);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Text to tokens for scoring against a human transcript. The rules, in order, all applied to
    /// both sides alike:
    /// <list type="number">
    /// <item>Anything inside <c>[...]</c>, <c>&lt;...&gt;</c> or <c>(...)</c> is removed — transcriber
    /// annotations such as <c>[inaudible]</c>, not speech.</item>
    /// <item>Lower-cased, culture-invariantly, so the machine's locale cannot change a score.</item>
    /// <item>The typographic apostrophes and primes (<c>’ ‘ ‛ ´ `</c>) become <c>'</c>.</item>
    /// <item>Letters and digits are kept. An apostrophe is kept only between two of them
    /// (<c>don't</c>, <c>o'clock</c>); a full stop is kept only between two digits (<c>3.2</c>); a
    /// comma between two digits is dropped without splitting (<c>1,000</c> becomes <c>1000</c>).
    /// A percent sign becomes the word <c>percent</c>, since that is how it is spoken. Every other
    /// character — punctuation, symbols, hyphens and dashes, whitespace — separates tokens, so
    /// <c>year-over-year</c> and <c>year over year</c> agree.</item>
    /// <item>The <see cref="Fillers"/> are dropped unless <paramref name="keepFillers"/> is set.</item>
    /// <item>Runs of English cardinal number words become digits: <c>eighty seven</c> (or
    /// <c>eighty-seven</c>, split by the rule above) becomes <c>87</c>, <c>two hundred and fifty
    /// two</c> becomes <c>252</c>, <c>three point two million</c> becomes <c>3.2 million</c>.
    /// The model writes numbers as words and human transcripts write them as digits, and without
    /// this the score on any material with numbers in it measures that convention rather than the
    /// recognition. It applies to both sides alike, so where both already agree — <c>five</c>
    /// against <c>five</c>, or <c>2021</c> against <c>2021</c> — nothing changes. <c>and</c> is
    /// absorbed only inside a number that has already passed a hundred or a thousand
    /// (<c>two hundred and one</c>), never between two small numbers (<c>two and three</c>).</item>
    /// </list>
    /// Not done, and it matters when reading a score: a year said as two pairs
    /// (<c>twenty twenty-one</c>) becomes <c>20 21</c>, not <c>2021</c>; ordinals, currency words,
    /// and a bare scale word (<c>a hundred</c>, <c>millions</c>) stay as words; British against
    /// American spellings and a contraction against its expansion count as errors.
    /// </summary>
    public static string[] WordErrorRateTokens(string text, bool keepFillers)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var stripped = RemoveBracketed(text);
        var tokens = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < stripped.Length; i++)
        {
            var ch = stripped[i];
            if (IsApostrophe(ch)) ch = '\'';

            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            var previous = i > 0 ? stripped[i - 1] : '\0';
            var next = i + 1 < stripped.Length ? stripped[i + 1] : '\0';

            if (ch == '\'' && char.IsLetterOrDigit(previous) && char.IsLetterOrDigit(next))
            {
                current.Append('\'');
                continue;
            }

            if (ch == '.' && char.IsDigit(previous) && char.IsDigit(next))
            {
                current.Append('.');
                continue;
            }

            if (ch == ',' && char.IsDigit(previous) && char.IsDigit(next))
            {
                continue;
            }

            Flush(current, tokens, keepFillers);

            if (ch == '%')
            {
                tokens.Add("percent");
            }
        }

        Flush(current, tokens, keepFillers);
        return NumberWords.ToDigits(tokens);
    }

    private static void Flush(StringBuilder current, List<string> tokens, bool keepFillers)
    {
        if (current.Length == 0) return;

        var token = current.ToString();
        current.Clear();

        if (!keepFillers && IsFiller(token)) return;
        tokens.Add(token);
    }

    private static bool IsFiller(string token)
    {
        for (var i = 0; i < Fillers.Count; i++)
        {
            if (string.Equals(Fillers[i], token, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool IsApostrophe(char ch) =>
        ch == '’' || ch == '‘' || ch == '‛' || ch == '´' || ch == '`';

    /// <summary>
    /// Text to characters for scoring a language written without spaces between words, where
    /// <see cref="WordErrorRateTokens"/> has no denominator to work with. The rules, in order,
    /// applied to both sides alike:
    /// <list type="number">
    /// <item>Anything inside ASCII <c>[...]</c>, <c>&lt;...&gt;</c> or <c>(...)</c> is removed, as
    /// in the word rule — transcriber annotations, not speech.</item>
    /// <item>NFKC. This folds the width and compatibility distinctions that are a writing
    /// convention rather than a recognition difference: <c>１２３</c> becomes <c>123</c> and
    /// <c>ｶﾀｶﾅ</c> becomes <c>カタカナ</c>, which the word rule does not do and which cost real
    /// errors when it was measured.</item>
    /// <item>Whitespace is dropped entirely rather than kept as a separator, which is what the
    /// published Japanese recipes do and is the only choice that makes a score independent of
    /// whether a model emits spaces at all.</item>
    /// <item>Unless <paramref name="keepPunctuation"/> is set, anything that is not a letter or a
    /// digit is dropped — punctuation, symbols and marks alike.</item>
    /// <item>Lower-cased culture-invariantly, so the machine's locale cannot change a score and
    /// so Latin text embedded in Japanese is treated as the word rule treats it.</item>
    /// </list>
    ///
    /// <para><b>Runs are enumerated as <see cref="Rune"/>, not <c>char</c>, and that is the
    /// point.</b> <c>char.IsLetterOrDigit</c> is false on both surrogates of a non-BMP character,
    /// so the word rule deletes <c>𠮟</c> (U+20B9F) and splits the word around it — 彼を𠮟った
    /// tokenises to 彼を and った. 𠮟 and 𠮷 are surname characters, so that is a real defect and
    /// not a curiosity.</para>
    ///
    /// <para><b>The ordering of steps 1 and 2 is deliberate and reversing it loses words.</b>
    /// Annotation stripping matches ASCII brackets only and runs first, because NFKC turns the
    /// full-width <c>（）</c> that Japanese uses as ordinary punctuation in running text into
    /// ASCII parentheses. Normalising first would see 聖域（神社）を and delete 神社 as though it
    /// were a transcriber's note.</para>
    ///
    /// <para><b>This is not any published recipe, and a figure from it is not comparable to a
    /// published one.</b> NVIDIA's Japanese card strips punctuation and expands numbers with
    /// <c>num2words</c>; kotoba-whisper runs <c>BasicTextNormalizer</c> and then deletes spaces;
    /// Reazon publishes no recipe at all. Two labs scoring the same <c>whisper-large-v3</c> differ
    /// by 0.08 on JSUT and 0.32 on Common Voice 8.0 for reasons of recipe alone. What a figure from
    /// here is comparable to is another figure computed the same way, which is what a quantisation
    /// ladder or a backend comparison needs. Anything quoting one must say which recipe it used.
    /// </para>
    ///
    /// <para>Not done, and it matters when reading a score: hiragana and katakana are not folded
    /// to one another and a kanji spelling is not folded to its kana spelling, so 顎 against あご
    /// and わけです against 訳です each count as errors. NFKC cannot fold either. The published
    /// headroom there is 2.4 to 3.1 absolute points (Karita, Sproat and Ishikawa, CAWL 2023,
    /// arXiv:2306.04530), and this project has not measured it.</para>
    /// </summary>
    public static string[] CharacterErrorRateTokens(string text, bool keepPunctuation)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        var stripped = RemoveBracketed(text).Normalize(NormalizationForm.FormKC);
        var tokens = new List<string>();

        foreach (var rune in stripped.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            if (!keepPunctuation && !Rune.IsLetterOrDigit(rune)) continue;

            tokens.Add(Rune.ToLowerInvariant(rune).ToString());
        }

        return tokens.ToArray();
    }

    /// <summary>
    /// English cardinal number words to digits, over an already-tokenised, lower-cased sequence.
    /// Deliberately small: units, teens, tens, hundred, and the thousand/million/billion/trillion
    /// scales, <c>point</c> followed by digit words, and <c>and</c> inside a number that has
    /// passed a scale. Anything it does not recognise ends the number and passes through
    /// untouched, so a token that is not a number word can never be changed.
    /// </summary>
    internal static class NumberWords
    {
        private static readonly Dictionary<string, int> Small = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
            ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11,
            ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
            ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
            ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60,
            ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        };

        private static readonly Dictionary<string, long> Scales = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["thousand"] = 1_000L, ["million"] = 1_000_000L, ["billion"] = 1_000_000_000L, ["trillion"] = 1_000_000_000_000L,
        };

        public static string[] ToDigits(List<string> tokens)
        {
            var output = new List<string>(tokens.Count);
            var i = 0;
            while (i < tokens.Count)
            {
                if (!StartsNumber(tokens[i]))
                {
                    output.Add(tokens[i]);
                    i++;
                    continue;
                }

                var end = ParseNumber(tokens, i, out var rendered);
                if (end == i)
                {
                    output.Add(tokens[i]);
                    i++;
                    continue;
                }

                output.Add(rendered);
                i = end;
            }

            return output.ToArray();
        }

        private static bool StartsNumber(string token) =>
            Small.ContainsKey(token) || token == "hundred" || Scales.ContainsKey(token);

        /// <summary>
        /// Reads the longest run of number words from <paramref name="start"/>, returns the index
        /// after it, and renders it. Returns <paramref name="start"/> itself when the run is not a
        /// number after all.
        /// </summary>
        private static int ParseNumber(List<string> tokens, int start, out string rendered)
        {
            long total = 0;      // completed scale groups
            long current = 0;    // the group being built, below the next scale word
            var sawScale = false; // hundred or larger has been applied in this run
            var lastWasSmall = false;
            var lastSmall = 0;
            var i = start;
            var anything = false;

            while (i < tokens.Count)
            {
                var token = tokens[i];

                if (Small.TryGetValue(token, out var small))
                {
                    // Two small numbers in a row only chain as tens + units ("twenty one"). Anything
                    // else ("twenty twenty", "five six") is two numbers, and this one ends here.
                    if (lastWasSmall && !(lastSmall >= 20 && lastSmall % 10 == 0 && small >= 1 && small <= 9))
                    {
                        break;
                    }

                    // "zero" only stands alone: "zero five" is two numbers, not 5.
                    if (anything && small == 0)
                    {
                        break;
                    }

                    current += small;
                    lastWasSmall = true;
                    lastSmall = small;
                    anything = true;
                    i++;
                    continue;
                }

                if (token == "hundred")
                {
                    // A scale word with no number in front of it — "a hundred", "3.2 million",
                    // "hundreds" is not even in the table — is left as the word it is. Multiplying
                    // an implied one would turn "3.2 million" into "3.2 1000000" and put the
                    // number word style back on the other side.
                    if (current == 0 || current >= 100) break;
                    current *= 100;
                    sawScale = true;
                    lastWasSmall = false;
                    anything = true;
                    i++;
                    continue;
                }

                if (Scales.TryGetValue(token, out var scale))
                {
                    if (current == 0) break;

                    total += current * scale;
                    current = 0;
                    sawScale = true;
                    lastWasSmall = false;
                    anything = true;
                    i++;
                    continue;
                }

                if (token == "and" && anything && sawScale && i + 1 < tokens.Count && Small.ContainsKey(tokens[i + 1]))
                {
                    lastWasSmall = false;
                    i++;
                    continue;
                }

                if (token == "point" && anything && i + 1 < tokens.Count && IsDigitWord(tokens[i + 1]))
                {
                    var fraction = new StringBuilder();
                    var j = i + 1;
                    while (j < tokens.Count && IsDigitWord(tokens[j]))
                    {
                        fraction.Append((char)('0' + Small[tokens[j]]));
                        j++;
                    }

                    rendered = (total + current).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + fraction;
                    return j;
                }

                break;
            }

            if (!anything)
            {
                rendered = string.Empty;
                return start;
            }

            rendered = (total + current).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return i;
        }

        private static bool IsDigitWord(string token) => Small.TryGetValue(token, out var value) && value <= 9;
    }

    /// <summary>
    /// Drops <c>[...]</c>, <c>&lt;...&gt;</c> and <c>(...)</c> spans. An opener with no closer is
    /// left alone, so a stray bracket in real speech cannot swallow the rest of the transcript.
    /// </summary>
    private static string RemoveBracketed(string text)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            var closer = ch switch
            {
                '[' => ']',
                '<' => '>',
                '(' => ')',
                _ => '\0',
            };

            if (closer != '\0')
            {
                var end = text.IndexOf(closer, i + 1);
                if (end >= 0)
                {
                    // Removing the span outright would fuse the words either side of it; a space
                    // keeps them apart and is discarded by tokenisation anyway.
                    builder.Append(' ');
                    i = end + 1;
                    continue;
                }
            }

            builder.Append(ch);
            i++;
        }

        return builder.ToString();
    }
}
