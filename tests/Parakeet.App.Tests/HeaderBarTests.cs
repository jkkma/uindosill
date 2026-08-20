using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Parakeet.App.Views;

namespace Parakeet.App.Tests;

/// <summary>
/// The geometry of the headerbar's window buttons, which is the one part of this window that
/// cannot be checked by looking at a screenshot of it.
/// </summary>
/// <remarks>
/// <para>
/// The glyph is centred in its button, so a button that is 30px tall inside a 45px bar draws its
/// dash and its cross in exactly the same place as one that fills the bar. The difference only
/// appears under the pointer, as a hover ground that floats instead of filling the corner — and it
/// is invisible in a resting capture, which is how it shipped past a rendered check and had to be
/// reported by eye.
/// </para>
/// <para>
/// It came from style precedence rather than from anything in the headerbar: Avalonia resolves
/// competing styles by declaration order and each one only overrides the properties it names, so
/// the general <c>Button</c> rule's <c>Height="30"</c> survived underneath <c>Button.window</c>
/// until that style set <c>Height="NaN"</c> of its own. The lesson generalises past this control,
/// which is why the assertion is about the box rather than about the setter.
/// </para>
/// </remarks>
public class HeaderBarTests
{
    [AvaloniaTheory]
    [InlineData("WindowMinimise")]
    [InlineData("WindowMaximise")]
    [InlineData("WindowClose")]
    public void EveryWindowButtonFillsTheHeaderBarTopToBottom(string name)
    {
        var window = new MainWindow { DataContext = WindowTests.NewViewModel(out _) };
        window.Show();
        window.UpdateLayout();

        var header = window.FindControl<Border>("HeaderBar");
        var button = window.FindControl<Button>(name);

        Assert.NotNull(header);
        Assert.NotNull(button);

        // The bar's inner height: its own height less the hairline along the bottom.
        var inner = header!.Bounds.Height - header.BorderThickness.Bottom;
        Assert.True(inner > 0, "the headerbar has no height to fill");

        Assert.Equal(inner, button!.Bounds.Height, precision: 3);

        // And it starts at the very top of the bar rather than floating below it.
        var top = button.TranslatePoint(default, header);
        Assert.NotNull(top);
        Assert.Equal(0, top!.Value.Y, precision: 3);
    }

    [AvaloniaFact]
    public void CloseOwnsTheTopRightCornerOfTheWindow()
    {
        // Fitts's law, and the reason the headerbar carries no right padding: throwing the pointer
        // into the corner has to land on Close. A gap of even a pixel there means the corner
        // belongs to the window frame and the gesture misses.
        var window = new MainWindow { DataContext = WindowTests.NewViewModel(out _) };
        window.Show();
        window.UpdateLayout();

        var header = window.FindControl<Border>("HeaderBar");
        var close = window.FindControl<Button>("WindowClose");

        Assert.NotNull(header);
        Assert.NotNull(close);

        var topLeft = close!.TranslatePoint(default, header!);
        Assert.NotNull(topLeft);

        var right = topLeft!.Value.X + close.Bounds.Width;
        Assert.Equal(header!.Bounds.Width, right, precision: 3);
    }
}
