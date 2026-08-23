using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia;
using Avalonia.Headless.XUnit;
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

        var text = window.FindControl<SelectableTextBlock>("LicenceText");
        Assert.NotNull(text);
        Assert.Equal(viewModel.LicenceText, text!.Text);

        // A spot check that it is the real package rather than a placeholder that happens to match.
        Assert.Contains("NVIDIA Corporation", text.Text, StringComparison.Ordinal);
        Assert.Contains("creativecommons.org/licenses/by/4.0", text.Text, StringComparison.Ordinal);
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
            window.FindControl<SelectableTextBlock>("EnvironmentSummary")?.Text,
            window.FindControl<TextBlock>("ThreadingNote")?.Text,
            window.FindControl<SelectableTextBlock>("ModelDirectory")?.Text,
            window.FindControl<SelectableTextBlock>("SettingsPath")?.Text,
        };

        Assert.All(drawn, line => Assert.False(string.IsNullOrWhiteSpace(line)));

        var report = viewModel.SystemReport;
        Assert.All(drawn, line => Assert.Contains(line!, report, StringComparison.Ordinal));

        // The version is on both panes, so it is checked against the report rather than by name.
        Assert.Contains(viewModel.Version, report, StringComparison.Ordinal);

        Assert.NotNull(window.FindControl<Button>("CopySystemReport"));

        // Not announced until it has happened. A confirmation that is on before the press says
        // nothing about the press.
        var notice = window.FindControl<TextBlock>("CopyNotice");
        Assert.NotNull(notice);
        Assert.False(notice!.IsVisible);
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

        var header = window.FindControl<Border>("HeaderBar");
        var close = window.FindControl<Button>("WindowClose");
        Assert.NotNull(header);
        Assert.NotNull(close);

        Assert.Equal(
            WindowDecorationsElementRole.TitleBar,
            WindowDecorationProperties.GetElementRole(header!));

        Assert.Equal(
            WindowDecorationsElementRole.CloseButton,
            WindowDecorationProperties.GetElementRole(close!));

        // And the second way out, at the foot of the dialog. A dialog whose only exit is a 46px
        // glyph in a corner is one people drag off the screen instead of closing.
        Assert.NotNull(window.FindControl<Button>("Dismiss"));
    }
}
