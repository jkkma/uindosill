using Parakeet.App.Services;
using Parakeet.App.Services.Tools;
using Parakeet.App.ViewModels;
using Parakeet.Core.Jobs;
using Parakeet.Core.Muxing;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Tests;

public class ExportCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResettingTheSelectedRowClearsItsPreviousWriteResult(bool remux)
    {
        var muxer = new BlockingMuxer();
        var writer = new BlockingWriter();
        muxer.Release.SetResult();
        writer.Release.SetResult();
        var vm = new TranscribeViewModel(new FakeEngineProvider(), () => new EngineSelection(),
            muxer: muxer, writeTranscript: writer.WriteAsync);
        var row = CompletedRecording("previous.mp4");
        vm.Jobs.Add(row);
        vm.SelectedJob = row;
        await (remux ? vm.AddToRecordingCommand.ExecuteAsync(null) : vm.ExportFilesCommand.ExecuteAsync(null));
        Assert.Contains(remux ? "Added the transcript" : "Wrote", remux ? vm.AddToRecordingNotice : vm.ExportNotice,
            StringComparison.Ordinal);

        row.Reset();

        Assert.Equal("Transcribe this recording first.", vm.ExportNotice);
        Assert.Equal("Transcribe this recording first.", vm.AddToRecordingNotice);
        Assert.False(vm.CanExportFiles);
        Assert.False(vm.CanAddToRecording);
    }

    [Theory]
    [InlineData(false, false, "selection")]
    [InlineData(false, true, "selection")]
    [InlineData(false, false, "clear")]
    [InlineData(false, true, "clear")]
    [InlineData(false, false, "reset")]
    [InlineData(false, true, "reset")]
    [InlineData(true, false, "selection")]
    [InlineData(true, true, "selection")]
    [InlineData(true, false, "clear")]
    [InlineData(true, true, "clear")]
    [InlineData(true, false, "reset")]
    [InlineData(true, true, "reset")]
    public async Task AnOldWriteCannotChangeAnotherSelectionOrRun(bool remux, bool fail, string transition)
    {
        var muxer = new BlockingMuxer { Fail = fail };
        var writer = new BlockingWriter { Fail = fail };
        var vm = new TranscribeViewModel(new FakeEngineProvider(), () => new EngineSelection(),
            muxer: muxer, writeTranscript: writer.WriteAsync);
        var row = CompletedRecording("original.mp4");
        vm.Jobs.Add(row);
        vm.SelectedJob = row;

        var operation = remux
            ? vm.AddToRecordingCommand.ExecuteAsync(null)
            : vm.ExportFilesCommand.ExecuteAsync(null);
        await (remux ? muxer.Started.Task : writer.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));

        switch (transition)
        {
            case "selection":
                var other = new JobViewModel(Path.Combine(Path.GetDirectoryName(row.Path)!, "other.mp4"));
                vm.Jobs.Add(other);
                vm.SelectedJob = other;
                break;
            case "clear":
                vm.ClearCommand.Execute(null);
                break;
            case "reset":
                row.Reset();
                Complete(row, "replacement");
                break;
        }

        var expectedExport = vm.ExportNotice;
        var expectedMux = vm.AddToRecordingNotice;
        muxer.Release.TrySetResult();
        writer.Release.TrySetResult();
        await operation;

        Assert.Equal(expectedExport, vm.ExportNotice);
        Assert.Equal(expectedMux, vm.AddToRecordingNotice);
        Assert.Equal(!fail && transition == "selection" ? 1 : 0, row.OutputFiles.Count);
    }

    [Fact]
    public async Task AnExportKeepsEveryVersionFromTheRunItStartedWith()
    {
        var writer = new BlockingWriter();
        var vm = new TranscribeViewModel(new FakeEngineProvider(), () => new EngineSelection(),
            writeTranscript: writer.WriteAsync);
        var row = CompletedRecording("versions.mp4", withVersions: true);
        vm.Jobs.Add(row);
        vm.SelectedJob = row;

        var operation = vm.ExportFilesCommand.ExecuteAsync(null);
        await writer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        row.Reset();
        Complete(row, "replacement", withVersions: true);
        writer.Release.SetResult();
        await operation;

        Assert.Equal(new[] { "original", "original English", "original tidy" }, writer.Documents.Select(document => document.Text));
        Assert.Empty(row.OutputFiles);
        Assert.DoesNotContain("Wrote", vm.ExportNotice, StringComparison.Ordinal);
    }

    private static JobViewModel CompletedRecording(string name, bool withVersions = false)
    {
        var directory = TestTemp.NewDirectory("uindosill-export-completion");
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "UI fixture");
        var row = new JobViewModel(path);
        Complete(row, "original", withVersions);
        return row;
    }

    private static void Complete(JobViewModel row, string text, bool withVersions = false)
    {
        static TranscriptDocument Document(string words) => new()
        {
            Segments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Text = words }],
        };

        var spoken = Document(text);
        row.Complete(new JobResult
        {
            Job = new TranscriptionJob { InputPath = row.Path },
            State = JobState.Completed,
            Document = withVersions ? Document(text + " English") : spoken,
            TidiedDocument = withVersions ? Document(text + " tidy") : null,
        }, withVersions ? spoken : null);
    }

    private sealed class BlockingWriter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<TranscriptDocument> Documents { get; } = [];
        public bool Fail { get; init; }

        public async Task<IReadOnlyList<string>> WriteAsync(TranscriptDocument document, TranscriptionJob job, CancellationToken ct)
        {
            Documents.Add(document);
            if (Documents.Count == 1)
            {
                Started.SetResult();
                await Release.Task.WaitAsync(ct);
                if (Fail)
                {
                    throw new IOException("The old export failed.");
                }
            }

            return [Path.ChangeExtension(job.InputPath, ".srt")];
        }
    }

    private sealed class BlockingMuxer : ISubtitleMuxer
    {
        public bool IsAvailable => true;
        public bool Fail { get; init; }
        public string? DescribeUnavailable() => null;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> MuxAsync(SubtitleMuxPlan plan, string subtitlePath, IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (Fail)
            {
                throw new SubtitleMuxException("The old remux failed.");
            }

            return plan.OutputPath;
        }
    }
}
