using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.Audio;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Tidying;

namespace Parakeet.App.Tests;

/// <summary>
/// The window's half of "Tidy up the transcript": the opt-in, the stage beside the recogniser,
/// and the pane that shows the result beside the transcript rather than instead of it.
/// </summary>
public class TidyTests
{
    private static (MainWindowViewModel Main, TranscribeViewModel ViewModel, string Directory) Create(FakeTidierOptions? tidier = null)
    {
        var directory = TestTemp.NewDirectory("uindosill-tidy");
        var main = new MainWindowViewModel(
            new FakeEngineProvider(tidier: tidier), new LocalModelStore(directory), ModelCatalog.Default, player: new FakeMediaPlayer());
        main.Transcribe.OutputDirectory = directory;

        main.Session.LoadAsync(new EngineSelection { Model = main.Models.SelectedDescriptor })
            .GetAwaiter().GetResult();

        return (main, main.Transcribe, directory);
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

    private static void Select(TranscribeViewModel viewModel, params string[] ids)
    {
        foreach (var format in viewModel.Formats)
        {
            format.IsSelected = ids.Contains(format.Id, StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task TheTidyOptInIsOffByDefaultAndProducesATidiedVersionBesideTheTranscript()
    {
        var (_, viewModel, directory) = Create();
        Assert.False(viewModel.TidyUpTranscript);
        Assert.True(viewModel.CanTidy);        // the fake provider has a tidier
        Assert.Null(viewModel.TidyHint);

        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        Select(viewModel, "txt");

        // Off: one transcript, nothing to switch to.
        await viewModel.StartCommand.ExecuteAsync(null);
        Assert.Equal(JobState.Completed, viewModel.Jobs[0].State);
        Assert.False(viewModel.Jobs[0].HasTidy);
        Assert.False(viewModel.CanShowTidy);
        Assert.False(viewModel.CanShowPanes);
        Assert.Null(viewModel.Jobs[0].TidyProvenance);

        viewModel.TidyUpTranscript = true;
        await viewModel.RunAgainCommand.ExecuteAsync(null);

        var job = viewModel.Jobs[0];
        Assert.Equal(JobState.Completed, job.State);
        Assert.Empty(job.Warning ?? string.Empty);

        // Beside, not instead of: the spoken transcript is what the engine wrote and the tidied
        // one is the fake's rewrite of it — visibly, since the canned tidier capitalises the line.
        Assert.True(job.HasTidy);
        Assert.StartsWith("the quick brown fox", job.Transcript, StringComparison.Ordinal);
        Assert.StartsWith("The quick brown fox", job.TidiedTranscript, StringComparison.Ordinal);
        Assert.Equal("Tidied: fake-tidier on cpu", job.TidyProvenance);
        Assert.NotNull(job.TidiedDocument);
        Assert.Equal("fake-tidier", job.TidiedDocument!.TidyModelId);
        Assert.Null(job.Document!.TidyModelId);

        // The tidied lines keep the spoken words' timings, which the English pane cannot have, and
        // no line on a finished row is still waiting for its tidy.
        Assert.NotEmpty(job.Lines);
        Assert.Equal(job.Lines.Count, job.TidiedLines.Count);
        Assert.Equal(job.Lines[0].HasWordTimings, job.TidiedLines[0].HasWordTimings);
        Assert.All(job.Lines, line => Assert.False(line.IsProvisional));
        Assert.All(job.TidiedLines, line => Assert.False(line.IsProvisional));

        // The reader who watched the tidied lines land is left reading them.
        Assert.True(viewModel.CanShowTidy);
        Assert.True(viewModel.CanShowPanes);
        Assert.False(viewModel.CanShowTranslation);
        Assert.Equal(2, viewModel.TranscriptPane);
        Assert.Same(job.TidiedLines, viewModel.VisibleLines);
    }

    [Fact]
    public async Task ATidiedExportWritesTheTidiedVersionBesideThePlainFilesNeverOverThem()
    {
        var (_, viewModel, directory) = Create();
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        Select(viewModel, "txt", "vtt-words", "json");

        await viewModel.StartCommand.ExecuteAsync(null);
        viewModel.SelectedJob = viewModel.Jobs[0];
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        var plain = Path.Combine(directory, "a.txt");
        Assert.True(File.Exists(plain));
        var beforeTidying = await File.ReadAllTextAsync(plain);

        viewModel.TidyUpTranscript = true;
        await viewModel.RunAgainCommand.ExecuteAsync(null);

        // Re-selected, so the notice describes what the next export would write rather than
        // reporting the last one; it now promises the tidied files beside the plain ones.
        viewModel.SelectedJob = null;
        viewModel.SelectedJob = viewModel.Jobs[0];
        Assert.Contains(".tidy files", viewModel.ExportNotice, StringComparison.Ordinal);
        await viewModel.ExportFilesCommand.ExecuteAsync(null);

        var tidy = Path.Combine(directory, "a.tidy.txt");
        Assert.True(File.Exists(tidy), "the tidied export wrote no .tidy file");
        Assert.Contains("The quick brown fox", await File.ReadAllTextAsync(tidy), StringComparison.Ordinal);
        Assert.Contains("the quick brown fox", await File.ReadAllTextAsync(plain), StringComparison.Ordinal);

        // The word-timed file is written for the tidied version too — its words carry the
        // spoken words' times — and the JSON names the model that tidied it.
        Assert.True(File.Exists(Path.Combine(directory, "a.tidy.words.vtt")));
        Assert.Contains("\"tidyModel\": \"fake-tidier\"", await File.ReadAllTextAsync(Path.Combine(directory, "a.tidy.json")), StringComparison.Ordinal);

        // Untouched, rather than overwritten with the tidied text under its old name.
        Assert.Equal(beforeTidying, await File.ReadAllTextAsync(plain));
    }

    [Fact]
    public async Task ATidierThatCannotLoadStopsTheBatchBeforeAnyFileIsDecoded()
    {
        var (_, viewModel, directory) = Create(new FakeTidierOptions { FailOnLoad = true });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        Select(viewModel, "txt");
        viewModel.TidyUpTranscript = true;

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Contains("configured to fail on load", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(JobState.Pending, viewModel.Jobs[0].State);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task ATidierThatFailsOnAFileLeavesTheTranscriptWholeWithNoTidiedPane()
    {
        var (_, viewModel, directory) = Create(new FakeTidierOptions { FailOnTidy = true });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        Select(viewModel, "txt");
        viewModel.TidyUpTranscript = true;

        await viewModel.StartCommand.ExecuteAsync(null);

        var row = viewModel.Jobs[0];
        Assert.Equal(JobState.Completed, row.State);
        Assert.Contains("without the tidied version", row.Status, StringComparison.Ordinal);
        Assert.Contains("Tidying failed for this file", row.Warning, StringComparison.Ordinal);
        Assert.Contains("configured to fail on every line", row.Warning, StringComparison.Ordinal);

        // The transcript is whole and the row has one pane: no tidied document, no switcher, no
        // line left marked as waiting for a tidy that is not coming.
        Assert.False(row.HasTidy);
        Assert.Null(row.TidiedDocument);
        Assert.NotEmpty(row.Lines);
        Assert.All(row.Lines, line => Assert.False(line.IsProvisional));
        Assert.False(viewModel.CanShowPanes);
        Assert.Equal(0, viewModel.TranscriptPane);

        viewModel.SelectedJob = row;
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(directory, "a.txt")));
        Assert.False(File.Exists(Path.Combine(directory, "a.tidy.txt")));
        Assert.Contains("transcribed without the tidied version", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARewriteThatBreaksTheContractLeavesTheLineAsSpokenAndTheRowSaysSo()
    {
        // The fake is made to add a word to every line, which the contract refuses: every line is
        // kept as spoken, the tidied pane exists and reads as the spoken one, and the row counts
        // the refusals.
        var (_, viewModel, directory) = Create(new FakeTidierOptions { Insert = "Well," });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        Select(viewModel, "txt");
        viewModel.TidyUpTranscript = true;

        await viewModel.StartCommand.ExecuteAsync(null);

        var row = viewModel.Jobs[0];
        Assert.Equal(JobState.Completed, row.State);
        Assert.Empty(row.FailedPassesOrEmpty());
        Assert.True(row.HasTidy);
        Assert.Equal(row.Transcript, row.TidiedTranscript);
        Assert.DoesNotContain("Well,", row.TidiedTranscript, StringComparison.Ordinal);
        Assert.Contains("kept as spoken because the rewrite changed or added words", row.Warning, StringComparison.Ordinal);
        Assert.Equal(row.TidiedDocument!.Segments.Count(s => !s.IsEmpty), row.TidiedDocument.TidyRefusedSegments);
    }

    [Fact]
    public async Task TheSwitcherShowsTheTidiedPillOnlyForARowThatHasOneAndNeverStrandsAnybody()
    {
        var (_, viewModel, directory) = Create();
        Select(viewModel, "txt");

        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        viewModel.TidyUpTranscript = true;
        await viewModel.StartCommand.ExecuteAsync(null);

        viewModel.AddFiles([WriteWav(directory, "b.wav")]);
        viewModel.TidyUpTranscript = false;
        await viewModel.StartCommand.ExecuteAsync(null);

        var tidied = viewModel.Jobs.First(j => j.FileName == "a.wav");
        var plain = viewModel.Jobs.First(j => j.FileName == "b.wav");

        viewModel.SelectedJob = tidied;
        Assert.True(viewModel.CanShowPanes);
        Assert.True(viewModel.CanShowTidy);
        Assert.False(viewModel.CanShowTranslation);

        viewModel.TranscriptPane = 2;
        Assert.Same(tidied.TidiedLines, viewModel.VisibleLines);

        // The pane a person chose is theirs to keep; a row that cannot honour it falls back to
        // the spoken lines rather than an empty pane.
        viewModel.SelectedJob = plain;
        Assert.False(viewModel.CanShowPanes);
        Assert.Equal(2, viewModel.TranscriptPane);
        Assert.Same(plain.Lines, viewModel.VisibleLines);

        viewModel.SelectedJob = tidied;
        Assert.Same(tidied.TidiedLines, viewModel.VisibleLines);
    }

    [AvaloniaFact]
    public async Task TheWindowDrawsTheOptInAndTheTidiedPill()
    {
        var (main, viewModel, directory) = Create();
        var window = new MainWindow { DataContext = main };
        window.Show();

        var optIn = window.FindControl<CheckBox>("TidyUpTranscript");
        Assert.NotNull(optIn);
        Assert.True(optIn!.IsEnabled);

        var switcher = window.FindControl<Border>("TranscriptPaneSwitcher")!;
        var tidiedPill = window.FindControl<RadioButton>("PaneTidied")!;
        Assert.False(switcher.IsVisible);

        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        Select(viewModel, "txt");
        viewModel.TidyUpTranscript = true;
        await viewModel.StartCommand.ExecuteAsync(null);
        viewModel.SelectedJob = viewModel.Jobs[0];

        Assert.True(switcher.IsVisible);
        Assert.True(tidiedPill.IsVisible);
        Assert.False(window.FindControl<RadioButton>("PaneEnglish")!.IsVisible);

        // And the Ask tab, which asks over the tidied pane, is put on it once.
        main.Ask.SelectedRecording = viewModel.Jobs[0];
        Assert.True(main.Ask.CanShowTidy);
        Assert.True(main.Ask.CanShowPanes);
        Assert.NotNull(window.FindControl<RadioButton>("AskPaneTidied"));

        window.Close();
    }
}

internal static class JobViewModelTestExtensions
{
    /// <summary>The passes the row says it is missing, read off its status: none when it does not say "without".</summary>
    public static IEnumerable<string> FailedPassesOrEmpty(this JobViewModel row) =>
        row.Status.Contains("without", StringComparison.Ordinal) ? [row.Status] : [];
}
