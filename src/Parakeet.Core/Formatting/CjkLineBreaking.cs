using System.Text;

namespace Parakeet.Core.Formatting;

/// <summary>
/// Line breaking for scripts written without spaces between words — Japanese first, and Chinese
/// and Korean by the same rules where they apply.
///
/// <para><b>Why the subtitle writers need this at all.</b> Every wrap in
/// <see cref="SubtitleCueBuilder"/> breaks at a space, because a Latin line has nowhere else to
/// break. A Japanese line has no spaces, so it is one token and comes back unwrapped: measured on
/// 2026-09-04, a 74-character sentence was written as a single SubRip line running 11.52 seconds.
/// A player shows that as one long row off the edge of the frame.</para>
///
/// <para><b>Width is not character count.</b> A full-width character occupies two columns where a
/// half-width one occupies one, so 13 full-width characters and 13 half-width ones are not the
/// same line. Everything here counts columns, and the caller's limit is expressed in full-width
/// characters and doubled — which is the same arithmetic as Netflix's "full-width counts as 1,
/// half-width as 0.5" and easier to get right in integers.</para>
///
/// <para><b>Kinsoku shori</b> (禁則処理) is the rule that some characters may not begin a line and
/// others may not end one: a line may not open with 。 or 、 or a closing bracket, and may not
/// close with an opening one. The sets below are the common ones. This is a deliberately small
/// implementation of a large convention — it is not UAX #14, it does not do line-breaking for
/// Thai or Khmer, and it does not implement 分割禁止 for Latin runs embedded in Japanese beyond
/// keeping them whole.</para>
///
/// <para><b>Runes, not chars.</b> A non-BMP kanji is one character and two UTF-16 code units;
/// breaking a line between its surrogates produces two replacement characters on screen. The same
/// reason <c>TranscriptNormalizer.CharacterErrorRateTokens</c> enumerates runes.</para>
/// </summary>
internal static class CjkLineBreaking
{
    /// <summary>Characters that may not begin a line — closing marks, small kana, sound marks.</summary>
    private const string CannotStartLine =
        "。、，．・：；？！‼⁇⁈⁉)]}｝）〕］〉》」』】〙〗〟’”｠»" +
        "ゝゞーァィゥェォッャュョヮヵヶぁぃぅぇぉっゃゅょゎ々〻" +
        "‐゠–〜?!‥…‧﹏､~，.";

    /// <summary>Characters that may not end a line — opening marks.</summary>
    private const string CannotEndLine = "([{｛（〔［〈《「『【〘〖〝‘“｟«";

    /// <summary>
    /// True when the text carries any character this breaker knows how to break inside.
    ///
    /// <para>The gate on every behaviour change in the subtitle writers: text with no such
    /// character takes the path it always took, byte for byte, and a test holds that.</para>
    /// </summary>
    public static bool ContainsBreakableScript(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (IsWide(rune)) return true;
        }

