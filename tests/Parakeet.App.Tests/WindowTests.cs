using Avalonia.Media;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Audio;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

public class WindowTests
{
    [AvaloniaFact]
    public void MainWindowBuildsWithAllTabs()
    {
        var window = new MainWindow { DataContext = NewViewModel(out _) };
        window.Show();

        // The tabs stopped being the window's whole content when the update notice was docked above
        // them, so this looks the control up by name rather than asserting on window.Content.
        var tabs = window.FindControl<TabControl>("Tabs");
        Assert.NotNull(tabs);
        Assert.Equal(6, tabs!.Items.Count);
    }

    /// <summary>
    /// Every pill in the headerbar selects the page it names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The switcher's order and the TabControl's are two different lists and always have been, so
    /// each pill carries a hand-written <c>ConverterParameter</c> naming its page's index. That is
    /// six numbers typed by a person, in a file where the pages have twice been reordered, and
    /// nothing else checks them: a pill wired to the wrong index still renders, still highlights,
    /// and quietly shows the wrong page. Swapping any two of them leaves the suite green without
    /// this.
    /// </para>
    /// <para>
    /// Asserted on the TabItem's <c>Header</c> rather than on an index, because an index is the
    /// thing under test — restating it here would only check that a number equals itself.
    /// </para>
    /// </remarks>
    [AvaloniaTheory]
    [InlineData("TabTranscribe", "Transcribe")]
    [InlineData("TabAsk", "Ask")]
    [InlineData("TabExport", "Export")]
    [InlineData("TabSettings", "Settings")]
    [InlineData("TabModels", "Models")]
    [InlineData("TabUpdates", "Updates")]
    public void EveryPillSelectsThePageItNames(string pillName, string header)
    {
        var window = new MainWindow { DataContext = NewViewModel(out _) };
        window.Show();
        window.UpdateLayout();

        var pill = window.FindControl<RadioButton>(pillName);
        var tabs = window.FindControl<TabControl>("Tabs");
        Assert.NotNull(pill);
        Assert.NotNull(tabs);

        pill!.IsChecked = true;
        window.UpdateLayout();

        var selected = Assert.IsType<TabItem>(tabs!.SelectedItem);
        Assert.Equal(header, selected.Header);
    }

    /// <summary>
    /// The Licences page is gone from the switcher, and its notice is reachable from Settings.
    /// </summary>
    /// <remarks>
    /// A retired tab is only retired if nothing still draws it. The pill and the page were removed
    /// together on 2026-08-23; this asserts both halves, and that the button which replaces them is
    /// where the reader was told to look.
    /// </remarks>
    [AvaloniaFact]
    public void TheLicencesTabIsGoneAndSettingsCarriesTheWayToTheNotice()
    {
        var viewModel = NewViewModel(out _);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        Assert.Null(window.FindControl<RadioButton>("TabLicences"));

        var tabs = window.FindControl<TabControl>("Tabs");
        Assert.NotNull(tabs);
        Assert.DoesNotContain(
            tabs!.Items.OfType<TabItem>(),
            tab => tab.Header as string == "Licences");

        // The Settings page, where the way in now is — and drawn there, which is a stronger claim
        // than FindControl can make: the name scope holds every page whether it is realised or not
        // (gotcha 31), so this walks what the window has actually put on screen.
        viewModel.SelectedTab = 5;
        window.UpdateLayout();

        var about = Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.Name == "ShowAbout");

