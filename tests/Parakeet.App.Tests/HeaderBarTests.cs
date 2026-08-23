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
    [InlineData("TabAsk")]
    [InlineData("TabExport")]
    [InlineData("TabSettings")]
    [InlineData("TabModels")]
    [InlineData("TabUpdates")]
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

    /// <summary>
    /// The switcher clears the wordmark on its left and the window buttons on its right, at every
    /// width the window can be dragged to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The switcher is centred in the star column of <c>ColumnDefinitions="210,*,210"</c>, so it
    /// grows outwards from the middle as pills are added and a star column does not push its
    /// neighbours aside — it lets the content run over them. The pills went from four to five when
    /// Ask shipped and to six when Export and Settings did, and the failure that arrives at some
    /// number is a pill drawn under the window buttons: still clickable in the middle, dead at the
    /// end, and invisible at the width anybody screenshots at.
    /// </para>
    /// <para>
    /// Measured rather than estimated, and at <c>MinWidth</c> as well as the declared width,
    /// because <c>MinWidth</c> is the case that fails first and the one nobody opens the window at
    /// while checking a design. A seventh pill is what this is really guarding against.
    /// </para>
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(1080)]  // the width the window opens at
    [InlineData(920)]   // MinWidth — the narrowest it can be dragged, and the case that fails first
    public void TheSwitcherClearsTheNameAndTheWindowButtonsAtEveryWidthTheWindowAllows(double width)
    {
        var window = new MainWindow { DataContext = WindowTests.NewViewModel(out _), Width = width };
        window.Show();
        window.UpdateLayout();

        // The narrow case is only a test of anything if the window really took the width.
        Assert.True(window.MinWidth <= width, $"MinWidth is {window.MinWidth}, above the {width} asked for");

        var header = window.FindControl<Border>("HeaderBar");
        var wordmark = window.FindControl<StackPanel>("Wordmark");
        var switcher = window.FindControl<Border>("Switcher");
        var buttons = window.FindControl<StackPanel>("WindowButtons");

        Assert.NotNull(header);
        Assert.NotNull(wordmark);
        Assert.NotNull(switcher);
        Assert.NotNull(buttons);

        static double Left(Visual child, Visual of) =>
            child.TranslatePoint(default, of)?.X ?? throw new InvalidOperationException("not in the tree");

        // The pills, not the Border around them, and that distinction is the whole test. A Grid
        // hands its star column whatever is left and a Border arranged into it reports that width
        // back — so the switcher's own Bounds are the space it was given rather than the space it
        // needs, and they look correct at every width including the ones where it is clipped. The
        // pills inside are arranged at their desired width regardless, so their ink is where the
        // reader's eye and the pointer actually land.
        var pills = switcher!.GetVisualDescendants().OfType<RadioButton>().ToList();
        Assert.Equal(6, pills.Count);

        var inkLeft = pills.Min(p => Left(p, header!));
        var inkRight = pills.Max(p => Left(p, header!) + p.Bounds.Width);

        var wordmarkRight = Left(wordmark!, header!) + wordmark!.Bounds.Width;
        var buttonsLeft = Left(buttons!, header!);

        Assert.True(
            inkLeft >= wordmarkRight,
            $"at {width}px the first pill starts at {inkLeft:0.#} and the name ends at {wordmarkRight:0.#}");

        Assert.True(
            inkRight <= buttonsLeft,
            $"at {width}px the last pill ends at {inkRight:0.#} and the window buttons start at {buttonsLeft:0.#}");

        // And the sunken track still goes round them. A Border does not clip, so a switcher squeezed
        // by its column keeps drawing its pills at full width and simply stops painting the rail
        // underneath them — the pills survive, the shape they sit in does not, and the failure is a
        // rounded track that ends in the middle of a word.
        var switcherLeft = Left(switcher, header!);

        Assert.True(
            inkLeft >= switcherLeft && inkRight <= switcherLeft + switcher.Bounds.Width,
            $"at {width}px the pills run from {inkLeft:0.#} to {inkRight:0.#}, "
                + $"outside a track that runs from {switcherLeft:0.#} to {switcherLeft + switcher.Bounds.Width:0.#}");
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
