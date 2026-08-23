using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;

namespace Parakeet.App.Tests;

/// <summary>
/// The Export and Settings tabs, which between them carry everything that used to stack down the
/// right-hand side of the Transcribe tab.
/// </summary>
/// <remarks>
/// <para>
/// A move like this has exactly two ways of going wrong, and neither throws. A control can arrive
/// on the new page unbound — this window has shipped a checkbox wired to nothing before — and a
/// control can arrive on the new page while a copy of it stays on the old one, which gives two
/// boxes for one setting and a user who ticks the wrong one. So each page is asserted for what it
/// carries, and the Transcribe tab is asserted for what it no longer does.
/// </para>
/// <para>
/// Every lookup here is preceded by selecting the page it is on. A <c>TabControl</c> realises only
/// the selected tab's content, so a lookup on the wrong page answers null — loudly here, because
/// the assertion catches it, and silently in the code-behind, which is gotcha 31.
/// </para>
/// </remarks>
public class OptionTabTests
{
    private const int Transcribe = 0;
    private const int Export = 2;
    private const int Settings = 5;

    private static MainWindow Open(int tab, out MainWindowViewModel viewModel)
    {
        viewModel = WindowTests.NewViewModel(out _);
        viewModel.SelectedTab = tab;

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    [AvaloniaFact]
    public void TheExportTabCarriesTheFormatsTheFolderAndTheWayIntoTheRecording()
    {
        var window = Open(Export, out var viewModel);

        // One tick per format the view model offers, bound both ways rather than merely drawn.
        var ticks = window.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => c.DataContext is OutputFormatViewModel)
            .ToList();

        Assert.Equal(viewModel.Transcribe.Formats.Count, ticks.Count);

        var first = viewModel.Transcribe.Formats[0];
        var tick = Assert.Single(ticks, c => ReferenceEquals(c.DataContext, first));
        Assert.Equal(first.IsSelected, tick.IsChecked);

        tick.IsChecked = !first.IsSelected;
        Assert.Equal(tick.IsChecked, first.IsSelected);

        // The folder box writes through. Blank means "beside each input file", which is why the
        // placeholder rather than a default is what fills the empty case.
        var folder = window.GetVisualDescendants().OfType<TextBox>()
            .Single(b => b.PlaceholderText == "beside each input file");

        folder.Text = @"C:\somewhere";
        Assert.Equal(@"C:\somewhere", viewModel.Transcribe.OutputDirectory);

        Assert.NotNull(window.FindControl<Button>("BrowseOutput"));

        // Bound rather than present: a Button whose Command is null renders, hovers and does
        // nothing.
        var mux = window.FindControl<Button>("AddToRecording");
        Assert.NotNull(mux);
        Assert.NotNull(mux!.Command);
        Assert.NotNull(window.FindControl<TextBlock>("AddToRecordingNotice"));
    }

    [AvaloniaFact]
    public void TheSettingsTabCarriesEveryOptionThatLeftTheTranscribeTab()
    {
        var window = Open(Settings, out var viewModel);

        var speakers = window.FindControl<CheckBox>("LabelSpeakers");
        var english = window.FindControl<CheckBox>("TranslateToEnglish");
        Assert.NotNull(speakers);
        Assert.NotNull(english);
        Assert.NotNull(window.FindControl<DockPanel>("SpeechDetectionRow"));

        // The segmentation note followed the cap it explains, rather than being left behind on a
        // page with no cap on it.
        Assert.NotNull(window.FindControl<TextBlock>("SegmentationNote"));

        // The cut controls, found by their bindings rather than by their labels: a label match
        // passes on a control wired to nothing, which is the failure being guarded against.
        var fixedWindows = window.GetVisualDescendants().OfType<CheckBox>()
            .Single(c => c.Content as string == "Fixed windows instead of speech detection");

        fixedWindows.IsChecked = true;
        Assert.True(viewModel.Transcribe.UseFixedWindows);

        var cap = window.GetVisualDescendants().OfType<NumericUpDown>()
            .Single(n => n.FormatString == "0 's'");

        cap.Value = 45;
        Assert.Equal(45, viewModel.Transcribe.MaxSegmentSeconds);

        // Both opt-ins write through too.
        speakers!.IsChecked = true;
        Assert.True(viewModel.Transcribe.LabelSpeakers);

        english!.IsChecked = true;
        Assert.True(viewModel.Transcribe.TranslateToEnglish);

        // And the way to the About window, which is the Licences tab's replacement.
        Assert.NotNull(window.FindControl<Button>("ShowAbout"));
    }

    /// <summary>
    /// The Transcribe tab kept none of what moved, and says where it went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The half of the move nothing else checks. A control duplicated rather than moved leaves two
    /// boxes for one setting: both bind to the same property, so both work, and the second one is
    /// found months later by somebody who ticked the one that was scrolled off screen. The
    /// forwarding line is asserted with them, because a control that moves without one is as hard
    /// to find as a control that was deleted.
    /// </para>
    /// <para>
    /// Asked of the visual tree and not of <c>FindControl</c>, which cannot answer this question at
    /// all: <c>FindControl</c> reads the window's name scope, and the name scope holds every page's
    /// controls whether or not the page is being drawn — so it finds the Settings tab's checkboxes
    /// from here and every assertion below would fail on a correct window. Gotcha 31.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void TheTranscribeTabKeptNoneOfItAndSaysWhereItWent()
    {
        var window = Open(Transcribe, out _);

        var drawn = window.GetVisualDescendants().OfType<Control>()
            .Select(c => c.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("LabelSpeakers", drawn);
        Assert.DoesNotContain("TranslateToEnglish", drawn);
        Assert.DoesNotContain("SpeechDetectionRow", drawn);
        Assert.DoesNotContain("AddToRecording", drawn);
        Assert.DoesNotContain("BrowseOutput", drawn);
        Assert.DoesNotContain("SegmentationNote", drawn);

        // Not one tick anywhere on the page: the formats went to Export and the four opt-ins to
        // Settings, so a checkbox surviving here is a copy rather than a leftover.
        Assert.Empty(window.GetVisualDescendants().OfType<CheckBox>());

        // What it kept: the queue and the transcript, which is the work.
        Assert.NotNull(window.FindControl<Border>("DropZone"));
        Assert.NotNull(window.FindControl<TextBox>("LinkBox"));

        var moved = window.FindControl<TextBlock>("OptionsMoved");
        Assert.NotNull(moved);
        Assert.True(moved!.IsVisible);
        Assert.Contains("Export tab", moved.Text, StringComparison.Ordinal);
        Assert.Contains("Settings", moved.Text, StringComparison.Ordinal);
    }
}