        Assert.Equal("About Uindosill", about.Content);
    }

    [AvaloniaFact]
    public void TheUpdateNoticeIsHiddenWhenThereIsNoUpdate()
    {
        // The banner is the visible half of the update decision, and it must be invisible the rest
        // of the time: a window that always carries a bar about updates is a window with a bar.
        var window = new MainWindow { DataContext = NewViewModel(out _) };
        window.Show();

        var notice = window.FindControl<Border>("UpdateNotice");
        Assert.NotNull(notice);
        Assert.False(notice!.IsVisible);
    }

    [AvaloniaFact]
    public void DropZoneAcceptsDrops()
    {
        var window = new MainWindow { DataContext = NewViewModel(out _) };
        window.Show();

        var dropZone = window.FindControl<Border>("DropZone");
        Assert.NotNull(dropZone);
        Assert.True(Avalonia.Input.DragDrop.GetAllowDrop(dropZone!));
    }

    /// <summary>
    /// The other half of the fix in
    /// <see cref="TranscribeViewModelTests.AFileDroppedWhileTheBatchRunsIsRefusedRatherThanLeftADeadRow"/>.
    /// </summary>
    /// <remarks>
    /// The zone carried <c>AllowDrop="True"</c> unconditionally, so once the view model started
    /// refusing files the UI would still take the gesture and throw it away — the same shape as the
    /// disabled-control convention this window keeps everywhere else, broken in the one place a
    /// user is invited to act. The enabled state is driven from <c>IsRunning</c> here rather than
    /// from a real batch on purpose: what is under test is the binding, and the behaviour behind it
    /// is tested against a batch that really runs.
    /// </remarks>
    [AvaloniaFact]
    public void TheDropZoneShutsWhileABatchRunsAndSaysWhy()
    {
        var viewModel = NewViewModel(out _);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var dropZone = window.FindControl<Border>("DropZone");
        Assert.NotNull(dropZone);
        Assert.True(Avalonia.Input.DragDrop.GetAllowDrop(dropZone!));

        viewModel.Transcribe.IsRunning = true;
        window.UpdateLayout();

        Assert.False(Avalonia.Input.DragDrop.GetAllowDrop(dropZone!));

        // And it says so where the invitation used to be, rather than going quietly dead.
        var lines = dropZone!.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible)
            .Select(t => t.Text)
            .ToList();

        Assert.Contains(lines, line => line is not null && line.Contains("while a batch runs", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line is not null && line.Contains("Drop audio", StringComparison.Ordinal));

        // The extension list goes with it. A zone that is not taking files has no business
        // advertising which ones it takes, and asserting on it is what keeps that binding honest:
        // the list matches neither phrase above, so on its own it could be inverted or deleted
        // with the suite still green.
        Assert.DoesNotContain(lines, line => line == viewModel.Transcribe.SupportedExtensionsHint);

        // Over is over: the queue reopens with the batch, so the refusal is not a dead end.
        viewModel.Transcribe.IsRunning = false;
        window.UpdateLayout();

        Assert.True(Avalonia.Input.DragDrop.GetAllowDrop(dropZone!));

        var reopened = dropZone.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible)
            .Select(t => t.Text)
            .ToList();

        Assert.Contains(reopened, line => line is not null && line.Contains("Drop audio", StringComparison.Ordinal));
        Assert.Contains(reopened, line => line == viewModel.Transcribe.SupportedExtensionsHint);
    }

    [AvaloniaFact]
    public void ModelsTabExposesTheControlsItsOwnTextRefersTo()
    {
        // The provenance notice says an unverified download "requires the explicit unverified
        // opt-in below". The commands and the opt-in existed on the view model and were bound to
        // nothing, so the tab was read-only and the notice pointed at a control that did not
        // exist. Asserting on the bound surface, because the failure was that it was absent.
        var viewModel = NewViewModel(out _);
        viewModel.SelectedTab = 1;

        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        // A TabControl only realises the selected tab, so the Models controls do not exist in the
        // visual tree until that tab is current and a layout pass has run.
        window.UpdateLayout();

        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList();
        var checkboxes = window.GetVisualDescendants().OfType<CheckBox>().Select(c => c.Content as string).ToList();

        Assert.Contains(buttons, c => c is not null && c.Contains("Download", StringComparison.Ordinal));
        Assert.Contains(buttons, c => c is not null && c.Contains("Remove", StringComparison.Ordinal));
        Assert.NotEmpty(checkboxes);

        // The opt-in is found by name and tested by its binding rather than by its label. It used
        // to be matched on the word "unverified", which stopped being in the label when these
        // strings were rewritten for the people who read them — and a label match never tested the
        // thing that was actually broken, which was a control bound to nothing at all.
        var optIn = window.FindControl<CheckBox>("UnverifiedOptIn");
        Assert.NotNull(optIn);

        Assert.False(viewModel.Models.AllowUnverified);
        optIn!.IsChecked = true;
        Assert.True(viewModel.Models.AllowUnverified);
    }

    [AvaloniaFact]
    public void AVerifiedProvenanceLineIsNotPaintedAsAWarning()
    {
        // The flag below is only worth having if the view reads it, and this window has shipped a
        // control bound to nothing before. So this asserts on the rendered brush rather than on
        // the view model: every shipped entry is pinned and checked, so the line must not come out
        // in the warning colour.
        var viewModel = NewViewModel(out _);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        viewModel.SelectedTab = 1;
        viewModel.Models.Selected = viewModel.Models.Models.First();

        var provenance = window.FindControl<TextBlock>("Provenance");
        Assert.NotNull(provenance);
        Assert.True(viewModel.Models.Selected!.ProvenanceIsVerified);
        Assert.Contains("verified", (IEnumerable<string>)provenance!.Classes);

        var brush = Assert.IsType<SolidColorBrush>(provenance.Foreground);
        Assert.Equal(Color.Parse("#4A602C"), brush.Color);
    }

    [AvaloniaFact]
    public void TheAboutWindowCarriesTheFullNoticeInsideTheApplication()
    {
        // The notice has to be present where the material is used, not only in a file in the
        // source repository, so it is asserted on the view model the window renders. It moved off
        // MainWindowViewModel with the Licences tab on 2026-08-23 — the notice is now the About
        // window's second pane, and AboutViewModel is the one builder of it.
        var viewModel = NewViewModel(out _);
        var licence = viewModel.About.LicenceText;

        Assert.Contains("NVIDIA Corporation", licence, StringComparison.Ordinal);
        Assert.Contains("Modified:", licence, StringComparison.Ordinal);
        Assert.Contains("without warranties", licence, StringComparison.Ordinal);
        Assert.Contains("creativecommons.org/licenses/by/4.0", licence, StringComparison.Ordinal);
        Assert.Contains("technological measures", licence, StringComparison.Ordinal);
        Assert.Contains("MIT", licence, StringComparison.Ordinal);

        // The one non-MIT component, and its qualifying note. This panel used to render the
        // component line and drop the note, so both are asserted here rather than one standing
        // in for the other.
        Assert.Contains("NVIDIA CUDA Toolkit EULA", licence, StringComparison.Ordinal);
        Assert.Contains("opt-in CUDA backend", licence, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindowUnloadsTheModelAndReleasesTheBackendBeforeItGoes()
    {
        // The app used to reach native static teardown with a CUDA backend still resident and abort
        // with 0xC0000409 on exit (gotcha 19). The window now turns the first close into a shutdown
        // — unload, release the backend — and only then closes. Asserted on the real window's
        // Closing path, not on the view model method it calls.
        var provider = new FakeEngineProvider();
        var directory = Directory.CreateTempSubdirectory("uindosill-app").FullName;
        var viewModel = new MainWindowViewModel(provider, new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        await viewModel.Session.LoadAsync(new EngineSelection { Model = viewModel.Models.SelectedDescriptor });

        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();

        // The first request is intercepted; the real close follows once shutdown has run, so this
        // pumps the dispatcher until it does rather than asserting on the same call stack.
        for (var i = 0; i < 200 && !closed; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(closed, "the window never closed after shutdown");
        Assert.False(viewModel.Session.IsLoaded);
        Assert.Equal(1, provider.ReleaseCount);
    }

    internal static MainWindowViewModel NewViewModel(out string directory)
    {
        directory = Directory.CreateTempSubdirectory("uindosill-app").FullName;
        return new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
    }

    private static MainWindowViewModel NewViewModel(IAppUpdater updater)
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-app").FullName;
        return new MainWindowViewModel(
            new FakeEngineProvider(),
            new LocalModelStore(directory),
            ModelCatalog.Default,
            updater,
            new AppSettingsStore(Path.Combine(directory, "settings.json")), player: new FakeMediaPlayer());
    }

    /// <summary>
    /// The update offer is bound rather than commanded, and this is why.
    /// </summary>
    /// <remarks>
    /// The commands were first generated with a <c>CanExecute</c> and the buttons bound only
    /// <c>Command</c>. A generated <c>CanExecute</c> is re-queried only when the command is told to,
    /// and nothing was telling it — so the button was disabled at construction and stayed disabled
    /// after a check found an update. Every view-model test passed, because
    /// <c>IAsyncRelayCommand.ExecuteAsync</c> does not consult <c>CanExecute</c>: they were all
    /// reaching around the one thing that was broken. This asserts the enabled state of the control
    /// a person actually presses.
    /// </remarks>
    [AvaloniaFact]
    public async Task TheUpdateButtonIsDeadUntilThereIsAnUpdateAndThenLive()
    {
        // Nothing newer to begin with. Showing the window runs the launch check, so by the time
        // this looks at the button the check has already happened and found nothing.
        var updater = new FakeUpdater { Available = null };
        var viewModel = NewViewModel(updater);
        viewModel.SelectedTab = 3;

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        window.UpdateLayout();

        var button = window.FindControl<Button>("InstallUpdate");
        var notice = window.FindControl<Border>("UpdateNotice");
        Assert.NotNull(button);
        Assert.NotNull(notice);

        Assert.Equal(1, updater.Checks);
        Assert.False(button!.IsEnabled);
        Assert.False(notice!.IsVisible);

        // Now there is. The offer has to become live off the back of the property changing.
        updater.Available = "1.1.0";
        await viewModel.Updates.CheckCommand.ExecuteAsync(null);
        window.UpdateLayout();

        Assert.True(button.IsEnabled);
        Assert.True(notice.IsVisible);
    }
}

public class ShutdownTests
{
    /// <summary>
    /// A provider that records whether a batch was still running at the moment the backend was
    /// released — the ordering the whole fix depends on, and the one thing a call count cannot show.
    /// </summary>
    private sealed class OrderRecordingProvider : IEngineProvider
    {
        private readonly FakeEngineProvider _inner = new(new FakeEngineOptions
        {
            PerSegmentDelay = TimeSpan.FromMilliseconds(100),
        });

        public Func<bool>? IsBatchRunning { get; set; }

        public bool? BatchWasRunningAtRelease { get; private set; }

        public int ReleaseCount { get; private set; }

        public bool IsModelAvailable(EngineSelection selection) => true;

        public ITranscriptionEngine Create(EngineSelection selection) => _inner.Create(selection);

        public bool SupportsSpeakerLabelling => _inner.SupportsSpeakerLabelling;

        public Parakeet.Core.Diarisation.ISpeakerLabeller? CreateSpeakerLabeller() => _inner.CreateSpeakerLabeller();

        public Parakeet.Core.Diarisation.SpeakerLabellerLimits? SpeakerLimits => _inner.SpeakerLimits;

        public string? DescribeLabeller(Parakeet.Core.Diarisation.ISpeakerLabeller labeller) =>
            _inner.DescribeLabeller(labeller);

        public string? DescribeUnavailable(Parakeet.Core.Models.ModelTask task) =>
            _inner.DescribeUnavailable(task);

        public string? DescribeTranslator(Parakeet.Core.Translation.ITranscriptTranslator translator) =>
            _inner.DescribeTranslator(translator);

        public bool SupportsTranslation => _inner.SupportsTranslation;

        public Parakeet.Core.Translation.ITranscriptTranslator? CreateTranslator() => _inner.CreateTranslator();

        public bool SupportsNeuralSpeechDetection => _inner.SupportsNeuralSpeechDetection;

        public Parakeet.Core.Segmentation.ISpeechDetector? CreateSpeechDetector() => _inner.CreateSpeechDetector();

        public void ReleaseBackend()
        {
            ReleaseCount++;
            BatchWasRunningAtRelease = IsBatchRunning?.Invoke();
        }
    }

    [Fact]
    public async Task DisposingTheSessionUnloadsThenReleasesTheBackend()
    {
        var provider = new FakeEngineProvider();
        var session = new ModelSession(provider);
        await session.LoadAsync(new EngineSelection { Model = ModelCatalog.Default.Models[0] });

        Assert.True(session.IsLoaded);
        Assert.Equal(0, provider.ReleaseCount);

        await session.DisposeAsync();

        Assert.False(session.IsLoaded);
        Assert.Equal(1, provider.ReleaseCount);

        // Nothing loaded is not an error; the release is still made, because a failed load can
        // leave the native backend resident with no engine to show for it.
        var empty = new ModelSession(provider);
        await empty.DisposeAsync();
        Assert.Equal(2, provider.ReleaseCount);
    }

    [Fact]
    public async Task ShutdownStopsARunningBatchBeforeReleasingTheBackend()
    {
        // The ABI has no abort hook, so shutdown cancels the batch and then waits for it, and only
        // then disposes the session. Releasing the backend under a running decode would let the
        // decode recreate it, and the exit abort would come back.
        var provider = new OrderRecordingProvider();
        var directory = Directory.CreateTempSubdirectory("uindosill-shutdown").FullName;
        var main = new MainWindowViewModel(provider, new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        provider.IsBatchRunning = () => main.Transcribe.IsRunning;
        main.Transcribe.OutputDirectory = directory;
        main.Transcribe.UseFixedWindows = true;
        main.Transcribe.MaxSegmentSeconds = 5;

        await main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor });

        // Thirty seconds of tone in five-second windows: six segments at 100 ms each, long enough
        // that shutdown arrives while the batch is genuinely in flight.
        var path = Path.Combine(directory, "long.wav");
        var samples = new float[16_000 * 30];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 200 * i / 16_000.0));
        }

        WavWriter.WriteFile(path, samples, 16_000);
        main.Transcribe.AddFiles([path]);

        var batch = main.Transcribe.StartCommand.ExecuteAsync(null);
        Assert.True(main.Transcribe.IsRunning);

        await main.ShutdownAsync();
        await batch;

        Assert.False(main.Transcribe.IsRunning);
        Assert.False(main.Session.IsLoaded);
        Assert.Equal(1, provider.ReleaseCount);
        Assert.False(provider.BatchWasRunningAtRelease);
        Assert.Contains(main.Transcribe.Jobs, job => job.State != JobState.Completed);
    }
}

