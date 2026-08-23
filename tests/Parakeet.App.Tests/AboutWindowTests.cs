using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;

namespace Parakeet.App.Tests;

/// <summary>
/// The About window, which is where the Licences tab went on 2026-08-23.
/// </summary>
/// <remarks>
/// <para>
/// The notice package has to be present inside the application, because that is what the licences
/// require — not a file in a source repository. Moving it out of the main window's tab strip is
/// only safe if it is still reachable and still complete, so both halves are asserted here: the
/// window builds with its three panes, and the pane that carries the notice carries all of it.
/// </para>
/// <para>
/// The panes are a headless <c>TabControl</c> driven by pills, the same arrangement the main
/// window uses, which means the same failure is available: a pill wired to the wrong index renders
/// and highlights and shows the wrong page. Three hand-written converter parameters, so they are
/// checked the same way.
/// </para>
/// <para>
/// Nothing here reaches a control through <c>FindControl</c>. It reads the window's name scope,
/// which holds all three panes whether or not they are drawn (gotcha 31), so it cannot tell a
/// control on the Licences pane from one on the System pane — which is the only thing these tests
/// are trying to establish.
/// </para>
/// </remarks>
public class AboutWindowTests
{
    private static AboutViewModel NewViewModel() =>
        new("1.2.3", @"C:\models", @"C:\settings\settings.json");

