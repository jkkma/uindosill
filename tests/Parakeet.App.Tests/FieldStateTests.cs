using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Parakeet.App.Services;
using Parakeet.App.Services.Tools;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

/// <summary>
/// A text box dims when it is disabled and stays put under the pointer; it does not fill.
/// </summary>
/// <remarks>
/// <para>
/// Fluent paints a disabled text box with a solid grey ground and a hovered one with a paler grey,
/// and the design's fields are a hairline that does not change the box. The find box on the Ask
/// tab is disabled for as long as there is no transcript to search — the whole of a first visit to
/// that tab — so the one grey block in the window sat under its most-used page, on the same terms
/// as the disabled ComboBox the Models tab fixed before it.
/// </para>
/// <para>
/// Both states are asserted on the rendered brush of the template's own border, the way the
/// window-button hovers are, because the overrides are resource keys and a key the theme does not
/// read loads without complaint and changes nothing. The hover is driven by simulated pointer
/// input for the reason <see cref="WindowButtonHoverTests"/> gives.
/// </para>
/// </remarks>
public class FieldStateTests
{
    // From Theme/Tokens.axaml. Duplicated deliberately: a test that reads the same resource the
    // window reads would pass whatever that resource said, including nothing.
    private static readonly Color Ground = Color.Parse("#FFFFFF");
    private static readonly Color DisabledEdge = Color.Parse("#E7E9E2");

    [AvaloniaFact]
    public void ADisabledFieldDimsRatherThanFilling()
    {
        var viewModel = WindowTests.NewViewModel(out _);
        viewModel.SelectedTab = 4;

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        // Nothing in the queue, so nothing to search: the box is dead, and says so by going pale.
        var box = window.FindControl<TextBox>("SearchBox")!;
        Assert.False(box.IsEffectivelyEnabled);

        var border = BorderOf(box);
        Assert.Equal(Ground, ColourOf(border.Background));
        Assert.Equal(DisabledEdge, ColourOf(border.BorderBrush));

        window.Close();
    }

    [AvaloniaFact]
    public void AHoveredFieldKeepsItsGround()
    {
        // The link box is alive only when this build can fetch links, and that is a question about
        // the machine rather than about the window: yt-dlp and Deno are looked for under
        // native/win-x64/tools/, which a developer's clone has and a fresh runner does not. A
        // disabled control never takes the pointer, so the real fetcher makes this assert a hover
        // that cannot happen — green where the tools are vendored, red everywhere else, which is
        // how it reached CI. The fake says this build can fetch, so the box is live wherever the
        // suite runs.
        var directory = TestTemp.NewDirectory("uindosill-app");
        var viewModel = new MainWindowViewModel(
            new FakeEngineProvider(),
            new LocalModelStore(directory),
            ModelCatalog.Default,
            player: new FakeMediaPlayer(),
            fetcher: new FakeMediaUrlFetcher(),
            downloadRoot: directory);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var box = window.FindControl<TextBox>("LinkBox")!;

        // Named here rather than left to the pointer assertion below, which reports a dead box as
        // a pointer that missed.
        Assert.True(box.IsEffectivelyEnabled, "the link box is disabled, so there is no hover to read");

        var border = BorderOf(box);
        Assert.Equal(Ground, ColourOf(border.Background));

        var centre = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window)!.Value;
        window.MouseMove(centre);
        window.UpdateLayout();

        Assert.True(box.IsPointerOver, "the pointer did not reach the field");
        Assert.Equal(Ground, ColourOf(border.Background));

        window.Close();
    }

    /// <summary>The border Fluent's text box template draws its ground and hairline on.</summary>
    private static Border BorderOf(TextBox box) =>
        box.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement");

    private static Color ColourOf(IBrush? brush) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