public class TranscribeViewModelTests
{
    private static (TranscribeViewModel ViewModel, string Directory) Create()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-vm").FullName;
        var main = new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        main.Transcribe.OutputDirectory = directory;

        // Start refuses without a loaded model, so these tests load one the way the window does.
        // Waiting on the task rather than firing it: an unawaited load races the Start that follows.
        main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor })
            .GetAwaiter().GetResult();

        return (main.Transcribe, directory);
    }

    private static string WriteWav(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        var samples = new float[16_000 * 4];
        var random = new Random(5);

        for (var i = 0; i < samples.Length; i++)
        {
            var second = i / 16_000.0;
            samples[i] = second is > 0.5 and < 3.2
                ? (float)(0.5 * Math.Sin(2 * Math.PI * 200 * i / 16_000.0))
                : (float)(random.NextDouble() * 0.001 - 0.0005);
        }

        WavWriter.WriteFile(path, samples, 16_000);
        return path;
    }

    /// <summary>
    /// A plain tone of a chosen length. Thirty seconds of it in five-second fixed windows is six
    /// decodes, which is long enough that the batch is genuinely mid-flight when the test looks.
    /// </summary>
    private static string WriteTone(string directory, string name, int seconds)
    {
        var path = Path.Combine(directory, name);
        var samples = new float[16_000 * seconds];

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 200 * i / 16_000.0));
        }

        WavWriter.WriteFile(path, samples, 16_000);
        return path;
    }

    [Fact]
    public void AddingTheSameFileTwiceQueuesItOnce()
    {
        var (viewModel, directory) = Create();
        var path = WriteWav(directory, "a.wav");

        viewModel.AddFiles([path, path]);

        Assert.Single(viewModel.Jobs);
        Assert.True(viewModel.HasJobs);
    }

    [Fact]
    public void FilesThatDoNotExistAreReportedRatherThanQueued()
    {
        var (viewModel, directory) = Create();

        viewModel.AddFiles([Path.Combine(directory, "nope.wav")]);

        Assert.Empty(viewModel.Jobs);
        Assert.Contains("not found", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileDroppedWhileTheBatchRunsIsRefusedRatherThanLeftADeadRow()
    {
        // Queue one long file, press Start, drop a second one on the zone while the first decodes.
        // Start works from a snapshot of the queue taken before the first file is opened, so the
        // new row was in neither that snapshot nor the results the batch reconciles against at the
        // end: it sat blank at "Waiting" for ever beside "Finished 1 file." — the silent dead row
        // that reconciliation was written to prevent, arriving by the one door it does not cover.
        // Adding is refused while a batch runs now, the way Clear already was, and says so.
        var directory = Directory.CreateTempSubdirectory("uindosill-vm").FullName;
        var provider = new FakeEngineProvider(new FakeEngineOptions
        {
            PerSegmentDelay = TimeSpan.FromMilliseconds(100),
        });

        var main = new MainWindowViewModel(provider, new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        var viewModel = main.Transcribe;
        viewModel.OutputDirectory = directory;
        viewModel.UseFixedWindows = true;
        viewModel.MaxSegmentSeconds = 5;
        await main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor });

        var queued = WriteTone(directory, "queued.wav", seconds: 30);
        var dropped = WriteTone(directory, "dropped.wav", seconds: 4);
        viewModel.AddFiles([queued]);

        var batch = viewModel.StartCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsRunning);

        // The drop: what MainWindow's OnDrop hands over, with the batch in flight.
        viewModel.AddFiles([dropped]);

        Assert.Single(viewModel.Jobs);
        Assert.Contains("A batch is running", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("while a batch runs", viewModel.DropHint, StringComparison.Ordinal);

        await batch;

        // Nothing extra in the queue, nothing left at "Waiting", and no output for a file the
        // batch never took on.
        var job = Assert.Single(viewModel.Jobs);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("Finished 1 file.", viewModel.StatusMessage);
        Assert.False(File.Exists(Path.Combine(directory, "dropped.txt")));

        // Over is over: the queue reopens with the batch, so the refusal is not a dead end.
        Assert.False(viewModel.IsRunning);
        Assert.Contains("Drop audio", viewModel.DropHint, StringComparison.Ordinal);

        viewModel.AddFiles([dropped]);
        Assert.Equal(2, viewModel.Jobs.Count);
    }

    [Fact]
    public async Task StartLoadsTheModelItselfAndRefusesOnlyWhenNoneIsInstalled()
    {
        // Start refused with "open the Models tab and press Load" until 2026-08-23, which is a
        // second button for a decision already made by pressing this one. It loads for itself now.
        // What it deliberately does NOT do is load at launch: a load fixes the compute backend for
        // the rest of the process, so it waits for somebody to actually ask for work.
        var directory = Directory.CreateTempSubdirectory("uindosill-vm").FullName;
        var engines = new FakeEngineProvider { ModelAvailable = false };
        var main = new MainWindowViewModel(
            engines, new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        main.Transcribe.OutputDirectory = directory;
        main.Transcribe.AddFiles([WriteWav(directory, "a.wav")]);

        // Nothing on disk is the one state that still refuses, and it names the thing to do.
        Assert.False(main.Transcribe.IsModelLoaded);
        Assert.False(main.Transcribe.CanStart);
        Assert.Contains("No model is installed", main.Transcribe.StartHint, StringComparison.Ordinal);

        await main.Transcribe.StartCommand.ExecuteAsync(null);

        Assert.Contains("No model is installed", main.Transcribe.StatusMessage, StringComparison.Ordinal);
        Assert.All(main.Transcribe.Jobs, job => Assert.Equal(JobState.Pending, job.State));
        Assert.False(File.Exists(Path.Combine(directory, "a.txt")));

        // Weights arriving turn the button on without anything being loaded.
        engines.ModelAvailable = true;
        main.Transcribe.RefreshModelAvailability();

        Assert.False(main.Transcribe.IsModelLoaded);
        Assert.True(main.Transcribe.CanStart);
        Assert.Null(main.Transcribe.StartHint);

        // And Start does the loading on its way past.
        await main.Transcribe.StartCommand.ExecuteAsync(null);

        Assert.True(main.Transcribe.IsModelLoaded);
        Assert.Equal(JobState.Completed, main.Transcribe.Jobs[0].State);
        Assert.NotEmpty(main.Transcribe.Jobs[0].Transcript);

        // Unloading does not take the button away any more: the weights are still on disk, so the
        // next Start loads them again rather than being dead until somebody visits the other tab.
        await main.Session.UnloadAsync();

        Assert.False(main.Transcribe.IsModelLoaded);
        Assert.Single(main.Transcribe.Jobs);
    }

    [Fact]
    public async Task TheTranscriptPaneFillsWhileTheFileIsStillDecoding()
    {
        // The pane draws JobViewModel.Lines, and until 2026-08-23 that collection was filled in one
        // go by Complete(). The streamed text went into LiveTranscript, which no view has ever bound
        // — `git log -S LiveTranscript -- '*.axaml'` finds nothing in any commit — so a file being
        // transcribed showed a progress bar and nothing to read, for as long as the decode took.
        var directory = Directory.CreateTempSubdirectory("uindosill-vm").FullName;
        var provider = new FakeEngineProvider(new FakeEngineOptions
        {
            PerSegmentDelay = TimeSpan.FromMilliseconds(100),
        });

        var main = new MainWindowViewModel(
            provider, new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        var viewModel = main.Transcribe;
        viewModel.OutputDirectory = directory;
        viewModel.UseFixedWindows = true;
        viewModel.MaxSegmentSeconds = 5;
        await main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor });

        viewModel.AddFiles([WriteTone(directory, "long.wav", seconds: 60)]);
        var job = viewModel.Jobs[0];

        var batch = viewModel.StartCommand.ExecuteAsync(null);

        // Rows have to appear before the batch ends, so the state is sampled at the moment they do
        // rather than asserted afterwards — by then the run has finished either way.
        var sawRowsWhileRunning = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (job.Lines.Count > 0)
            {
                sawRowsWhileRunning = viewModel.IsRunning;
                break;
            }

            if (!viewModel.IsRunning)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.True(sawRowsWhileRunning, "the transcript pane had no rows while the file was decoding");

        await batch;

        // And Complete() still rebuilds them, so the finished transcript is the one on screen.
        Assert.Equal(JobState.Completed, job.State);
        Assert.NotEmpty(job.Lines);
    }

    [Fact]
    public async Task TheRowsThatFillWhileDecodingAreAlreadyOnePerSentence()
    {
        // Two places turn a segment into rows — the pane filling mid-decode above, and the rebuild
        // Complete() does — and since 2026-08-23 both go through TranscriptLineViewModel.LinesFor,
        // which cuts a segment at its sentences. This drives the first through the fake engine with
        // a two-sentence phrase and reads the rows the moment they appear, before the rebuild can
        // have happened: a transcript that re-cut itself when the decode ended would read as a
        // defect, and nothing but this would see it.
        var directory = Directory.CreateTempSubdirectory("uindosill-vm").FullName;
        var provider = new FakeEngineProvider(new FakeEngineOptions
        {
            PerSegmentDelay = TimeSpan.FromMilliseconds(100),
            Phrases = ["One here. Two there."],
        });

        var main = new MainWindowViewModel(
            provider, new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        var viewModel = main.Transcribe;
        viewModel.OutputDirectory = directory;
        viewModel.UseFixedWindows = true;
        viewModel.MaxSegmentSeconds = 5;
        await main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor });

        viewModel.AddFiles([WriteTone(directory, "long.wav", seconds: 60)]);
        var job = viewModel.Jobs[0];

        var batch = viewModel.StartCommand.ExecuteAsync(null);

        List<string>? firstRows = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (job.Lines.Count > 0 && viewModel.IsRunning)
            {
                firstRows = [.. job.Lines.Select(l => l.Text)];
                break;
            }

            if (!viewModel.IsRunning)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(firstRows);
        // Rows are drawn without their sentence-final stop, as subtitles are (TranscriptLineTests).
        Assert.Equal("One here", firstRows[0]);
        Assert.All(firstRows, row => Assert.True(row is "One here" or "Two there", $"a mid-decode row held more than a sentence: '{row}'"));

        await batch;

        // Two rows per decoded segment — however many the fixed windows made of a flat tone, which
        // is not twelve: on audio with no quietest frame the forced cut lands at the start of its
        // search window — and the rebuild cut them the same way the stream did.
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(2 * job.Document!.Segments.Count(s => !s.IsEmpty), job.Lines.Count);
        Assert.All(job.Lines, line => Assert.True(line.Text is "One here" or "Two there"));
    }

    [Fact]
    public void ASecondPassClearsTheBarTheDecodeLeftFull()
    {
        // The decode ends at 100%. Speaker labelling then reads and resamples the whole file again
        // before its own first report arrives — minutes on a long recording — and the row used to
        // keep the full bar under the new status for all of it, which is the shape of a hang and
        // was mistaken for one.
        var job = new JobViewModel("a.wav");
        job.Apply(new TranscriptionProgress
        {
            Stage = TranscriptionStage.Decoding,
            Processed = TimeSpan.FromMinutes(10),
            Total = TimeSpan.FromMinutes(10),
        });

        Assert.Equal(100, job.Progress);
        Assert.False(job.IsIndeterminate);

        job.BeginPass("Labelling speakers");

        Assert.Equal("Labelling speakers", job.Status);
        Assert.Equal(0, job.Progress);
        Assert.True(job.IsIndeterminate);

        // The staging half names itself, so the two pieces of work under one stage are told apart.
        job.Apply(new TranscriptionProgress
        {
            Stage = TranscriptionStage.LabellingSpeakers,
            Detail = "Labelling speakers — reading the audio again",
            Processed = TimeSpan.FromMinutes(5),
            Total = TimeSpan.FromMinutes(10),
        });

        Assert.Equal("Labelling speakers — reading the audio again", job.Status);
        Assert.Equal(50, job.Progress);
        Assert.False(job.IsIndeterminate);

        // And the sidecar's own reports fall back to the stage's name.
        job.Apply(new TranscriptionProgress
        {
            Stage = TranscriptionStage.LabellingSpeakers,
            Processed = TimeSpan.FromMinutes(2),
            Total = TimeSpan.FromMinutes(10),
        });

        Assert.Equal("Labelling speakers", job.Status);
        Assert.Equal(20, job.Progress);
    }

    [Fact]
    public void TheModelsTabShowsAndRemovesWeightsTheCatalogueDoesNotClaim()
    {
        // Four quantisations were withdrawn from the catalogue on 2026-08-20 and stayed on disk.
        // This tab names the model directory at the top of itself and then described only part of
        // its contents, so it sat beside gigabytes it would neither show nor remove.
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        File.WriteAllText(Path.Combine(directory, "tdt-0.6b-v3-q8_0.gguf"), "withdrawn weights");

        var main = new MainWindowViewModel(
            new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());

        Assert.True(main.Models.HasSideloaded);
        var file = Assert.Single(main.Models.Sideloaded);
        Assert.Equal("tdt-0.6b-v3-q8_0.gguf", file.FileName);
        Assert.Contains("no entry above accounts for", main.Models.SideloadedSummary, StringComparison.Ordinal);

        // The uninstall notice measures the folder rather than repeating a sentence typed once.
        Assert.Contains("comes to", main.Models.UninstallNotice, StringComparison.Ordinal);
        Assert.DoesNotContain("the three of them", main.Models.UninstallNotice, StringComparison.Ordinal);

        // And it says the weights survive, which is true again: the uninstall hook that deleted
        // them existed for one night and was removed. This line is the only place the window tells
        // anyone what becomes of gigabytes of their disk, so it is asserted rather than trusted.
        Assert.Contains("does not delete downloaded models", main.Models.UninstallNotice, StringComparison.Ordinal);

        main.Models.SelectedSideloaded = file;
        Assert.True(main.Models.CanRemoveSideloaded);

        main.Models.RemoveSideloadedCommand.Execute(null);

        Assert.False(File.Exists(Path.Combine(directory, "tdt-0.6b-v3-q8_0.gguf")));
        Assert.False(main.Models.HasSideloaded);
        Assert.Empty(main.Models.Sideloaded);
    }

    [Fact]
    public void LoadSaysWhyItIsDarkOnAModelItCannotLoad()
    {
        // The LOADED MODEL panel is the window's one ASR engine, and it used to be drawn inside the
        // per-entry detail pane — so selecting Speaker labelling put a Backend picker and a dead
        // Load button underneath it, reading as that model's backend. It is neither: the diariser
        // picks its own provider inside the sidecar, and CanLoad has always required a
        // transcription entry. The panel moved out of the pane, and the button now says so.
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var main = new MainWindowViewModel(
            new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());

        var diariser = main.Models.Models.First(m => m.Descriptor.Task == ModelTask.Diarisation);
        main.Models.Selected = diariser;

        Assert.False(main.Models.CanLoad);
        var hint = main.Models.LoadHint;
        Assert.NotNull(hint);
        Assert.Contains("turns speech into text", hint, StringComparison.Ordinal);
        Assert.Contains("'Label speakers'", hint, StringComparison.Ordinal);

        // The translation entry gets its own opt-in named rather than the diariser's.
        main.Models.Selected = main.Models.Models.First(m => m.Descriptor.Task == ModelTask.Translation);
        Assert.Contains("'Translate to English'", main.Models.LoadHint, StringComparison.Ordinal);

        // And a transcription entry that is simply not downloaded says that instead.
        main.Models.Selected = main.Models.Models.First(m => m.IsTranscriptionModel);
        Assert.Equal("Download it first.", main.Models.LoadHint);
    }

    [Fact]
    public void TheLoadedSummaryNoLongerTellsPeopleToPressLoadFirst()
    {
        // Start loads the model itself as of 2026-08-23, so "Choose a model and press Load before
        // transcribing" was describing a refusal that no longer happens.
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var main = new MainWindowViewModel(
            new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());

        Assert.False(main.Models.IsLoaded);
        Assert.DoesNotContain("press Load before transcribing", main.Models.LoadedSummary, StringComparison.Ordinal);
        Assert.Contains("Transcribing loads it", main.Models.LoadedSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningTheModelsTabRereadsTheFolder()
    {
        // Every fact on the tab was established once, at construction, and Refresh() existed with
        // nothing calling it — so weights arriving from anywhere but this window's own Download
        // button stayed invisible until a restart.
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var main = new MainWindowViewModel(
            new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());

        Assert.False(main.Models.HasSideloaded);

        // Something else puts a file there: another copy of the application, or an older version.
        File.WriteAllText(Path.Combine(directory, "left-behind.gguf"), "weights");

        Assert.False(main.Models.HasSideloaded);

        main.SelectedTab = 1;

        Assert.True(main.Models.HasSideloaded);
        Assert.Equal("left-behind.gguf", Assert.Single(main.Models.Sideloaded).FileName);
    }

    [Fact]
    public async Task RunningTheQueueFillsTranscriptsAndExportWritesTheFiles()
    {
        var (viewModel, directory) = Create();
        viewModel.AddFiles([WriteWav(directory, "a.wav"), WriteWav(directory, "b.wav")]);

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.All(viewModel.Jobs, job => Assert.Equal(JobState.Completed, job.State));
        Assert.All(viewModel.Jobs, job => Assert.NotEmpty(job.Transcript));
        Assert.False(viewModel.IsRunning);

        // The run wrote nothing: files are the Export button's business, for the selected row.
        Assert.False(File.Exists(Path.Combine(directory, "a.srt")));

        viewModel.SelectedJob = viewModel.Jobs[0];
        Assert.True(viewModel.CanExportFiles);
        await viewModel.ExportFilesCommand.ExecuteAsync(null);

        // The default tick is SRT alone, so one press writes exactly one file and nothing else.
        Assert.True(File.Exists(Path.Combine(directory, "a.srt")));
        Assert.False(File.Exists(Path.Combine(directory, "a.txt")));
        Assert.Contains("Wrote 1 file", viewModel.ExportNotice, StringComparison.Ordinal);

        // And only for it: the other row's files wait for their own press.
        Assert.False(File.Exists(Path.Combine(directory, "b.srt")));
    }

    [Fact]
    public async Task StartRunsWhatHasNotBeenRunAndLeavesFinishedRowsAlone()
    {
        // Start used to hand the whole queue to the runner and reset every row on the way, so
        // adding a fourth file to a queue of three re-decoded the three — minutes a file, and a
        // second copy of every output beside the first. It runs what has not been run now, and a
        // row that failed is not one of those: pressing Start after a failure retries it.
        var (viewModel, directory) = Create();
        var good = WriteWav(directory, "good.wav");
        var broken = Path.Combine(directory, "broken.wav");
        await File.WriteAllTextAsync(broken, "this is not a wave file at all");

        viewModel.AddFiles([good, broken]);
        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.Equal(JobState.Failed, viewModel.Jobs[1].State);

        var transcript = viewModel.Jobs[0].Transcript;
        Assert.NotEmpty(transcript);

        // Repair the unreadable one and add a third file: the two that have work to do run, and
        // the one that is done is not touched.
        WriteWav(directory, "broken.wav");
        viewModel.AddFiles([WriteWav(directory, "late.wav")]);
        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.All(viewModel.Jobs, job => Assert.Equal(JobState.Completed, job.State));
        Assert.All(viewModel.Jobs, job => Assert.NotEmpty(job.Transcript));

        // The finished row kept everything a finished row has, untouched by the second batch.
        Assert.Equal(transcript, viewModel.Jobs[0].Transcript);
        Assert.Equal("Done", viewModel.Jobs[0].Status);

        // And the count is of what ran, with what was skipped said rather than left to be guessed
        // at from a queue of three reporting two.
        Assert.Equal("Finished 2 files. 1 already transcribed and left alone.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task AFinishedQueueTurnsStartOffAndSaysWhichButtonRunsItAgain()
    {
        // The other half of the decision: Start is disabled once there is nothing left to run,
        // because a live button that does nothing is the Phase 4 defect this window already fixed
        // once. 'Run again' is the way back, and it is the only way.
        var (viewModel, directory) = Create();
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);

        Assert.True(viewModel.CanStart);
        Assert.False(viewModel.CanRunAgain);

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasWorkToDo);
        Assert.False(viewModel.CanStart);
        Assert.True(viewModel.CanRunAgain);
        Assert.Contains("'Run again'", viewModel.StartHint, StringComparison.Ordinal);

        // Pressing Start anyway — the command runs even when its button is off — runs nothing
        // and says so instead of quietly re-transcribing.
        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Contains("transcribed already", viewModel.StatusMessage, StringComparison.Ordinal);

        // 'Run again' puts the row back to waiting and runs it — asked for, this time.
        await viewModel.RunAgainCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.NotEmpty(viewModel.Jobs[0].Transcript);
        Assert.Equal("Finished 1 file.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task OneUnreadableFileDoesNotStopTheOthers()
    {
        var (viewModel, directory) = Create();
        var broken = Path.Combine(directory, "broken.wav");
        await File.WriteAllTextAsync(broken, "this is not a wave file at all");

        viewModel.AddFiles([broken, WriteWav(directory, "good.wav")]);
        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Failed, viewModel.Jobs[0].State);
        Assert.NotNull(viewModel.Jobs[0].Error);
        Assert.Equal(JobState.Completed, viewModel.Jobs[1].State);
        Assert.NotEmpty(viewModel.Jobs[1].Transcript);
    }

    [Fact]
    public async Task TheSpeakerOptInIsOffByDefaultAndNamesTheVoicesWhenOn()
    {
        var (viewModel, directory) = Create();
        Assert.False(viewModel.LabelSpeakers);
        Assert.True(viewModel.CanLabelSpeakers);   // the fake provider has a labeller
        Assert.Null(viewModel.SpeakerHint);

        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        foreach (var format in viewModel.Formats)
        {
            format.IsSelected = format.Id is "txt" or "rttm";
        }

        // Off: nothing about speakers anywhere — and exporting with RTTM ticked skips it with the
        // reason beside the button, rather than refusing the run up front or writing an empty
        // file. The finished document answers what Start could only predict.
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.DoesNotContain("Speaker", viewModel.Jobs[0].Transcript, StringComparison.Ordinal);

        viewModel.SelectedJob = viewModel.Jobs[0];
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(directory, "a.txt")));
        Assert.False(File.Exists(Path.Combine(directory, "a.rttm")));
        Assert.Contains("no RTTM", viewModel.ExportNotice, StringComparison.Ordinal);

        // On: the window's transcript and the exported files carry the names. Through 'Run
        // again', because the row is finished and Start runs only what has not been run — turning
        // the opt-in on and asking for the same file back is exactly what that button is for. The
        // count comes with it, because the opt-in no longer runs without one.
        viewModel.LabelSpeakers = true;
        viewModel.SpeakerCount = 2;
        Assert.False(viewModel.CanStart);
        Assert.True(viewModel.CanRunAgain);

        await viewModel.RunAgainCommand.ExecuteAsync(null);
        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.Contains("Speaker 1: ", viewModel.Jobs[0].Transcript, StringComparison.Ordinal);

        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.Contains("Speaker 1: ", await File.ReadAllTextAsync(Path.Combine(directory, "a (2).txt")), StringComparison.Ordinal);
        Assert.StartsWith("SPEAKER a 1 0.000", await File.ReadAllTextAsync(Path.Combine(directory, "a.rttm")), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutALabellerTheRttmFormatIsNotEvenOffered()
    {
        // It could only ever write an empty file there, so the checkbox is absent rather than a trap.
        var withLabeller = new TranscribeViewModel(new FakeEngineProvider(), () => new EngineSelection());
        Assert.Contains(withLabeller.Formats, f => f.Id == "rttm");

        var without = new TranscribeViewModel(
            new EngineProvider(new LocalModelStore(Directory.CreateTempSubdirectory("uindosill-vm").FullName), () => true),
            () => new EngineSelection());
        Assert.DoesNotContain(without.Formats, f => f.Id == "rttm");
    }

    [Fact]
    public async Task ALoadedModelStartsEvenWhenTheSelectedRowIsNotOne()
    {
        // The loaded engine is what runs, not whichever row is highlighted; a selection the engine
        // provider will not build must not be reported as "no model is installed".
        var directory = Directory.CreateTempSubdirectory("uindosill-vm").FullName;
        var main = new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        main.Transcribe.OutputDirectory = directory;
        await main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor });
        main.Models.Selected = null;   // nothing highlighted: the session still holds the engine

        main.Transcribe.AddFiles([WriteWav(directory, "a.wav")]);
        await main.Transcribe.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, main.Transcribe.Jobs[0].State);
        Assert.DoesNotContain("No model is installed", main.Transcribe.StatusMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheDiarisationModelTheOptInIsDisabledWithAReasonAUserCanActOn()
    {
        var viewModel = new TranscribeViewModel(new EngineProvider(new LocalModelStore(Directory.CreateTempSubdirectory("uindosill-vm").FullName), () => true), () => new EngineSelection());

        Assert.False(viewModel.CanLabelSpeakers);
        Assert.Contains("not installed", viewModel.SpeakerHint, StringComparison.Ordinal);
        Assert.Contains("Models tab", viewModel.SpeakerHint, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheSpeechDetectionModelTheOptInIsDisabledWithAReasonAUserCanActOn()
    {
        // The third opt-in on the first two's terms — and with one reason rather than two, because
        // the detector runs in this process: a missing Python is not its problem and a hint that
        // mentioned one would send somebody to repair a thing that is not broken.
        var viewModel = new TranscribeViewModel(new EngineProvider(new LocalModelStore(Directory.CreateTempSubdirectory("uindosill-vm").FullName), () => false), () => new EngineSelection());

        Assert.False(viewModel.CanUseNeuralSpeechDetection);
        Assert.Contains("not installed", viewModel.SpeechDetectionHint, StringComparison.Ordinal);
        Assert.Contains("Models tab", viewModel.SpeechDetectionHint, StringComparison.Ordinal);
        Assert.DoesNotContain("Python", viewModel.SpeechDetectionHint, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallingTheSpeechDetectionModelIsWhatTurnsTheOptInOnAndFixedWindowsAreSaidToMakeItInert()
    {
        // A file of the right name is enough here: nothing loads it until Start, and what is under
        // test is the wiring between the store and the checkbox — and the one case where the box is
        // live but inert, which the hint names instead of leaving a ticked box that changes nothing.
        var directory = Directory.CreateTempSubdirectory("uindosill-vad").FullName;
        var store = new LocalModelStore(directory);
        var model = Assert.Single(ModelCatalog.Default.VoiceActivityModels);

        var before = new TranscribeViewModel(new EngineProvider(store, () => true), () => new EngineSelection());
        Assert.False(before.CanUseNeuralSpeechDetection);
        Assert.False(before.UseNeuralSpeechDetection);

        File.WriteAllText(store.PathFor(model), "not really a graph");

        // Ticked, not merely enabled: on is the default whenever the model is there.
        var after = new TranscribeViewModel(new EngineProvider(store, () => true), () => new EngineSelection());
        Assert.True(after.CanUseNeuralSpeechDetection);
        Assert.True(after.UseNeuralSpeechDetection);
        Assert.Null(after.SpeechDetectionHint);

        after.UseFixedWindows = true;
        Assert.Contains("Fixed windows are on", after.SpeechDetectionHint, StringComparison.Ordinal);

        after.UseFixedWindows = false;
        Assert.Null(after.SpeechDetectionHint);
    }

    [Fact]
    public void TheDetectorIsOnByDefaultWhenItsModelIsThereAndAnAnswerAgainstItSurvivesTheModelComingAndGoing()
    {
        // The box follows the model — off with nothing behind it, on the moment the model arrives
        // from the Models tab — because on is the default; and an answer the user gave is theirs:
        // untick it, remove the model, install it again, and it comes back unticked rather than
        // reset to the default. The same in the other direction, because the answer is a choice
        // and not a ratchet.
        var directory = Directory.CreateTempSubdirectory("uindosill-vad").FullName;
        var store = new LocalModelStore(directory);
        var model = Assert.Single(ModelCatalog.Default.VoiceActivityModels);
        var path = store.PathFor(model);

        var viewModel = new TranscribeViewModel(new EngineProvider(store, () => true), () => new EngineSelection());
        Assert.False(viewModel.UseNeuralSpeechDetection);

        File.WriteAllText(path, "not really a graph");
        viewModel.RefreshSpeechDetectionAvailability();
        Assert.True(viewModel.UseNeuralSpeechDetection);

        viewModel.UseNeuralSpeechDetection = false;
        File.Delete(path);
        viewModel.RefreshSpeechDetectionAvailability();
        Assert.False(viewModel.UseNeuralSpeechDetection);

        File.WriteAllText(path, "not really a graph");
        viewModel.RefreshSpeechDetectionAvailability();
        Assert.False(viewModel.UseNeuralSpeechDetection);

        viewModel.UseNeuralSpeechDetection = true;
        File.Delete(path);
        viewModel.RefreshSpeechDetectionAvailability();
        Assert.False(viewModel.UseNeuralSpeechDetection);

        File.WriteAllText(path, "not really a graph");
        viewModel.RefreshSpeechDetectionAvailability();
        Assert.True(viewModel.UseNeuralSpeechDetection);
    }

    [Fact]
    public async Task TickingNeuralDetectionHandsTheEngineOneDetectorForTheBatchAndAStreamPerFile()
    {
        // The window's half of the detector contract: one detector per batch, created only when the
        // box is ticked and fixed windows are off, disposed with the batch — and the engine opens a
        // stream on it per file, at the file's rate, and closes each.
        var directory = Directory.CreateTempSubdirectory("uindosill-vm").FullName;
        var provider = new FakeEngineProvider();
        var main = new MainWindowViewModel(
            provider, new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        var viewModel = main.Transcribe;
        viewModel.OutputDirectory = directory;
        await main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor });

        viewModel.AddFiles([WriteWav(directory, "a.wav"), WriteWav(directory, "b.wav")]);

        // Ticked under fixed windows: no detector is loaded, because none would decide anything.
        viewModel.UseNeuralSpeechDetection = true;
        viewModel.UseFixedWindows = true;
        viewModel.MaxSegmentSeconds = 5;
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Null(provider.LastSpeechDetector);

        // Ticked with detection on: one detector, two streams, both closed, the detector disposed
        // with the batch.
        viewModel.UseFixedWindows = false;
        await viewModel.RunAgainCommand.ExecuteAsync(null);

        var detector = provider.LastSpeechDetector;
        Assert.NotNull(detector);
        Assert.Equal(2, detector.Opened);
        Assert.Equal(2, detector.Closed);
        Assert.True(detector.Disposed);
        Assert.All(viewModel.Jobs, job => Assert.Equal(JobState.Completed, job.State));
    }

    [Fact]
    public void InstallingTheDiarisationModelIsWhatTurnsTheOptInOn()
    {
        // The provider asks about the file on disk rather than about the build, so the checkbox and
        // the rttm format come alive when the download finishes rather than at the next release.
        // A file of the right name is enough here: nothing loads it, and what is under test is the
        // wiring between the model store and the window.
        var directory = Directory.CreateTempSubdirectory("uindosill-diar").FullName;
        var store = new LocalModelStore(directory);
        var model = Assert.Single(ModelCatalog.Default.DiarisationModels);

        var before = new TranscribeViewModel(new EngineProvider(store, () => true), () => new EngineSelection());
        Assert.False(before.CanLabelSpeakers);
        Assert.DoesNotContain(before.Formats, f => f.Id == "rttm");

        File.WriteAllText(store.PathFor(model), "not really a graph");

        var after = new TranscribeViewModel(new EngineProvider(store, () => true), () => new EngineSelection());
        Assert.True(after.CanLabelSpeakers);
        Assert.Null(after.SpeakerHint);
        Assert.Contains(after.Formats, f => f.Id == "rttm");
    }

    [Fact]
    public void TheReasonTheInterpreterWasNotFoundReachesTheHintRatherThanAGuessAboutReinstalling()
    {
        // The resolver knows what it looked for — two bundle directories, or an override that
        // points at nothing — and until 2026-08-22 the window threw that away and said "reinstall",
        // which is the wrong advice when UINDOSILL_PYTHON names a path that does not exist.
        var directory = Directory.CreateTempSubdirectory("uindosill-diar").FullName;
        var store = new LocalModelStore(directory);
        var model = Assert.Single(ModelCatalog.Default.DiarisationModels);
        File.WriteAllText(store.PathFor(model), "not really a graph");

        var provider = new EngineProvider(
            store,
            () => (false, "UINDOSILL_PYTHON points at C:\\nowhere\\python.exe, which is neither a file nor a directory holding one."));
        var viewModel = new TranscribeViewModel(provider, () => new EngineSelection());

        Assert.False(viewModel.CanLabelSpeakers);
        Assert.Contains("points at", viewModel.SpeakerHint, StringComparison.Ordinal);
        Assert.DoesNotContain("reinstalling", viewModel.SpeakerHint, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptInComesAliveWithoutReopeningTheWindow()
    {
        // The view model is constructed once for the life of the window, so a snapshot taken in its
        // constructor is a hint that reads "install it from the Models tab" for the rest of the
        // session after the user has done exactly that. Asserted on one instance across the change,
        // not on two instances either side of it.
        var directory = Directory.CreateTempSubdirectory("uindosill-diar").FullName;
        var store = new LocalModelStore(directory);
        var model = Assert.Single(ModelCatalog.Default.DiarisationModels);
        var viewModel = new TranscribeViewModel(new EngineProvider(store, () => true), () => new EngineSelection());

        var notified = new List<string?>();
        viewModel.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        Assert.False(viewModel.CanLabelSpeakers);
        File.WriteAllText(store.PathFor(model), "not really a graph");
        viewModel.RefreshSpeakerAvailability();

        Assert.True(viewModel.CanLabelSpeakers);
        Assert.Null(viewModel.SpeakerHint);
        Assert.Contains(viewModel.Formats, f => f.Id == "rttm");
        Assert.Contains(nameof(TranscribeViewModel.CanLabelSpeakers), notified);
        Assert.Contains(nameof(TranscribeViewModel.SpeakerHint), notified);
    }

    [Fact]
    public void RemovingTheDiariserTurnsTheOptInOffRatherThanLeavingItTicked()
    {
        // The Models tab will remove the diariser — only the *loaded* transcription engine is
        // protected there. A checkbox left ticked with nothing behind it produces a transcript with
        // no names and a zero-byte .rttm, reported as "Finished": exactly the silent failure the
        // command line refuses.
        var directory = Directory.CreateTempSubdirectory("uindosill-diar").FullName;
        var store = new LocalModelStore(directory);
        var model = Assert.Single(ModelCatalog.Default.DiarisationModels);
        File.WriteAllText(store.PathFor(model), "not really a graph");

        var viewModel = new TranscribeViewModel(new EngineProvider(store, () => true), () => new EngineSelection());
        viewModel.LabelSpeakers = true;
        Assert.Contains(viewModel.Formats, f => f.Id == "rttm");

        File.Delete(store.PathFor(model));
        viewModel.RefreshSpeakerAvailability();

        Assert.False(viewModel.CanLabelSpeakers);
        Assert.False(viewModel.LabelSpeakers);
        Assert.DoesNotContain(viewModel.Formats, f => f.Id == "rttm");
        Assert.NotNull(viewModel.SpeakerHint);
    }

    [AvaloniaFact]
    public void TheModelsTabTellsTheTranscribeTabWhenTheDiariserArrives()
    {
        // The two tabs are siblings that do not know about each other, so the wiring lives in the
        // window's view model. Without it the checkbox never notices a download, which is what the
        // hint tells the user to go and do.
        var directory = Directory.CreateTempSubdirectory("uindosill-app").FullName;
        var store = new LocalModelStore(directory);
        var main = new MainWindowViewModel(new FakeEngineProvider(), store, ModelCatalog.Default, player: new FakeMediaPlayer());
        // By task, not by "not transcription": there are two non-ASR entries now, and the one this
        // wires up is the diariser.
        var diariser = Assert.Single(
            main.Models.Models, m => m.Descriptor.Task == ModelTask.Diarisation);

        File.WriteAllText(store.PathFor(diariser.Descriptor), "not really a graph");

        var notified = false;
        main.Transcribe.PropertyChanged += (_, e) =>
            notified |= e.PropertyName == nameof(TranscribeViewModel.CanLabelSpeakers);

        // What ModelsViewModel sets when a download finishes, and what Refresh() sets on a rescan.
        diariser.IsInstalled = true;

        Assert.True(notified, "the Transcribe tab was never told the diariser arrived");
    }

    [Fact]
    public async Task SelectingNoFormatStopsExportRatherThanTheRun()
    {
        // Formats are the Export button's question now, so a bare queue runs fine — the
        // transcript on screen needs no format — and it is Export that goes dark, with the
        // reason beside it.
        var (viewModel, directory) = Create();
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);

        foreach (var format in viewModel.Formats)
        {
            format.IsSelected = false;
        }

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.NotEmpty(viewModel.Jobs[0].Transcript);

        viewModel.SelectedJob = viewModel.Jobs[0];
        Assert.False(viewModel.CanExportFiles);
        Assert.Contains("at least one format", viewModel.ExportNotice, StringComparison.Ordinal);

        // The command guards itself too — a live keyboard shortcut is a button of its own.
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(directory, "a.txt")));
    }

    [Fact]
    public async Task SilentFileProducesAWarningTheUserCanActOn()
    {
        var (viewModel, directory) = Create();
        var path = Path.Combine(directory, "silent.wav");
        WavWriter.WriteFile(path, new float[16_000 * 3], 16_000);

        viewModel.AddFiles([path]);
        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.Contains("digitally silent", viewModel.Jobs[0].Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void LateProgressDoesNotResurrectAFinishedJob()
    {
        var job = new JobViewModel("/tmp/a.wav");
        job.Complete(new JobResult
        {
            Job = new Parakeet.Core.Jobs.TranscriptionJob { InputPath = "/tmp/a.wav" },
            State = JobState.Completed,
        });

        job.Apply(new Parakeet.Core.Transcription.TranscriptionProgress
        {
            Stage = Parakeet.Core.Transcription.TranscriptionStage.Decoding,
            Processed = TimeSpan.FromSeconds(3),
        });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("Done", job.Status);
    }

    [Fact]
    public void TheOnlyFormatTickedByDefaultIsSubtitles()
    {
        // SRT alone since 2026-08-23 — it was txt and srt. One default tick makes the Export
        // page's first press write one predictable file; anything more is asked for by ticking.
        var (viewModel, _) = Create();
        var selected = viewModel.Formats.Where(f => f.IsSelected).Select(f => f.Id).ToList();

        Assert.Equal(["srt"], selected);
    }
}

public class ModelsViewModelTests
{
    /// <summary>
    /// A catalogue with one unpinned entry. The shipped catalogue no longer has one — every entry
    /// is pinned against the repository's LFS digests — but the behaviour when an entry lacks a
    /// digest still has to hold, because that is the state of any entry somebody adds later.
    /// Sourcing these tests from the shipped data made them assert a fact about the data rather
    /// than the behaviour, and pinning the catalogue would have broken them for the wrong reason.
    /// </summary>
    private static ModelCatalog UnpinnedCatalogue() => ModelCatalog.Parse("""
        {
          "schema": 1,
          "models": [
            {
              "id": "test-unpinned",
              "family": "parakeet-tdt-0.6b-v3",
              "displayName": "Unpinned test entry",
              "quantisation": "q8_0",
              "fileName": "test-unpinned.gguf",
              "url": "https://example.invalid/test-unpinned.gguf",
              "sizeBytes": null,
              "sha256": null,
              "verified": false,
              "license": "CC-BY-4.0",
              "attributionId": "nvidia-parakeet-tdt-0.6b-v3",
              "languages": ["en"]
            }
          ]
        }
        """);

    [Fact]
    public void UnverifiedEntriesAreLabelledAsSuch()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var viewModel = new ModelsViewModel(new LocalModelStore(directory), UnpinnedCatalogue());

        var model = viewModel.Models.First(m => !m.Descriptor.Verified);

        // Asserted on the meaning rather than on one word. This looked for the literal "Unverified"
        // until the provenance lines were rewritten for the people who read them — "Unverified:
        // file name, size and digest were never checked against the repository" is a sentence for
        // whoever maintains the catalogue, not for somebody deciding whether to press Download.
        // A test that pins the vocabulary makes the text hard to improve without making it any
        // harder to ship a line that says nothing.
        Assert.False(model.ProvenanceIsVerified);
        Assert.True(model.NeedsUnverifiedOptIn);

        // It still has to *say* so, in whatever words: silence here would let an unchecked entry
        // look exactly like a checked one.
        Assert.Contains("cannot check", model.Provenance, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(
            new ModelsViewModel(new LocalModelStore(directory), ModelCatalog.Default)
                .Models.First(m => m.ProvenanceIsVerified).Provenance,
            model.Provenance);
    }

    [Fact]
    public async Task DownloadIsRefusedWithoutTheUnverifiedOptIn()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var viewModel = new ModelsViewModel(new LocalModelStore(directory), UnpinnedCatalogue())
        {
            AllowUnverified = false,
        };

        viewModel.Selected = viewModel.Models.First(m => m.NeedsUnverifiedOptIn);
        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.StatusMessage);
        Assert.Contains("cannot", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        // A refusal that does not say how to proceed is a dead end, and this one has a way out.
        Assert.Contains("tick", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.Selected!.IsInstalled);
    }

    [Fact]
    public void OnlyAFullyCheckedEntryCountsAsVerifiedProvenance()
    {
        // The view painted all four provenance lines in the warning colour, so "digest pinned" —
        // the one reassuring case — was drawn as a problem. The colour now follows this flag, and
        // it must be true for the checked case only: the other three each name something that was
        // never verified, and an unpinned entry saying "cannot be verified" in green would be the
        // same defect pointing the other way.
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var unpinned = new ModelsViewModel(new LocalModelStore(directory), UnpinnedCatalogue());

        var unchecked_ = unpinned.Models.First(m => !m.Descriptor.Verified);
        Assert.False(unchecked_.ProvenanceIsVerified);

        // On the shipped catalogue the property still has to track the descriptor exactly, and as of
        // 2026-08-20 every shipped entry is verified: the translation entry was the last one that
        // was not, and its nine files were published that day with every LFS oid matching the digest
        // the gate run recorded. The negative case is the constructed catalogue above, which is
        // where it belongs — a flag exercised only by whatever the shipped manifest happens to
        // contain stops being exercised the moment the manifest changes.
        var shipped = new ModelsViewModel(new LocalModelStore(directory), ModelCatalog.Default);
        Assert.All(shipped.Models, model =>
        {
            Assert.Equal(model.Descriptor.Verified, model.ProvenanceIsVerified);

            // The text has to distinguish the two states without this test dictating which words
            // do it: a checked entry must not be handed the sentence an unchecked one gets.
            Assert.NotEqual(unchecked_.Provenance, model.Provenance);
        });

        Assert.All(shipped.Models, model => Assert.True(model.ProvenanceIsVerified));
    }

    [Fact]
    public void ShippedEntriesAreAllPinnedAndSaySo()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var viewModel = new ModelsViewModel(new LocalModelStore(directory), ModelCatalog.Default);

        // "and say so" is the half worth testing, so it is tested as a property of the pair of
        // states rather than as a search for the phrase "digest pinned". These lines are read by
        // people deciding whether to download a gigabyte, so they get rewritten; a test that
        // spells the wording makes rewriting them look like a regression.
        Assert.All(viewModel.Models, model =>
        {
            Assert.False(model.NeedsUnverifiedOptIn);
            Assert.True(model.ProvenanceIsVerified);
            Assert.NotEmpty(model.Provenance);
        });
    }

    [Fact]
    public void DownloadIsOfferedOnlyForModelsThatAreNotAlreadyHere()
    {
        // The button bound to IsBusy alone, so an installed model still offered Download — a
        // 1.34 GiB re-fetch of a file the store already had, next to a Remove button that was
        // correctly disabled on the opposite condition. Asserting on CanDownload, which is what
        // the binding and the command guard both read.
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var viewModel = new ModelsViewModel(new LocalModelStore(directory), ModelCatalog.Default);
        var model = viewModel.Models.First();

        Assert.False(model.IsInstalled);
        Assert.True(model.CanDownload);

        model.IsInstalled = true;
        Assert.False(model.CanDownload);

        // Removing it puts the offer back, which is the only route from installed to downloadable.
        model.IsInstalled = false;
        Assert.True(model.CanDownload);

        // And a download in flight still suppresses it, as it did before.
        model.IsBusy = true;
        Assert.False(model.CanDownload);
    }

    [Fact]
    public async Task DownloadingAnInstalledModelDoesNothing()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var viewModel = new ModelsViewModel(new LocalModelStore(directory), ModelCatalog.Default);

        viewModel.Selected = viewModel.Models.First();
        viewModel.Selected.IsInstalled = true;

        // No installer factory is supplied, so a real InstallAsync would try to reach the network.
        // Returning early is what keeps this test offline, which is the point being asserted.
        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Null(viewModel.StatusMessage);
        Assert.False(viewModel.Selected.IsBusy);
    }

    [Fact]
    public async Task TheSessionNamesWhatIsLoadedAndTheBackendItActuallyGot()
    {
        // The window could not say which model was loaded because nothing stayed loaded: the engine
        // was created and disposed inside one Start. This asserts the session answers it, and that
        // it reports the backend the engine came back with rather than the one that was asked for.
        var session = new ModelSession(new FakeEngineProvider());
        var descriptor = ModelCatalog.Default.Models[0];

        Assert.False(session.IsLoaded);
        Assert.Null(session.LoadedBackend);

        await session.LoadAsync(new EngineSelection { Backend = ComputeBackend.Cuda, Model = descriptor });

        Assert.True(session.IsLoaded);
        Assert.NotNull(session.Engine);
        Assert.Equal(descriptor.Id, session.Model?.Id);
        Assert.Equal(ComputeBackend.Cuda, session.RequestedBackend);

        // The fake engine reports CPU whatever it is handed, which is exactly the fallback shape
        // the summary has to be able to show: asked for one backend, given another.
        Assert.Equal(ComputeBackend.Cpu, session.LoadedBackend);

        await session.UnloadAsync();

        Assert.False(session.IsLoaded);
        Assert.Null(session.Engine);
        Assert.Null(session.Model);
        Assert.Null(session.LoadedBackend);
    }

    [Fact]
    public async Task TheModelsTabReportsTheLoadedModelAndFlagsAFallback()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var session = new ModelSession(new FakeEngineProvider());
        var viewModel = new ModelsViewModel(
            new LocalModelStore(directory),
            ModelCatalog.Default,
            session: session,
            backend: () => ComputeBackend.Cuda);

        Assert.Contains("Nothing loaded", viewModel.LoadedSummary, StringComparison.Ordinal);
        Assert.False(viewModel.CanUnload);

        // Loading is offered only for a model that is actually on disk.
        viewModel.Selected = viewModel.Models[0];
        Assert.False(viewModel.CanLoad);

        viewModel.Selected.IsInstalled = true;
        Assert.True(viewModel.CanLoad);

        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsLoaded);
        Assert.True(viewModel.CanUnload);
        Assert.True(viewModel.Models[0].IsLoaded);
        Assert.Contains("Loaded:", viewModel.LoadedSummary, StringComparison.Ordinal);
        Assert.Contains("cpu", viewModel.LoadedSummary, StringComparison.Ordinal);

        // Asked for cuda, given cpu — the summary has to say so rather than repeat the request.
        Assert.Contains("fell back", viewModel.LoadedSummary, StringComparison.Ordinal);

        // The backend cannot change after a load, and the note says that rather than the UI
        // offering a control that quietly does nothing.
        Assert.Contains("fixed for this process", viewModel.BackendNote, StringComparison.Ordinal);

        await viewModel.UnloadCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsLoaded);
        Assert.False(viewModel.Models[0].IsLoaded);
        Assert.Contains("Nothing loaded", viewModel.LoadedSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAndUnloadAreShutOffWhileATranscriptionIsRunning()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var session = new ModelSession(new FakeEngineProvider());
        var viewModel = new ModelsViewModel(
            new LocalModelStore(directory), ModelCatalog.Default, session: session);

        viewModel.Selected = viewModel.Models[0];
        viewModel.Selected.IsInstalled = true;
        await viewModel.LoadCommand.ExecuteAsync(null);

        Assert.True(viewModel.CanUnload);

        // The running batch is holding the engine an unload would dispose.
        viewModel.IsTranscribing = true;

        Assert.False(viewModel.CanUnload);
        Assert.False(viewModel.CanLoad);

        viewModel.IsTranscribing = false;
        Assert.True(viewModel.CanUnload);
    }

    [Fact]
    public async Task ALoadedModelCannotBeRemovedFromUnderTheEngine()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var session = new ModelSession(new FakeEngineProvider());
        var viewModel = new ModelsViewModel(
            new LocalModelStore(directory), ModelCatalog.Default, session: session);

        viewModel.Selected = viewModel.Models[0];
        viewModel.Selected.IsInstalled = true;
        await viewModel.LoadCommand.ExecuteAsync(null);

        viewModel.RemoveCommand.Execute(null);

        Assert.Contains("Unload it first", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.True(viewModel.Models[0].IsInstalled);
    }

    [Fact]
    public void AttributionIsAvailableForDisplayNextToTheModels()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var viewModel = new ModelsViewModel(new LocalModelStore(directory), ModelCatalog.Default);

        Assert.Contains("NVIDIA", viewModel.Attribution, StringComparison.Ordinal);
    }
}
