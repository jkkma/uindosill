using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Translation;

namespace Parakeet.App.Tests;

/// <summary>
/// The window when an opt-in engine fails: before the batch, where it is a sentence and nothing
/// is decoded; and on a file, where the transcript is kept and the row says what it is missing.
/// </summary>
/// <remarks>
/// Until 2026-08-22 the window loaded each engine lazily, inside the first file's pass, so an
/// engine that could not load was discovered after that file's full decode — and after every other
/// file's, since each tried again — and a pass that failed on a file failed the file, finished
/// decode and all. The command line had neither problem; these hold the window to its rule.
/// </remarks>
public class OptInFailureTests
{
    private static (TranscribeViewModel ViewModel, string Directory) Create(
        FakeSpeakerLabellerOptions? speakers = null,
        FakeTranslatorOptions? translator = null)
    {
        var directory = TestTemp.NewDirectory("uindosill-optin");
        var main = new MainWindowViewModel(
            new FakeEngineProvider(speakers: speakers, translator: translator),
            new LocalModelStore(directory),
            ModelCatalog.Default, player: new FakeMediaPlayer());
        main.Transcribe.OutputDirectory = directory;

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

    private static void Select(TranscribeViewModel viewModel, params string[] ids)
    {
        foreach (var format in viewModel.Formats)
        {
            format.IsSelected = ids.Contains(format.Id, StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task ALabellerThatCannotLoadStopsTheBatchBeforeAnyFileIsDecoded()
    {
        var (viewModel, directory) = Create(speakers: new FakeSpeakerLabellerOptions { FailOnLoad = true });
        viewModel.AddFiles([WriteWav(directory, "a.wav"), WriteWav(directory, "b.wav")]);
        Select(viewModel, "txt");
        viewModel.LabelSpeakers = true;
        viewModel.SpeakerCount = 2;

        await viewModel.StartCommand.ExecuteAsync(null);

        // The reason is in the status bar, and nothing ran: no row moved, no file was written. The
        // alternative was a Failed row after a's full decode, then another after b's.
        Assert.Contains("configured to fail on load", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.All(viewModel.Jobs, job => Assert.Equal(JobState.Pending, job.State));
        Assert.False(File.Exists(Path.Combine(directory, "a.txt")));
        Assert.False(File.Exists(Path.Combine(directory, "b.txt")));
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task ATranslatorThatCannotLoadStopsTheBatchTheSameWay()
    {
        var (viewModel, directory) = Create(translator: new FakeTranslatorOptions { FailOnLoad = true });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        Select(viewModel, "txt");
        viewModel.TranslateToEnglish = true;

        await viewModel.StartCommand.ExecuteAsync(null);

        Assert.Contains("configured to fail on load", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(JobState.Pending, viewModel.Jobs[0].State);
        Assert.False(File.Exists(Path.Combine(directory, "a.en.txt")));
        Assert.False(File.Exists(Path.Combine(directory, "a.txt")));
    }

    [Fact]
    public async Task ALabellerThatFailsOnAFileLeavesTheTranscriptAndTheRowSaysWhatIsMissing()
    {
        var (viewModel, directory) = Create(speakers: new FakeSpeakerLabellerOptions { FailOnLabel = true });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        Select(viewModel, "txt", "rttm");
        viewModel.LabelSpeakers = true;
        viewModel.SpeakerCount = 2;

        await viewModel.StartCommand.ExecuteAsync(null);

        var row = viewModel.Jobs[0];
        Assert.Equal(JobState.Completed, row.State);

        // The row reads as done and as missing something, in the same breath; the warning line
        // carries the reason. Exporting writes the transcript and skips the turns-only format,
        // because the finished document has no turns for it to carry — the export path answers
        // from the document what Start could only have predicted.
        Assert.Contains("Done", row.Status, StringComparison.Ordinal);
        Assert.Contains("without speaker labels", row.Status, StringComparison.Ordinal);
        Assert.Contains("Speaker labelling failed for this file", row.Warning, StringComparison.Ordinal);
        Assert.Contains("configured to fail on every file", row.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Speaker", row.Transcript, StringComparison.Ordinal);

        viewModel.SelectedJob = row;
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(directory, "a.txt")));
        Assert.False(File.Exists(Path.Combine(directory, "a.rttm")));
        Assert.Contains("no RTTM", viewModel.ExportNotice, StringComparison.Ordinal);

        // And the summary does not let "Finished" stand for "finished with speakers".
        Assert.Contains("written without speaker labels", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATranslatorThatFailsOnAFileLeavesTheSpokenTranscriptUnderThePlainNameWithNoSwitcher()
    {
        var (viewModel, directory) = Create(translator: new FakeTranslatorOptions { FailOnTranslate = true });
        viewModel.AddFiles([WriteWav(directory, "a.wav")]);
        Select(viewModel, "txt");
        viewModel.TranslateToEnglish = true;

        await viewModel.StartCommand.ExecuteAsync(null);

        var row = viewModel.Jobs[0];
        Assert.Equal(JobState.Completed, row.State);
        Assert.Contains("without the English version", row.Status, StringComparison.Ordinal);
        Assert.Contains("Translation failed for this file", row.Warning, StringComparison.Ordinal);

        // Under the plain name — the .en one promises English — and with one pane, not two of the
        // same text. The row kept no English document, so exporting writes the spoken transcript
        // and nothing named .en.
        viewModel.SelectedJob = row;
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(directory, "a.txt")));
        Assert.False(File.Exists(Path.Combine(directory, "a.en.txt")));
        Assert.False(row.HasTranslation);
        Assert.DoesNotContain("[en]", row.Transcript, StringComparison.Ordinal);
    }
}
