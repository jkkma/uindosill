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
    /// Every other character — punctuation, symbols, hyphens and dashes, whitespace — separates
    /// tokens, so <c>year-over-year</c> and <c>year over year</c> agree and <c>17%</c> scores as
    /// <c>17</c>.</item>
    /// <item>The <see cref="Fillers"/> are dropped unless <paramref name="keepFillers"/> is set.</item>
    /// </list>
    /// Not done, and it matters when reading a score: digits against spelled-out numbers
    /// (<c>2022</c> / <c>twenty twenty-two</c>) count as errors, as do British against American
    /// spellings and a contraction against its expansion.
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
        }

        Flush(current, tokens, keepFillers);
        return tokens.ToArray();
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
