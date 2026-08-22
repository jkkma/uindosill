using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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

    [AvaloniaTheory]
    [InlineData("TabTranscribe")]
    [InlineData("TabModels")]
    [InlineData("TabUpdates")]
    [InlineData("TabLicences")]
    public void APressOnATabPillReachesThePillRatherThanDraggingTheWindow(string name)
    {
        // The headerbar is the TitleBar role, so inside it every press is a window move unless the
        // control under the pointer says otherwise — which is what the platform asks the chrome
        // hit-test here. The window buttons carry native roles and always answered for themselves;
        // the pills carried none, walked up to the bar, and dragged the window instead of switching
        // tabs. A render cannot show that, and a headless pointer press would not either, because
        // the swallowing happens in the platform before the press exists. This asks the question
        // the platform asks.
        var window = new MainWindow { DataContext = WindowTests.NewViewModel(out _) };
        window.Show();
        window.UpdateLayout();

        var header = window.FindControl<Border>("HeaderBar");
        var pill = window.FindControl<RadioButton>(name);
        Assert.NotNull(header);
        Assert.NotNull(pill);
        Assert.Equal(WindowDecorationsElementRole.TitleBar, WindowDecorationProperties.GetElementRole(header!));

        // The platform's resolver is internal to Avalonia, so this walks the way it walks: up from
        // the control under the pointer to the first element carrying a role, which is the one that
        // answers. For a pill that has to be the User carve-out, met before the bar's TitleBar.
        Assert.Equal(WindowDecorationsElementRole.User, FirstRoleAbove(pill!));

        // And the pill is inside the bar: the carve-out is the switcher, not a hole in the headerbar.
        Assert.True(pill!.GetVisualAncestors().Contains(header!), "the pill is not inside the headerbar");
    }

    /// <summary>The role that answers a press on <paramref name="visual"/>: its own, or the nearest ancestor's.</summary>
    private static WindowDecorationsElementRole FirstRoleAbove(Visual visual)
    {
        for (Visual? current = visual; current is not null; current = current.GetVisualParent())
        {
            var role = WindowDecorationProperties.GetElementRole(current);
            if (role != WindowDecorationsElementRole.None)
            {
                return role;
            }
        }

        return WindowDecorationsElementRole.None;
    }
}
