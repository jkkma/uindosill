using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Parakeet.App.Services.Tools;
using Parakeet.App.ViewModels;
using Parakeet.App.Views;
using Parakeet.App.Services;
using Parakeet.Core.Jobs;
using Parakeet.Core.Muxing;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

/// <summary>A muxer that writes a stub file and remembers what it was asked to do.</summary>
internal sealed class FakeSubtitleMuxer : ISubtitleMuxer
{
    public bool IsAvailable { get; set; } = true;

    public string? Unavailable { get; set; }

    public SubtitleMuxPlan? Plan { get; private set; }

    /// <summary>What the window handed it — which is where a renamed speaker has to show up.</summary>
    public string? Subtitles { get; private set; }

    public string? Failure { get; set; }

    public string? DescribeUnavailable() => IsAvailable ? null : Unavailable ?? "no muxer";

    public Task<string> MuxAsync(
        SubtitleMuxPlan plan,
        string subtitlePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Plan = plan;
        Subtitles = File.ReadAllText(subtitlePath);

        if (Failure is { } failure)
        {
            throw new SubtitleMuxException(failure);
        }

        File.WriteAllText(plan.OutputPath, "muxed");
        return Task.FromResult(plan.OutputPath);
    }
}

/// <summary>
/// Putting the transcript back inside the recording, from the window that offers it.
/// </summary>
/// <remarks>
/// Everything here runs against <see cref="FakeSubtitleMuxer"/>. The real one needs the vendored
/// ffmpeg and real media; what it does was measured by hand over eight input-and-format routes on
/// 2026-08-23, and the rules it follows are tested exhaustively in <c>SubtitleMuxTests</c>. What
/// these cover is the half only the window can be wrong about: which format is chosen, what the
/// reader is told, and whether a renamed speaker reaches the file.
/// </remarks>
public class AddToRecordingTests
{
    [Fact]
    public void TheRichestSelectedFormatIsTheOneThatGoesIn()
    {
        var (vm, _, _) = Create();

        // Word-timed WebVTT carries everything a plain one does and the word times as well.
        Select(vm, "srt", "vtt", "vtt-words");
        Assert.Equal("vtt-words", vm.ExportableFormat);

        // Between a plain WebVTT and an SRT there is nothing to choose on content — both are plain
        // cues — so the tie goes to the one that keeps the file an MP4. A plain WebVTT would force
        // Matroska for no gain at all.
        Select(vm, "srt", "vtt");
        Assert.Equal("srt", vm.ExportableFormat);

        Select(vm, "srt");
        Assert.Equal("srt", vm.ExportableFormat);

        // The rest are documents, not tracks, so there is nothing to put in.
        Select(vm, "txt", "json", "md");
        Assert.Null(vm.ExportableFormat);
        Assert.False(vm.CanAddToRecording);
    }

