using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Parakeet.App.Views;

/// <summary>
/// True when an index equals the one named by the converter parameter. Two-way, so it also turns a
/// check back into an index.
/// </summary>
/// <remarks>
/// <para>
/// Two things in this window are "one of a small fixed set", and both need the same conversion.
///
/// The pill view-switcher is six <c>RadioButton</c>s rather than a <c>TabControl</c>'s own
/// headers, because the design puts it in the middle of the headerbar with the application name to
/// its left and the window buttons to its right — a place a tab strip cannot reach. The pages
/// still live in a <c>TabControl</c>, whose header strip is collapsed, so this is what keeps the
/// two in step.
///
/// The speaker chips in a transcript are the other: a closed set of eight, selected by style class
/// rather than by a brush on the view model, so the colours stay in the theme where every other
/// colour in this application lives.
/// </para>
/// <para>
/// Converting back returns <see cref="BindingOperations.DoNothing"/> when the button is being
/// <em>un</em>checked. Radio buttons in a group report both halves of a change — the old segment
/// goes false, the new one goes true — and in an undefined order. Writing the index on the false
/// half would let a deselection race the selection and land the wrong page.
/// </para>
/// </remarks>
public sealed class IndexMatchConverter : IValueConverter
{
    public static IndexMatchConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int selected && TryIndex(parameter, out var index) && selected == index;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && TryIndex(parameter, out var index))
        {
            return index;
        }

        return BindingOperations.DoNothing;
    }

    // The parameter arrives from XAML as a string.
    private static bool TryIndex(object? parameter, out int index)
        => int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
}
