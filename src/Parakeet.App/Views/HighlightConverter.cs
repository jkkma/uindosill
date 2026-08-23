using System.Globalization;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Parakeet.App.ViewModels;

namespace Parakeet.App.Views;

/// <summary>
/// Turns a line of transcript, the word being searched for and the word being spoken into the runs
/// a <c>TextBlock</c> draws — each picked out, the rest plain.
/// </summary>
/// <remarks>
/// <para>
/// This is here rather than in the view model on purpose. A highlight is made of
/// <c>Avalonia.Controls.Documents.Run</c>, and building one in a view model would put the toolkit's
/// text types into the layer this application keeps free of them — and into the tests, which then
/// assert on ink rather than on behaviour. The view model says what to mark; this says how a mark
/// is drawn.
/// </para>
/// <para>
/// It binds against <see cref="TextHighlight"/> rather than against separate values because a
/// converter is handed one thing, and because that one thing is what the line raises a change for:
/// a new term or a new word produces a new record, and only the line it happened to produces one at
/// all.
/// </para>
/// <para>
/// <b>The two marks are independent and can overlap</b>, so the text is cut at every boundary of
/// either and each piece is asked both questions. Where they land on the same word the ground is
/// the spoken one — the search hit is still legible, because weight is what a hit is drawn with
/// here and weight is left alone.
/// </para>
/// <para>
/// <b>The spoken word takes a ground and never a weight</b>, and that is a layout decision rather
/// than a taste. A mark that bolded the word being said would change its width three times a
/// second, re-wrapping the paragraph under the reader while they read it; a ground is drawn behind
/// the same glyphs at the same places, so nothing moves. The search hit may be bold because a hit
/// stands still.
/// </para>
/// <para>
/// <see cref="Hit"/> and <see cref="Spoken"/> are set from the theme in XAML rather than written
/// here as colours. A hex literal in a converter is the same palette drift this design's token
/// sheet exists to prevent, and it would be a second place to change taro.
/// </para>
/// </remarks>
public sealed class HighlightConverter : IValueConverter
{
    /// <summary>The ground behind a hit. Taro, because searching a transcript is a v2 surface.</summary>
    public IBrush? Hit { get; set; }

    /// <summary>
    /// The ground behind the word being said. The token sheet's pastel yellow, which is pinned to
    /// this one job and admitted nowhere else in the system.
    /// </summary>
    public IBrush? Spoken { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TextHighlight highlight)
        {
            return null;
        }

        var inlines = new InlineCollection();
        var text = highlight.Text;
        var term = highlight.Term;

        var spokenStart = highlight.SpokenStart;
        var spokenEnd = spokenStart + highlight.SpokenLength;
        var hasSpoken = highlight.SpokenLength > 0 && spokenStart >= 0 && spokenEnd <= text.Length;

        if (string.IsNullOrEmpty(term) && !hasSpoken)
        {
            // The common line, and most of a transcript is common lines: one run, no scan.
            inlines.Add(new Run(text));
            return inlines;
        }

        var hits = Occurrences(text, term);

        // Where the text is cut. Every mark's edges and the two ends, deduplicated and in order,
        // which is what makes a run of overlapping marks come out as the pieces they overlap in.
        var cuts = new SortedSet<int> { 0, text.Length };

        foreach (var (start, end) in hits)
        {
            cuts.Add(start);
            cuts.Add(end);
        }

        if (hasSpoken)
        {
            cuts.Add(spokenStart);
            cuts.Add(spokenEnd);
        }

        var at = 0;

        foreach (var cut in cuts)
        {
            if (cut <= at)
            {
                continue;
            }

            var isHit = Covers(hits, at);
            var isSpoken = hasSpoken && at >= spokenStart && at < spokenEnd;

            // The text's own casing, not the term's: the transcript is what was said, and a search
            // for "tokon" must not rewrite "Tokon" on the page to match what was typed.
            var run = new Run(text[at..cut]);

            if (isSpoken || isHit)
            {
                run.Background = isSpoken ? Spoken : Hit;
            }

            if (isHit)
            {
                // Set only where it is wanted. A weight written onto every run — Normal included —
                // would override what the paragraph inherits from the control above it, so a line
                // drawn in a heavier face anywhere would silently come back Normal the moment it
                // was searched.
                run.FontWeight = FontWeight.Bold;
            }

            inlines.Add(run);

            at = cut;
        }

        return inlines;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A highlight is drawn, not read back.");

    /// <summary>Every place <paramref name="term"/> appears in <paramref name="text"/>, however cased.</summary>
    private static List<(int Start, int End)> Occurrences(string text, string? term)
    {
        var found = new List<(int Start, int End)>();

        if (string.IsNullOrEmpty(term))
        {
            return found;
        }

        var at = 0;

        while (at < text.Length)
        {
            var next = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase);

            if (next < 0)
            {
                break;
            }

            found.Add((next, next + term.Length));
            at = next + term.Length;
        }

        return found;
    }

    private static bool Covers(List<(int Start, int End)> ranges, int at)
    {
        foreach (var (start, end) in ranges)
        {
            if (at >= start && at < end)
            {
                return true;
            }
        }

        return false;
    }
}
