using System.Globalization;
using System.Text;

namespace Parakeet.Core.Text;

/// <summary>
/// German compound cardinal number words to digits, for the text that goes <i>into</i> a
/// translator.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of one measured failure and a class of failure behind it. The speech
/// recogniser writes numbers the way they are said, so a German speaker's 1929 arrives as
/// <c>neunzehnhundertneunundzwanzig</c> — a twenty-nine-character single word that almost certainly
/// never appeared in the translator's Bible-derived training corpus. It came back as <i>"the
/// nineteenth century"</i>. Neither model is wrong on its own metric: the recogniser wrote what was
/// said and the translator translated what it was given. **Digits survive translation; compound
/// number words do not**, so the cheapest repair is to hand the translator digits.
/// </para>
/// <para>
/// <b>Only compounds, and that is the whole safety argument.</b> A token is rewritten only when it
/// parses <i>completely</i> as a German cardinal <i>and</i> is built from at least two number
/// words. <c>zwei</c>, <c>zwanzig</c>, <c>neunzehn</c> and <c>hundert</c> are single lexical items
/// that translate perfectly well and are left exactly as they are; <c>einundzwanzig</c>,
/// <c>zweihundert</c> and <c>neunzehnhundertneunundzwanzig</c> are compositions and are rewritten.
/// Requiring the whole token to parse is what keeps ordinary words out: <c>Achtung</c> parses
/// <c>acht</c> and then has <c>ung</c> left over, <c>Dreieck</c> has <c>eck</c>, <c>Zweifel</c> has
/// <c>fel</c>, and each is therefore untouched rather than half-converted.
/// </para>
/// <para>
/// <b>It is safe to run without knowing the source language</b>, which matters because nothing in
/// this pipeline knows it — the translator is many-to-one, told the target and never the source. A
/// token that parses end to end as a multi-part German numeral is not a word in the other
/// twenty-three languages, and that is a measurement rather than an assertion: see
/// <c>docs/UNPROVEN.md</c> § <i>Translating into English</i> for what it fired on across all 8,149
/// FLEURS source sentences.
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> Ordinals (<c>neunzehnhundertneunundzwanzigste</c>),
/// decimals (<c>Komma</c>), the scale words that are separate tokens in German
/// (<c>zwei Millionen</c>), and years said as two pairs the way English does them. Each would widen
/// the grammar and therefore the chance of catching a word that is not a number, for cases the
/// recogniser has not been observed to produce.
/// </para>
/// </remarks>
public static class GermanNumberWords
{
    /// <summary>The scale words, which are the joints a compound is built at.</summary>
    private const string Hundred = "hundert";

    private const string Thousand = "tausend";

    private const string And = "und";

    /// <summary>
    /// Units in their in-compound form. <c>ein</c> rather than <c>eins</c>, because that is the
    /// form that appears inside <c>einundzwanzig</c> and <c>einhundert</c>.
    /// </summary>
    private static readonly (string Word, int Value)[] CompoundUnits =
    [
        ("ein", 1), ("zwei", 2), ("drei", 3), ("vier", 4), ("fünf", 5), ("fuenf", 5),
        ("sechs", 6), ("sieben", 7), ("acht", 8), ("neun", 9),
    ];

    /// <summary>
    /// Everything that can stand as a number below one hundred on its own, longest first so a
    /// prefix can never win: <c>dreizehn</c> before <c>drei</c>, <c>achtzig</c> before <c>acht</c>.
    /// </summary>
    /// <remarks>
    /// Bare <c>ein</c> is in this list and bare <c>eine</c>, <c>einer</c>, <c>einem</c> are not:
    /// those are the indefinite article and are among the commonest words in the language. It costs
    /// nothing to include <c>ein</c> here because a one-word parse never reaches two number words
    /// and so is never rewritten.
    /// </remarks>
    private static readonly (string Word, int Value)[] BelowHundred = Order(
    [
        ("zehn", 10), ("elf", 11), ("zwölf", 12), ("zwoelf", 12), ("dreizehn", 13),
        ("vierzehn", 14), ("fünfzehn", 15), ("fuenfzehn", 15), ("sechzehn", 16),
        ("siebzehn", 17), ("achtzehn", 18), ("neunzehn", 19),
        ("zwanzig", 20), ("dreißig", 30), ("dreissig", 30), ("vierzig", 40),
        ("fünfzig", 50), ("fuenfzig", 50), ("sechzig", 60), ("siebzig", 70),
        ("achtzig", 80), ("neunzig", 90),
        ("eins", 1), ("ein", 1), ("zwei", 2), ("drei", 3), ("vier", 4), ("fünf", 5), ("fuenf", 5),
        ("sechs", 6), ("sieben", 7), ("acht", 8), ("neun", 9),
    ]);

    /// <summary>The tens a <c>X und Y</c> compound may end on.</summary>
    private static readonly (string Word, int Value)[] Tens = Order(
    [
        ("zwanzig", 20), ("dreißig", 30), ("dreissig", 30), ("vierzig", 40),
        ("fünfzig", 50), ("fuenfzig", 50), ("sechzig", 60), ("siebzig", 70),
        ("achtzig", 80), ("neunzig", 90),
    ]);

