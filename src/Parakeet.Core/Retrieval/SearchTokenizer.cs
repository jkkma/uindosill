using System.Text;

namespace Parakeet.Core.Retrieval;

/// <summary>
/// The one definition of "the same text" that retrieval and citation checking share: runs of
/// letters and digits, lower-cased culture-invariantly, everything else a separator. Both sides
/// of every comparison — window against query, quote against cited span — go through here, so
/// <c>year-over-year</c> matches <c>year over year</c> and <c>Hello,</c> matches <c>hello</c>.
/// </summary>
/// <remarks>
/// Deliberately not <c>Text.TranscriptNormalizer</c>: that class defines how word error rates
/// were scored, keeps mid-word apostrophes, rewrites number words and drops fillers — rules that
/// exist to compare a model against a human transcript, not to find a window. Unstemmed on
/// purpose: the register's decision 3 wants stemming's contribution to recall to be a measurement
/// rather than an assumption, so the first index is built without it. Lower-casing is invariant
/// so the machine's locale can never change what retrieval finds.
/// </remarks>
public static class SearchTokenizer
{
    public static IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new List<string>();
        var current = new StringBuilder();
        Span<char> encoded = stackalloc char[2];

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                var lowered = Rune.ToLowerInvariant(rune);
                var length = lowered.EncodeToUtf16(encoded);
                current.Append(encoded[..length]);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Tokens joined by single spaces: the normalised form the quote-substring check compares in.
    /// <c>«Wir haben's gesehen!»</c> and <c>wir haben s gesehen</c> normalise identically.
    /// </summary>
    public static string Normalize(string text) => string.Join(' ', Tokenize(text));
}
