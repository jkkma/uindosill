using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Audio;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;

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

    private static MainWindowViewModel NewViewModel(out string directory)
    {
        directory = Directory.CreateTempSubdirectory("uindosill-app").FullName;
        return new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default);
    }
}

public class TranscribeViewModelTests
{
    private static (TranscribeViewModel ViewModel, string Directory) Create()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-vm").FullName;
        var main = new MainWindowViewModel(new FakeEngineProvider(), new LocalModelStore(directory), ModelCatalog.Default);
        main.Transcribe.OutputDirectory = directory;
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
    public void AttributionIsAvailableForDisplayNextToTheModels()
    {
        var directory = Directory.CreateTempSubdirectory("uindosill-models").FullName;
        var viewModel = new ModelsViewModel(new LocalModelStore(directory), ModelCatalog.Default);

        Assert.Contains("NVIDIA", viewModel.Attribution, StringComparison.Ordinal);
    }
}
