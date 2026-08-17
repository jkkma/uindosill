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

        var tabs = Assert.IsType<TabControl>(window.Content);
        Assert.Equal(3, tabs.Items.Count);
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
        Assert.Contains(checkboxes, c => c is not null && c.Contains("unverified", StringComparison.OrdinalIgnoreCase));
    }

    [AvaloniaFact]
    public void LicenceTabCarriesTheFullNoticeInsideTheApplication()
    {
        // The notice has to be present where the material is used, not only in a file in the
        // source repository, so it is asserted on the view model the window renders.
        var viewModel = NewViewModel(out _);
        var licence = viewModel.LicenceText;

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
        var viewModel = new MainWindowViewModel(provider, new LocalModelStore(directory), ModelCatalog.Default);
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

    private static MainWindowViewModel NewViewModel(out string directory)
    {
        directory = Directory.CreateTempSubdirectory("uindosill-app").FullName;
        return new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default);
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
        var main = new MainWindowViewModel(provider, new LocalModelStore(directory), ModelCatalog.Default);
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
        var main = new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default);
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
    public async Task StartRefusesUntilAModelIsLoaded()
    {
        // Start used to load whatever was selected on its way past, which meant the first run of a
        // session silently decided the backend — the choice this tab now makes explicit.
        var directory = Directory.CreateTempSubdirectory("uindosill-vm").FullName;
        var main = new MainWindowViewModel(
            new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default);
        main.Transcribe.OutputDirectory = directory;
        main.Transcribe.AddFiles([WriteWav(directory, "a.wav")]);

        Assert.False(main.Transcribe.IsModelLoaded);
        Assert.False(main.Transcribe.CanStart);
        Assert.Contains("No model is loaded", main.Transcribe.StartHint, StringComparison.Ordinal);

        await main.Transcribe.StartCommand.ExecuteAsync(null);

        Assert.Contains("No model is loaded", main.Transcribe.StatusMessage, StringComparison.Ordinal);
        Assert.All(main.Transcribe.Jobs, job => Assert.Equal(JobState.Pending, job.State));
        Assert.False(File.Exists(Path.Combine(directory, "a.txt")));

        // Loading turns the button on, and the hint off.
        await main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor });

        Assert.True(main.Transcribe.IsModelLoaded);
        Assert.True(main.Transcribe.CanStart);
        Assert.Null(main.Transcribe.StartHint);

        await main.Transcribe.StartCommand.ExecuteAsync(null);
        Assert.Equal(JobState.Completed, main.Transcribe.Jobs[0].State);

        // Unloading takes it away again, without the queue being touched.
        await main.Session.UnloadAsync();

        Assert.False(main.Transcribe.CanStart);
        Assert.Single(main.Transcribe.Jobs);
    }

    [Fact]
    public async Task RunningTheQueueProducesTranscriptsAndFiles()
    {
        var (viewModel, directory) = Create();
        viewModel.AddFiles([WriteWav(directory, "a.wav"), WriteWav(directory, "b.wav")]);

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.All(viewModel.Jobs, job => Assert.Equal(JobState.Completed, job.State));
        Assert.All(viewModel.Jobs, job => Assert.NotEmpty(job.Transcript));
        Assert.True(File.Exists(Path.Combine(directory, "a.txt")));
        Assert.True(File.Exists(Path.Combine(directory, "a.srt")));
        Assert.False(viewModel.IsRunning);
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
        Assert.True(File.Exists(Path.Combine(directory, "good.txt")));
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

        // RTTM without the opt-in is refused rather than written empty, and nothing runs.
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Contains("need 'Label speakers' on", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(JobState.Pending, viewModel.Jobs[0].State);

        // Off, without asking for RTTM: nothing about speakers anywhere.
        viewModel.Formats.First(f => f.Id == "rttm").IsSelected = false;
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.DoesNotContain("Speaker", viewModel.Jobs[0].Transcript, StringComparison.Ordinal);

        // On: the window's transcript and the files carry the names.
        viewModel.Formats.First(f => f.Id == "rttm").IsSelected = true;
        viewModel.LabelSpeakers = true;
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.Contains("Speaker 1: ", viewModel.Jobs[0].Transcript, StringComparison.Ordinal);
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
            new EngineProvider(new LocalModelStore(Directory.CreateTempSubdirectory("uindosill-vm").FullName)),
            () => new EngineSelection());
        Assert.DoesNotContain(without.Formats, f => f.Id == "rttm");
    }

    [Fact]
    public async Task ALoadedModelStartsEvenWhenTheSelectedRowIsNotOne()
    {
        // The loaded engine is what runs, not whichever row is highlighted; a selection the engine
        // provider will not build must not be reported as "no model is installed".
        var directory = Directory.CreateTempSubdirectory("uindosill-vm").FullName;
        var main = new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default);
        main.Transcribe.OutputDirectory = directory;
        await main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor });
        main.Models.Selected = null;   // nothing highlighted: the session still holds the engine

        main.Transcribe.AddFiles([WriteWav(directory, "a.wav")]);
        await main.Transcribe.StartCommand.ExecuteAsync(null);

        Assert.Equal(JobState.Completed, main.Transcribe.Jobs[0].State);
        Assert.DoesNotContain("No model is installed", main.Transcribe.StatusMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutALabellerTheOptInIsDisabledWithAReason()
    {
        var viewModel = new TranscribeViewModel(new EngineProvider(new LocalModelStore(Directory.CreateTempSubdirectory("uindosill-vm").FullName)), () => new EngineSelection());

        Assert.False(viewModel.CanLabelSpeakers);
        Assert.Contains("not in this build yet", viewModel.SpeakerHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingNoFormatIsRefusedBeforeAnythingRuns()
    {
        var (viewModel, directory) = Create();
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);

        foreach (var format in viewModel.Formats)
        {
            format.IsSelected = false;
        }

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Contains("at least one output format", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.All(viewModel.Jobs, job => Assert.Equal(JobState.Pending, job.State));
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
    public void DefaultFormatsAreTextAndSubtitles()
    {
        var (viewModel, _) = Create();
        var selected = viewModel.Formats.Where(f => f.IsSelected).Select(f => f.Id).ToList();

        Assert.Contains("txt", selected);
        Assert.Contains("srt", selected);
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
        Assert.Contains("Unverified", model.Provenance, StringComparison.Ordinal);
        Assert.True(model.NeedsUnverifiedOptIn);
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

        Assert.Contains("cannot be verified", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.Selected!.IsInstalled);
    }

    [Fact]
    public void ShippedEntriesAreAllPinnedAndSaySo()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var viewModel = new ModelsViewModel(new LocalModelStore(directory), ModelCatalog.Default);

        Assert.All(viewModel.Models, model =>
        {
            Assert.False(model.NeedsUnverifiedOptIn);
            Assert.Contains("digest pinned", model.Provenance, StringComparison.OrdinalIgnoreCase);
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