    private static (string Word, int Value)[] Order((string Word, int Value)[] words)
    {
        Array.Sort(words, (left, right) => right.Word.Length.CompareTo(left.Word.Length));
        return words;
    }

    /// <summary>
    /// Rewrites every compound German cardinal in <paramref name="text"/> as digits and returns the
    /// result, or <paramref name="text"/> unchanged when there is nothing to rewrite.
    /// </summary>
    /// <remarks>
    /// Word boundaries are decided by what is not a letter, so punctuation, hyphens and digits all
    /// end a candidate. A token is offered to <see cref="TryParseCompound"/> case-insensitively —
    /// German capitalises sentence-initially and the recogniser capitalises what it likes — and the
    /// replacement is the plain digit string, because that is what survives the translator.
    /// </remarks>
    public static string ToDigits(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder? builder = null;
        var copied = 0;
        var i = 0;

        while (i < text.Length)
        {
            if (!char.IsLetter(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && char.IsLetter(text[i]))
            {
                i++;
            }

            if (!TryParseCompound(text.AsSpan(start, i - start), out var value))
            {
                continue;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, start - copied);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
            copied = i;
        }

        if (builder is null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    /// <summary>
    /// True when <paramref name="token"/> is, in its entirety, a German cardinal built from two or
    /// more number words — and then <paramref name="value"/> is what it means.
    /// </summary>
    /// <remarks>
    /// The two-word floor is the difference between a repair and a rewrite. <c>zwei</c> is a number
    /// the translator handles; <c>zweihundert</c> is a composition it may not have seen. Converting
    /// the first would change text the gate was scored on for no measured benefit, and the whole
    /// argument for running this in the shipping path is that on written text it does nothing.
    /// </remarks>
    public static bool TryParseCompound(ReadOnlySpan<char> token, out long value)
    {
        value = 0;
        if (token.Length < 4)
        {
            // The shortest possible compound is einhundert / zweihundert; nothing under four
            // characters can be two number words, and the guard keeps every short word out cheaply.
            return false;
        }

        Span<char> lowered = token.Length <= 128 ? stackalloc char[token.Length] : new char[token.Length];
        for (var i = 0; i < token.Length; i++)
        {
            lowered[i] = char.ToLowerInvariant(token[i]);
        }

        var position = 0;
        var words = 0;
        long total = 0;
        var any = false;

        // Thousands: <below 1000> "tausend", or a bare "tausend".
        var mark = position;
        var marked = words;
        if (TryBelowThousand(lowered, ref position, ref words, out var thousands)
            && StartsWith(lowered, position, Thousand))
        {
            total += thousands * 1000L;
            position += Thousand.Length;
            words++;
            any = true;
        }
        else
        {
            position = mark;
            words = marked;
            if (StartsWith(lowered, position, Thousand))
            {
                total += 1000L;
                position += Thousand.Length;
                words++;
                any = true;
            }
        }

        // Whatever is left below one thousand.
        if (TryBelowThousand(lowered, ref position, ref words, out var remainder))
        {
            total += remainder;
            any = true;
        }

        if (!any || position != lowered.Length || words < 2)
        {
            return false;
        }

        value = total;
        return true;
    }

    private static bool TryBelowThousand(ReadOnlySpan<char> s, ref int position, ref int words, out long value)
    {
        value = 0;
        long total = 0;
        var any = false;

        var mark = position;
        var marked = words;
        if (TryBelowHundred(s, ref position, ref words, out var hundreds)
            && StartsWith(s, position, Hundred))
        {
            total += hundreds * 100L;
            position += Hundred.Length;
            words++;
            any = true;
        }
        else
        {
            position = mark;
            words = marked;
            if (StartsWith(s, position, Hundred))
            {
                total += 100L;
                position += Hundred.Length;
                words++;
                any = true;
            }
        }

        if (TryBelowHundred(s, ref position, ref words, out var tail))
        {
            total += tail;
            any = true;
        }

        if (!any)
        {
            position = mark;
            words = marked;
            return false;
        }

        value = total;
        return true;
    }

    private static bool TryBelowHundred(ReadOnlySpan<char> s, ref int position, ref int words, out long value)
    {
        value = 0;

        // The X-und-Y form first, because it starts with a unit and a unit would otherwise match
        // and stop: einundzwanzig would read as ein, leaving undzwanzig for nobody.
        foreach (var (word, unit) in CompoundUnits)
        {
            if (!StartsWith(s, position, word) || !StartsWith(s, position + word.Length, And))
            {
                continue;
            }

            var after = position + word.Length + And.Length;
            foreach (var (tensWord, tens) in Tens)
            {
                if (!StartsWith(s, after, tensWord))
                {
                    continue;
                }

                value = unit + tens;
                position = after + tensWord.Length;
                words += 2;
                return true;
            }
        }

        foreach (var (word, number) in BelowHundred)
        {
            if (!StartsWith(s, position, word))
            {
                continue;
            }

            value = number;
            position += word.Length;
            words++;
            return true;
        }

        return false;
    }

    private static bool StartsWith(ReadOnlySpan<char> s, int position, string word) =>
        position >= 0
        && position + word.Length <= s.Length
        && s.Slice(position, word.Length).SequenceEqual(word);
}
