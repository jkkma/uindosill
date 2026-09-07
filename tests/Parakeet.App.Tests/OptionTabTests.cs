using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.App.Services;
using Parakeet.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

/// <summary>
/// Which page carries which control, after the Transcribe tab's right-hand column was split up:
/// the outputs on Export, the cut on Settings, and the two extra passes back beside the queue on
/// Transcribe, where the run they change is launched.
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
/// Every test here selects the page it is about before looking at anything, and that is for the
/// write-through assertions, which walk the visual tree and therefore see only what is drawn. It is
/// NOT what makes <c>FindControl</c> work: <c>FindControl</c> reads the window's one name scope,
/// which holds every page whether or not it is realised, so it answers for a control on any tab
/// (gotcha 31). Anything here that claims a control is on a particular page therefore asks the
/// visual tree; a <c>FindControl</c> result proves only that the markup declares the control
/// somewhere.
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

    /// <summary>
    /// A named control the window is currently DRAWING, or a failure naming it.
    /// </summary>
    /// <remarks>
    /// The whole point of this file. <c>FindControl</c> would return the same object with any tab
    /// selected — it reads the name scope, which holds every page (gotcha 31) — so it can say a
    /// control exists but never that it is on this page. Moving a control into the wrong TabItem is
    /// one line's difference in a 1400-line file, and it is exactly what these tests are for.
    /// </remarks>
    private static T Drawn<T>(MainWindow window, string name) where T : Control
    {
        var found = window.GetVisualDescendants().OfType<T>()
            .Where(c => c.Name == name)
            .ToList();

        Assert.True(found.Count == 1, $"expected one drawn {typeof(T).Name} named '{name}', found {found.Count}");
        return found[0];
    }

    [AvaloniaFact]
    public void TheExportTabCarriesTheFormatsTheFolderAndTheExportButton()
    {
        var window = Open(Export, out var viewModel);

        // One tick per format the view model offers, bound both ways rather than merely drawn.
        var ticks = window.GetVisualDescendants().OfType<CheckBox>()
            .Where(c => c.DataContext is OutputFormatViewModel)
            .ToList();

        Assert.Equal(viewModel.Transcribe.Formats.Count, ticks.Count);

        var first = viewModel.Transcribe.Formats[0];
        var tick = Assert.Single(ticks, c => ReferenceEquals(c.DataContext, first));
        tick.IsChecked = !first.IsSelected;
        Assert.Equal(tick.IsChecked, first.IsSelected);

        // The page's own button, bound, dark in the state this page opens in — nothing is
        // selected in a queue that is on another tab — and explained by the notice beside it,
        // which points at that queue.
        var export = Drawn<Button>(window, "ExportFiles");
        Assert.NotNull(export.Command);

        // IsEffectivelyEnabled, not IsEnabled: a command's CanExecute reaches the button through
        // effective enablement, and the local property stays true either way.
        Assert.False(export.IsEffectivelyEnabled);

        var exportNotice = Drawn<TextBlock>(window, "ExportNotice");
        Assert.True(exportNotice.IsVisible);
        Assert.Contains("Transcribe tab", exportNotice.Text, StringComparison.Ordinal);

        // The folder box writes through. Blank means "beside each input file", which is why the
        // placeholder rather than a default is what fills the empty case.
        var folder = window.GetVisualDescendants().OfType<TextBox>()
            .Single(b => b.PlaceholderText == "beside each input file");

        folder.Text = @"C:\somewhere";
        Assert.Equal(@"C:\somewhere", viewModel.Transcribe.OutputDirectory);

        Drawn<Button>(window, "BrowseOutput");

        // Bound rather than present: a Button whose Command is null renders, hovers and does
        // nothing.
        var mux = Drawn<Button>(window, "AddToRecording");
        Assert.NotNull(mux.Command);

        // And it says why it cannot be pressed, in the state this page opens in — nothing is
        // selected in a queue that is on another tab, which used to explain itself when the button
        // sat under the list.
        var notice = Drawn<TextBlock>(window, "AddToRecordingNotice");
        Assert.True(notice.IsVisible);
        Assert.Contains("Transcribe tab", notice.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheSettingsTabCarriesTheCutAndTheWayToAbout()
    {
        var window = Open(Settings, out var viewModel);

        // **The way to About is on General, which is the sub-tab that opens.** Asserted before the
        // switch below, because the thing this half is about is that somebody who opens Settings
        // and touches nothing can still find the licences.
        Drawn<Button>(window, "ShowAbout");

        // **The cut moved behind Advanced on 2026-08-29 and this is what that costs a test.** A
        // TabControl realises only its selected page, so every assertion below would fail on a
        // control that is present in the name scope and not being drawn — which is precisely the
        // distinction `Drawn` exists to make, and why this switches the sub-tab rather than
        // reaching past it with FindControl.
        var subTabs = Drawn<TabControl>(window, "SettingsSubTabs");
        subTabs.SelectedIndex = 1;
        window.UpdateLayout();

        Drawn<DockPanel>(window, "SpeechDetectionRow");

        // The segmentation note followed the cap it explains, rather than being left behind on a
        // page with no cap on it.
        Drawn<TextBlock>(window, "SegmentationNote");

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

        // The two extra passes are NOT here — they went back to the Transcribe tab beside the
        // queue they run over, and a copy surviving here would be two boxes for one setting.
        var drawn = window.GetVisualDescendants().OfType<Control>()
            .Select(c => c.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("LabelSpeakers", drawn);
        Assert.DoesNotContain("TranslateToEnglish", drawn);
    }

    [AvaloniaFact]
    public void TheAdvancedSubTabWarnsBeforeItOffersAnything()
    {
        // The advisory is the point of the split. A page of settings that can make transcripts
        // worse, sitting one click from the update button anybody presses, has to say so — and it
        // has to say so on the page rather than in a heading somebody scrolls past.
        var window = Open(Settings, out _);

        var subTabs = Drawn<TabControl>(window, "SettingsSubTabs");
        subTabs.SelectedIndex = 1;
        window.UpdateLayout();

        var warning = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("warning") && t.Text is { Length: > 0 })
            .Select(t => t.Text!)
            .ToList();

        var banner = Assert.Single(warning, t => t.Contains("unless you know what they do", StringComparison.Ordinal));

        // Until 2026-09-06 the banner also said which side had been measured — "Every default here
        // was measured. Most alternatives have not been" — and two asserts held it there. The
        // maintainer took every measurement statement off the Settings page that day, so the
        // banner now says what the controls change and what a wrong choice costs, and nothing
        // about who measured what. What it must still not do is overclaim in either direction:
        // no "dangerous", and no measurement claim that the page no longer backs.
        Assert.DoesNotContain("dangerous", banner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("measured", banner, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void TheAdvancedPageNamesNoIdentifiers()
    {
        // **Advanced named every control twice until 2026-09-07**: once in English above it, and
        // again on a grey line beneath carrying the settings-file key, the CLI flag and the
        // library name. This test asserted their presence for as long as they were held to be the
        // reason the page existed. The maintainer took all four lines off that day, so it now
        // holds the opposite, and holds it against their return.
        var window = Open(Settings, out _);

        var subTabs = Drawn<TabControl>(window, "SettingsSubTabs");
        subTabs.SelectedIndex = 1;
        window.UpdateLayout();

        var text = string.Join("\n", window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => t is { Length: > 0 }));

        foreach (var identifier in new[]
        {
            "diarisationProvider", "diarisationBatchSize", "askExpertPlacement", "--backend",
            "segmentation_batch_size", "embedding_batch_size", "UseFixedWindows",
            "MaxSegmentSeconds", "UseNeuralSpeechDetection", "settings.json",
        })
        {
            Assert.DoesNotContain(identifier, text, StringComparison.Ordinal);
        }

        // What did not go with them, and why this still asserts something rather than nothing:
        // losing an Advanced setting to a window close is easy to do and expensive to discover,
        // so the page says so in its own English.
        Assert.Contains("go back to their defaults when the window closes", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Transcribe tab carries the two extra passes — translation first, speakers last — and
    /// says where everything else went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The half of the moves nothing else checks. A control duplicated rather than moved leaves two
    /// boxes for one setting: both bind to the same property, so both work, and the second one is
    /// found months later by somebody who ticked the one that was scrolled off screen. The
    /// forwarding line is asserted with them, because a control that moves without one is as hard
    /// to find as a control that was deleted.
    /// </para>
    /// <para>
    /// Asked of the visual tree and not of <c>FindControl</c>, which cannot answer this question at
    /// all: <c>FindControl</c> reads the window's name scope, and the name scope holds every page's
    /// controls whether or not the page is being drawn — so it finds the Export and Settings tabs'
    /// controls from here and every absence assertion below would fail on a correct window.
    /// Gotcha 31.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void TheTranscribeTabCarriesTheTwoPassesAndSaysWhereTheRestWent()
    {
        var window = Open(Transcribe, out var viewModel);

        // The two opt-ins are drawn here, bound both ways rather than merely present.
        var speakers = Drawn<CheckBox>(window, "LabelSpeakers");
        var english = Drawn<CheckBox>(window, "TranslateToEnglish");

        speakers.IsChecked = true;
        Assert.True(viewModel.Transcribe.LabelSpeakers);

        english.IsChecked = true;
        Assert.True(viewModel.Transcribe.TranslateToEnglish);

        // Translation above, speakers last — asked of the drawn geometry rather than the markup,
        // because tree order is what an edit swaps by accident and geometry is what a reader sees.
        var englishTop = english.TranslatePoint(default, window)!.Value.Y;
        var speakersTop = speakers.TranslatePoint(default, window)!.Value.Y;
        Assert.True(englishTop < speakersTop,
            $"'Translate to English' (y={englishTop}) is drawn below 'Label speakers' (y={speakersTop})");

        // Exactly the three opt-ins on the whole page — the tidy joined the two on 2026-09-02 —
        // the formats are on Export with the button that writes them, the cut is on Settings, so
        // a fourth checkbox here is a copy rather than a leftover.
        Assert.NotNull(Drawn<CheckBox>(window, "TidyUpTranscript"));
        Assert.Equal(3, window.GetVisualDescendants().OfType<CheckBox>().Count());

        var drawn = window.GetVisualDescendants().OfType<Control>()
            .Select(c => c.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("SpeechDetectionRow", drawn);
        Assert.DoesNotContain("AddToRecording", drawn);
        Assert.DoesNotContain("BrowseOutput", drawn);
        Assert.DoesNotContain("SegmentationNote", drawn);

        // The forwarding line is gone too: it addressed somebody who knew the pre-split layout,
        // and the tab strip already names where everything lives.
        Assert.DoesNotContain("OptionsMoved", drawn);

        // What it kept besides the passes: the queue and the transcript, which is the work.
        Drawn<Border>(window, "DropZone");
        Drawn<TextBox>(window, "LinkBox");
    }

    /// <summary>
    /// The long-recording speaker warning is drawn on the Transcribe tab, on the same screen as
    /// the queue and Start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SpeakerDurationWarning</c> exists to be, in its own words, "in front of the person who
    /// can still act on it… before any of the twenty minutes are spent" — and the person who can
    /// still act is the one about to press Start. While the opt-in lived on Settings the warning
    /// had to be drawn twice to reach them; with the opt-in back beside the queue one draw covers
    /// it, and this pins that the one draw is on the page where Start is.
    /// </para>
    /// <para>
    /// The Start guard does not cover it: <c>TranscribeViewModel</c> raises the bound sentence only
    /// when the count is missing, so a queue with a count set starts silently.
    /// </para>
    /// </remarks>
    [AvaloniaFact]
    public void ARecordingPastTheEvidenceWarnsOnTheScreenWhereStartIs()
    {
        var directory = TestTemp.NewDirectory("uindosill-warn");
        var viewModel = new MainWindowViewModel(
            new FakeEngineProvider(speakers: new FakeSpeakerLabellerOptions
            {
                SpeakerCount = 4,
                MaxSpeakers = 4,
                SupportsFixedSpeakerCount = false,
                ReliableUpTo = TimeSpan.FromMinutes(2),
            }),
            new LocalModelStore(directory),
            ModelCatalog.Default,
            player: new FakeMediaPlayer());

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        // Nothing is decoded: the length comes off the header when the file was queued, which is
        // what lets this be a warning about what is about to happen rather than a report on it.
        viewModel.Transcribe.AddFiles([LongWav(directory)]);
        viewModel.Transcribe.LabelSpeakers = true;

        // The count is set, which is the case the Start guard stays quiet for.
        viewModel.Transcribe.SpeakerCount = 3;
        window.UpdateLayout();

        Assert.NotNull(viewModel.Transcribe.SpeakerDurationWarning);
        Assert.Null(viewModel.Transcribe.StartHint);

        var warning = Drawn<TextBlock>(window, "DurationWarning");
        Assert.True(warning.IsVisible, "the duration warning is not drawn on the Transcribe tab");
        Assert.Equal(viewModel.Transcribe.SpeakerDurationWarning, warning.Text);

        // And it goes quiet again with the opt-in, rather than standing over a run that will not
        // label anything.
        viewModel.Transcribe.LabelSpeakers = false;
        window.UpdateLayout();

        Assert.False(warning.IsVisible);
    }

    /// <summary>
    /// The two halves of Settings are switched by the pills every other page switch in this
    /// window uses, and the toolkit's own tab strip is not drawn.
    /// </summary>
    /// <remarks>
    /// The strip was Fluent's — two words at 20px over a rounded underline — and the one header in
    /// the product not drawn in the design's terms. The pills bind to the TabControl's own index
    /// rather than to a view model property, so both directions are asserted: a pill pressed
    /// opens its half, and an index set from code (which is how the tests above reach Advanced)
    /// lights its pill.
    /// </remarks>
    [AvaloniaFact]
    public void TheSettingsHalvesAreSwitchedByPillsAndTheToolkitsStripIsNotDrawn()
    {
        var window = Open(Settings, out _);

        var subTabs = Drawn<TabControl>(window, "SettingsSubTabs");
        var general = Drawn<RadioButton>(window, "SettingsGeneral");
        var advanced = Drawn<RadioButton>(window, "SettingsAdvanced");

        Assert.Equal(true, general.IsChecked);
        Assert.Equal(false, advanced.IsChecked);

        // The TabControl's own header presenter is collapsed, which is what the headless class
        // is for; the pills are the only switch a reader sees.
        var strip = subTabs.GetVisualDescendants().OfType<ItemsPresenter>()
            .Single(p => p.Name == "PART_ItemsPresenter");
        Assert.False(strip.IsVisible);

        advanced.IsChecked = true;
        window.UpdateLayout();

        Assert.Equal(1, subTabs.SelectedIndex);
        Drawn<DockPanel>(window, "SpeechDetectionRow");

        subTabs.SelectedIndex = 0;
        window.UpdateLayout();

        Assert.Equal(true, general.IsChecked);
        Assert.Equal(false, advanced.IsChecked);
        Drawn<Button>(window, "ShowAbout");
    }

    /// <summary>
    /// Thinking before answering is a box like every other on-or-off setting here, and it writes
    /// through.
    /// </summary>
    /// <remarks>
    /// It was a ToggleSwitch, which Fluent drew with its label above the track and an "Off"
    /// beside it — the one rounded control on a page of square boxes, and the only one whose
    /// wording changed with its state. Asserted by name and by binding rather than by label, for
    /// the reason every write-through test in this file gives.
    /// </remarks>
    [AvaloniaFact]
    public void ThinkingBeforeAnsweringIsABoxAndWritesThrough()
    {
        var window = Open(Settings, out var viewModel);

        var box = Drawn<CheckBox>(window, "AskThinking");
        Assert.Equal("Think before answering", box.Content);
        Assert.False(viewModel.AskThinking);

        box.IsChecked = true;
        Assert.True(viewModel.AskThinking);

        Assert.Empty(window.GetVisualDescendants().OfType<ToggleSwitch>());
    }

    /// <summary>A file whose header says it is longer than the bound above, written cheaply.</summary>
    private static string LongWav(string directory)
    {
        var path = Path.Combine(directory, "long.wav");
        WavWriter.WriteFile(path, new float[8_000 * 400], 8_000);
        return path;
    }
}
