using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Parakeet.App.Views;

namespace Parakeet.App.Tests;

/// <summary>
/// The window buttons' hover states, driven by simulated pointer input rather than by looking.
/// </summary>
/// <remarks>
/// <para>
/// These buttons show nothing at rest — that is the whole design — so every property worth having
/// only exists under the pointer, and screenshots of the running application turned out to be a
/// poor way to check it: the capture does not reproduce pointer state reliably, and aiming a
/// cursor at a physical pixel competes with whoever is holding the mouse. Simulated input answers
/// the same question deterministically and keeps answering it.
/// </para>
/// <para>
/// Two real defects motivated this, and both were precedence rather than colour choice. The
/// buttons drew no hover at all while the ground was being set on Fluent's own content presenter,
/// because the Button ControlTheme sets that same property on that same element; they now carry a
/// template whose Border nothing else styles. And the close glyph stayed ink-dark on the red
/// because each Path had <c>Stroke</c> written on the element, and a local value outranks every
/// style setter no matter how specific the selector.
/// </para>
/// </remarks>
public class WindowButtonHoverTests
{
    // From Theme/Tokens.axaml. Duplicated deliberately: a test that reads the same resource the
    // window reads would pass whatever that resource said, including nothing.
    private static readonly Color Ground = Color.Parse("#ECEEE9");
    private static readonly Color CloseHover = Color.Parse("#C42B1C");
    private static readonly Color Ink = Color.Parse("#23261F");
    private static readonly Color White = Color.Parse("#FFFFFF");

    [AvaloniaTheory]
    [InlineData("WindowMinimise")]
    [InlineData("WindowMaximise")]
    public void HoveringAWindowButtonDrawsAGroundYouCanSee(string name)
    {
        var (window, button) = Open(name);

        Assert.True(IsTransparent(GroundOf(button)), "the button drew a ground before it was hovered");

        Hover(window, button);

        Assert.Equal(Ground, ColourOf(GroundOf(button)));
    }

    [AvaloniaFact]
    public void HoveringCloseTurnsItRedAndTheGlyphWhite()
    {
        var (window, close) = Open("WindowClose");

        // At rest it is exactly as quiet as the other two.
        Assert.True(IsTransparent(GroundOf(close)));
        Assert.Equal(Ink, StrokeOf(close));

        Hover(window, close);

        Assert.Equal(CloseHover, ColourOf(GroundOf(close)));

        // The half that was missed by eye: red under an ink-dark cross is worse than no red.
        Assert.Equal(White, StrokeOf(close));
    }

    [AvaloniaFact]
    public void OnlyTheHoveredButtonReacts()
    {
        // Three buttons in a row with no gap between them, so a rule that matched too broadly
        // would light all of them and look deliberate.
        var (window, close) = Open("WindowClose");
        var minimise = window.FindControl<Button>("WindowMinimise")!;
        var maximise = window.FindControl<Button>("WindowMaximise")!;

        Hover(window, close);

        Assert.True(IsTransparent(GroundOf(minimise)));
        Assert.True(IsTransparent(GroundOf(maximise)));
        Assert.False(IsTransparent(GroundOf(close)));
    }

    private static (MainWindow Window, Button Button) Open(string name)
    {
        var window = new MainWindow { DataContext = WindowTests.NewViewModel(out _) };
        window.Show();
        window.UpdateLayout();

        var button = window.FindControl<Button>(name);
        Assert.NotNull(button);
        return (window, button!);
    }

    /// <summary>Moves the pointer to the centre of a control and lets the frame settle.</summary>
    private static void Hover(MainWindow window, Button button)
    {
        var centre = button.TranslatePoint(
            new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);

        Assert.NotNull(centre);

        window.MouseMove(centre!.Value);
        window.UpdateLayout();
    }

    private static Border GroundOf(Button button)
    {
        var ground = button.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "Ground");

        Assert.NotNull(ground);
        return ground!;
    }

    private static Color StrokeOf(Button button)
    {
        var path = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First();
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(path.Stroke);
        return brush.Color;
    }

    // ISolidColorBrush rather than SolidColorBrush throughout, and it matters: a brush that came
    // from a resource arrives as ImmutableSolidColorBrush, which does not derive from
    // SolidColorBrush. Checking for the concrete type reports every one of these as "not a solid
    // colour" — which is how the first version of this file failed on a window that was correct.
    private static Color ColourOf(Border border)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(border.Background);
        return brush.Color;
    }

    private static bool IsTransparent(Border border)
        => border.Background is null
           || (border.Background is ISolidColorBrush b && b.Color.A == 0);
}