        return false;
    }

    /// <summary>Columns the text occupies: two per full-width character, one per half-width.</summary>
    public static int Columns(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var columns = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            columns += IsWide(rune) ? 2 : 1;
        }

        return columns;
    }

    /// <summary>
    /// Wraps into at most <paramref name="maxLines"/> lines of <paramref name="maxColumns"/>
    /// columns, breaking between characters and obeying kinsoku.
    ///
    /// <para>The last line is allowed to overflow rather than losing text: a caller that wants the
    /// text to fit splits it into more cues first, which is what <see cref="SplitByColumns"/> is
    /// for. Dropping a character is never an option here — the transcript is the product.</para>
    /// </summary>
    public static List<string> Wrap(string text, int maxColumns, int maxLines)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return [string.Empty];
        if (maxColumns < 2) maxColumns = 2;

        var runes = text.EnumerateRunes().ToList();
        var index = 0;

        while (index < runes.Count)
        {
            if (lines.Count == maxLines - 1)
            {
                // Last line takes whatever is left, overflow and all.
                lines.Add(Join(runes, index, runes.Count));
                return lines;
            }

            var end = BreakPoint(runes, index, maxColumns);
            lines.Add(Join(runes, index, end));
            index = end;
        }

        return lines.Count == 0 ? [string.Empty] : lines;
    }

    /// <summary>
    /// Cuts the text into pieces of at most <paramref name="maxColumns"/> columns, breaking
    /// between characters and obeying kinsoku — the character-script analogue of splitting a Latin
    /// line at spaces.
    /// </summary>
    public static List<string> SplitByColumns(string text, int maxColumns)
    {
        var pieces = new List<string>();
        if (string.IsNullOrEmpty(text)) return pieces;
        if (maxColumns < 2) maxColumns = 2;

        var runes = text.EnumerateRunes().ToList();
        var index = 0;

        while (index < runes.Count)
        {
            var end = BreakPoint(runes, index, maxColumns);
            var piece = Join(runes, index, end).Trim();
            if (piece.Length > 0)
            {
                pieces.Add(piece);
            }

            index = end;
        }

        return pieces;
    }

    /// <summary>
    /// Where the line starting at <paramref name="from"/> ends: as many characters as fit in
    /// <paramref name="maxColumns"/>, then moved by the kinsoku rules.
    /// </summary>
    private static int BreakPoint(List<Rune> runes, int from, int maxColumns)
    {
        var columns = 0;
        var end = from;
        while (end < runes.Count)
        {
            var width = IsWide(runes[end]) ? 2 : 1;
            if (columns + width > maxColumns && end > from) break;
            columns += width;
            end++;
        }

        if (end >= runes.Count) return runes.Count;

        // A line may not begin with a closing mark or a small kana, so let the current line hang
        // one character past its limit rather than pushing the mark down. Bounded: a run of them
        // is followed one at a time and never past the end.
        while (end < runes.Count && CannotStartLine.Contains(runes[end].ToString(), StringComparison.Ordinal))
        {
            end++;
        }

        // A line may not end with an opening mark, so push it down to the next line. Never past
        // the start, which would produce an empty line and loop.
        while (end - 1 > from && CannotEndLine.Contains(runes[end - 1].ToString(), StringComparison.Ordinal))
        {
            end--;
        }

        return end;
    }

    private static string Join(List<Rune> runes, int from, int to)
    {
        var builder = new StringBuilder();
        for (var i = from; i < to; i++)
        {
            builder.Append(runes[i].ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// True for a character drawn at full width — the CJK blocks, kana, Hangul, the fullwidth
    /// forms and the CJK punctuation that travels with them.
    /// </summary>
    private static bool IsWide(Rune rune)
    {
        var value = rune.Value;
        return value is
            (>= 0x1100 and <= 0x115F) or      // Hangul Jamo
            (>= 0x2E80 and <= 0x303E) or      // CJK radicals, Kangxi, CJK symbols and punctuation
            (>= 0x3041 and <= 0x33FF) or      // kana, Hangul compatibility, CJK compatibility
            (>= 0x3400 and <= 0x4DBF) or      // CJK Unified Ideographs Extension A
            (>= 0x4E00 and <= 0x9FFF) or      // CJK Unified Ideographs
            (>= 0xA000 and <= 0xA4CF) or      // Yi
            (>= 0xAC00 and <= 0xD7A3) or      // Hangul syllables
            (>= 0xF900 and <= 0xFAFF) or      // CJK compatibility ideographs
            (>= 0xFE30 and <= 0xFE4F) or      // CJK compatibility forms
            (>= 0xFF00 and <= 0xFF60) or      // fullwidth forms
            (>= 0xFFE0 and <= 0xFFE6) or      // fullwidth signs
            (>= 0x20000 and <= 0x2FA1F);      // CJK Extension B and beyond, where the surname kanji live
    }
}