    private static AboutWindow Open(out AboutViewModel viewModel)
    {
        viewModel = NewViewModel();
        var window = new AboutWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    /// <summary>
    /// A named control this window is currently DRAWING, or a failure naming it.
    /// </summary>
    /// <remarks>
    /// <c>FindControl</c> would answer for a control on any of the three panes, because it reads
    /// the name scope rather than the visual tree (gotcha 31). Every assertion below is about which
    /// pane something is on, so none of them may use it.
    /// </remarks>
    private static T Drawn<T>(AboutWindow window, string name) where T : Control
    {
        var found = window.GetVisualDescendants().OfType<T>()
            .Where(c => c.Name == name)
            .ToList();

        Assert.True(found.Count == 1, $"expected one drawn {typeof(T).Name} named '{name}', found {found.Count}");
        return found[0];
    }

    [AvaloniaFact]
    public void ItBuildsWithItsThreePanes()
    {
        var window = Open(out _);

        var panes = window.FindControl<TabControl>("Panes");
        Assert.NotNull(panes);

        Assert.Equal(
            ["About", "Licences", "System"],
            panes!.Items.OfType<TabItem>().Select(t => t.Header as string));
    }

    [AvaloniaTheory]
    [InlineData("PaneAbout", "About")]
    [InlineData("PaneLicences", "Licences")]
    [InlineData("PaneSystem", "System")]
    public void EveryPillSelectsThePaneItNames(string pillName, string header)
    {
        var window = Open(out _);

        var pill = window.FindControl<RadioButton>(pillName);
        var panes = window.FindControl<TabControl>("Panes");
        Assert.NotNull(pill);
        Assert.NotNull(panes);

        pill!.IsChecked = true;
        window.UpdateLayout();

        var selected = Assert.IsType<TabItem>(panes!.SelectedItem);
        Assert.Equal(header, selected.Header);
    }

    /// <summary>
    /// The notice pane carries the whole notice, drawn rather than merely available on the view
    /// model.
    /// </summary>
    /// <remarks>
    /// <see cref="WindowTests.TheAboutWindowCarriesTheFullNoticeInsideTheApplication"/> asserts the
    /// text is built correctly; this asserts a control in the window is showing it. The two used to
    /// be one test against <c>MainWindowViewModel</c>, and between them lay the failure this window
    /// has shipped more than once — a correct string bound to nothing.
    /// </remarks>
    [AvaloniaFact]
    public void TheLicencePaneDrawsTheWholeNotice()
    {
        var window = Open(out var viewModel);

        viewModel.SelectedTab = 1;
        window.UpdateLayout();

        var text = Drawn<SelectableTextBlock>(window, "LicenceText");
        Assert.Equal(viewModel.LicenceText, text.Text);

        // A spot check that it is the real package rather than a placeholder that happens to match.
        Assert.Contains("NVIDIA Corporation", text.Text, StringComparison.Ordinal);
        Assert.Contains("creativecommons.org/licenses/by/4.0", text.Text, StringComparison.Ordinal);

        // Selecting that text has to leave it readable. Fluent's selection brush is the accent
        // unless overridden, and Matcha600 under dark ink was found by eye on 2026-08-23; the
        // token override in Tokens.axaml resolves it to Matcha200, and this asserts the brush the
        // control actually resolved — an override on a mistyped or wrongly-typed key loads
        // without complaint and changes nothing.
        var selection = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(text.SelectionBrush);
        Assert.Equal(Avalonia.Media.Color.Parse("#CDE3B5"), selection.Color);
    }

    /// <summary>
    /// The System pane draws the five facts a bug report asks for, and the Copy button copies the
    /// same five.
    /// </summary>
    /// <remarks>
    /// The copied text is built in the view model rather than read off the controls, so the two
    /// could drift — a line added to the pane and not to the report would be a pane that says more
    /// than the button sends. That is what the containment check below is for.
    /// </remarks>
    [AvaloniaFact]
    public void TheSystemPaneDrawsWhatTheCopyButtonSends()
    {
        var window = Open(out var viewModel);

        viewModel.SelectedTab = 2;
        window.UpdateLayout();

        var drawn = new[]
        {
            Drawn<SelectableTextBlock>(window, "EnvironmentSummary").Text,
            Drawn<TextBlock>(window, "ThreadingNote").Text,
            Drawn<SelectableTextBlock>(window, "ModelDirectory").Text,
            Drawn<SelectableTextBlock>(window, "SettingsPath").Text,
        };

        Assert.All(drawn, line => Assert.False(string.IsNullOrWhiteSpace(line)));

        var report = viewModel.SystemReport;
        Assert.All(drawn, line => Assert.Contains(line!, report, StringComparison.Ordinal));

        // The version is on both panes, so it is checked against the report rather than by name.
        Assert.Contains(viewModel.Version, report, StringComparison.Ordinal);

        Drawn<Button>(window, "CopySystemReport");

        // Not announced until it has happened. A confirmation that is on before the press says
        // nothing about the press.
        var notice = Drawn<TextBlock>(window, "CopyNotice");
        Assert.False(notice.IsVisible);
    }

    /// <summary>
    /// The dialog wears the application's chrome rather than the platform's.
    /// </summary>
    /// <remarks>
    /// The main window turns off the OS title bar and draws its own, which only works because the
    /// headerbar carries <c>ElementRole="TitleBar"</c> and the close button carries
    /// <c>CloseButton</c>. Get either wrong on this window and it still renders — the bar is there,
    /// the glyph is there — but the window cannot be dragged and the corner does nothing, which is
    /// a defect no headless render and no screenshot shows. Same question as the main window's, so
    /// the same assertion.
    /// </remarks>
    [AvaloniaFact]
    public void ItDrawsItsOwnTitleBarAndItsCloseButtonAnswersForItself()
    {
        var window = Open(out _);

        var header = Drawn<Border>(window, "HeaderBar");
        var close = Drawn<Button>(window, "WindowClose");

        Assert.Equal(
            WindowDecorationsElementRole.TitleBar,
            WindowDecorationProperties.GetElementRole(header));

        Assert.Equal(
            WindowDecorationsElementRole.CloseButton,
            WindowDecorationProperties.GetElementRole(close));

        // The second and third ways out. A dialog whose only exit is a 46px glyph in a corner is
        // one people drag off the screen instead of closing — and IsCancel is what routes Escape,
        // which is the key a reader presses before looking for either button. ShowDialog disables
        // the owner window, so a modal that ignores Escape looks like an application that has
        // stopped responding.
        var dismiss = Drawn<Button>(window, "Dismiss");
        Assert.True(dismiss.IsCancel);
    }
}
