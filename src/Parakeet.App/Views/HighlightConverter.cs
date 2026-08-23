using System.Globalization;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Parakeet.App.ViewModels;

namespace Parakeet.App.Views;

/// <summary>
/// Turns a line of transcript and the word being searched for into the runs a
/// <c>TextBlock</c> draws — the term picked out, the rest plain.
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
/// It binds against <see cref="TextHighlight"/> rather than against two separate values because a
/// converter is handed one thing, and because that one thing is what the line raises a change for:
/// a new term produces a new record, and only the lines carrying it produce one at all.
/// </para>
/// <para>
/// <see cref="Hit"/> is set from the theme in XAML rather than written here as a colour. A hex
/// literal in a converter is the same palette drift this design's token sheet exists to prevent,
/// and it would be a second place to change taro.
/// </para>
/// </remarks>
public sealed class HighlightConverter : IValueConverter
{
    /// <summary>The ground behind a hit. Taro, because searching a transcript is a v2 surface.</summary>
    public IBrush? Hit { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TextHighlight highlight)
        {
            return null;
        }

        var inlines = new InlineCollection();
        var text = highlight.Text;
        var term = highlight.Term;

        if (string.IsNullOrEmpty(term))
        {
            inlines.Add(new Run(text));
            return inlines;
        }

        var at = 0;

        while (at < text.Length)
        {
            var found = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase);

            if (found < 0)
            {
                inlines.Add(new Run(text[at..]));
                break;
            }

            if (found > at)
            {
                inlines.Add(new Run(text[at..found]));
            }

            // The text's own casing, not the term's: the transcript is what was said, and a search
            // for "tokon" must not rewrite "Tokon" on the page to match what was typed.
            inlines.Add(new Run(text.Substring(found, term.Length))
            {
                Background = Hit,
                FontWeight = FontWeight.Bold,
            });

            at = found + term.Length;
        }

        return inlines;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A highlight is drawn, not read back.");
}