    [Fact]
    public async Task ARenamedSpeakerReachesTheFileThatGoesIntoTheRecording()
    {
        // The reason the transcript is rendered at this moment rather than taken off disk: the
        // sidecars were written before the window could show anybody a name.
        var (vm, job, muxer) = Create();
        Select(vm, "srt");

        job.Speakers[0].Name = "Ada";
        await vm.AddToRecordingCommand.ExecuteAsync(null);

        Assert.NotNull(muxer.Subtitles);
        Assert.Contains("Ada: hello there", muxer.Subtitles, StringComparison.Ordinal);
        Assert.DoesNotContain("Speaker 1", muxer.Subtitles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheNewFileIsListedWithTheRunsOtherOutputs()
    {
        var (vm, job, muxer) = Create();
        Select(vm, "srt");

        var before = job.OutputFiles.Count;
        await vm.AddToRecordingCommand.ExecuteAsync(null);

        Assert.Equal(before + 1, job.OutputFiles.Count);
        Assert.Equal(muxer.Plan!.OutputPath, job.OutputFiles[^1]);
        Assert.Contains("Added the transcript to", vm.AddToRecordingNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoticeSaysWhatWillHappenBeforeAnythingHas()
    {
        var (vm, _, _) = Create();

        Select(vm, "srt");
        Assert.Contains(".subtitled.mp4", vm.AddToRecordingNotice, StringComparison.Ordinal);
        Assert.Contains("left alone", vm.AddToRecordingNotice, StringComparison.Ordinal);

        // And says what the container costs when it costs something.
        Select(vm, "vtt-words");
        Assert.Contains(".subtitled.mkv", vm.AddToRecordingNotice, StringComparison.Ordinal);
        Assert.Contains("Word-by-word timing", vm.AddToRecordingNotice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("txt", "Tick SRT or WebVTT")]
    public void ADisabledButtonAlwaysSaysWhichReasonItIs(string format, string expected)
    {
        // This window's standing rule: a control that cannot be pressed says why, or it is the
        // shape of every interface defect it has shipped.
        var (vm, _, _) = Create();
        Select(vm, format);

        Assert.False(vm.CanAddToRecording);
        Assert.Contains(expected, vm.AddToRecordingNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildWithNoMuxerSaysSoRatherThanOfferingTheButton()
    {
        var muxer = new FakeSubtitleMuxer { IsAvailable = false, Unavailable = "ffmpeg was not vendered." };
        var (vm, _, _) = Create(muxer);
        Select(vm, "srt");

        Assert.False(vm.CanAddToRecording);
        Assert.Equal("ffmpeg was not vendered.", vm.AddToRecordingNotice);
    }

    [Fact]
    public void AnUntranscribedRecordingIsToldToBeTranscribedFirst()
    {
        var muxer = new FakeSubtitleMuxer();
        var vm = NewViewModel(muxer);
        vm.Jobs.Add(new JobViewModel("/tmp/talk.mp4"));
        vm.SelectedJob = vm.Jobs[0];
        Select(vm, "srt");

        Assert.False(vm.CanAddToRecording);
        Assert.Contains("Transcribe this recording first", vm.AddToRecordingNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureIsReportedInTheWindowRatherThanThrown()
    {
        var muxer = new FakeSubtitleMuxer { Failure = "ffmpeg refused the audio codec." };
        var (vm, job, _) = Create(muxer);
        Select(vm, "srt");

        await vm.AddToRecordingCommand.ExecuteAsync(null);

        Assert.Equal("ffmpeg refused the audio codec.", vm.AddToRecordingNotice);
        Assert.Empty(job.OutputFiles);
    }

    [Fact]
    public async Task TheResultDoesNotFollowTheReaderToAnotherRecording()
    {
        // A line saying "Added the transcript to ..." standing under a different row reads as a
        // claim about that row.
        var (vm, _, _) = Create();
        Select(vm, "srt");
        await vm.AddToRecordingCommand.ExecuteAsync(null);
        Assert.Contains("Added the transcript to", vm.AddToRecordingNotice, StringComparison.Ordinal);

        vm.Jobs.Add(new JobViewModel("/tmp/other.mp4"));
        vm.SelectedJob = vm.Jobs[^1];

        Assert.DoesNotContain("Added the transcript to", vm.AddToRecordingNotice, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheButtonIsDrawnAndBoundRatherThanMerelyPresent()
    {
        // Bound rather than present: a Button whose Command is null renders, hovers and does
        // nothing, which is the exact shape this window keeps finding.
        var window = new MainWindow { DataContext = WindowTests.NewViewModel(out _) };
        window.Show();
        window.UpdateLayout();

        var button = window.FindControl<Button>("AddToRecording");
        Assert.NotNull(button);
        Assert.NotNull(button!.Command);

        Assert.NotNull(window.FindControl<TextBlock>("AddToRecordingNotice"));
    }

    private static void Select(TranscribeViewModel vm, params string[] ids)
    {
        foreach (var format in vm.Formats)
        {
            format.IsSelected = ids.Contains(format.Id, StringComparer.Ordinal);
        }
    }

    private static TranscribeViewModel NewViewModel(FakeSubtitleMuxer muxer) =>
        new(new FakeEngineProvider(), () => new EngineSelection(), muxer: muxer);

    /// <summary>A view model with one transcribed, labelled video selected.</summary>
    private static (TranscribeViewModel ViewModel, JobViewModel Job, FakeSubtitleMuxer Muxer) Create(
        FakeSubtitleMuxer? withMuxer = null)
    {
        var muxer = withMuxer ?? new FakeSubtitleMuxer();
        var vm = NewViewModel(muxer);

        var directory = Directory.CreateTempSubdirectory("uindosill-mux").FullName;
        var path = Path.Combine(directory, "talk.mp4");
        File.WriteAllText(path, "not really a video");

        var job = new JobViewModel(path);
        job.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = path },
            State = JobState.Completed,
            Document = new TranscriptDocument
            {
                Segments =
                [
                    new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Speaker = "Speaker 1", Text = "hello there" },
                    new TranscriptSegment { Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(4), Speaker = "Speaker 2", Text = "hello back" },
                ],
            },
        });

        vm.Jobs.Add(job);
        vm.SelectedJob = job;

        return (vm, job, muxer);
    }
}
