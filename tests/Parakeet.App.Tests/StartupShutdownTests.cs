using System.Runtime.CompilerServices;
using Parakeet.App.Services;
using Parakeet.App.Services.Tools;
using Parakeet.App.ViewModels;
using Parakeet.Audio;
using Parakeet.Core.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Models;
using Parakeet.Core.Muxing;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Tidying;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;

namespace Parakeet.App.Tests;

public class StartupShutdownTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ShutdownDuringColdLoadWaitsAndNeverStartsDecode(bool cooperativeLoad, bool runAgain)
    {
        var provider = new GatedProvider();
        provider.Engine.CooperativeLoad = cooperativeLoad;
        var (main, tone) = Create(provider);
        provider.IsRunning = () => main.Transcribe.IsRunning;
        main.Transcribe.AddFiles([tone]);

        var start = runAgain
            ? main.Transcribe.RunAgainCommand.ExecuteAsync(null)
            : main.Transcribe.StartCommand.ExecuteAsync(null);
        await provider.Engine.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(main.Transcribe.IsRunning);
        Assert.True(main.Models.IsTranscribing);
        Assert.False(main.Transcribe.CanStart);

        var shutdown = main.ShutdownAsync();
        Assert.True(provider.Engine.LoadToken.IsCancellationRequested);
        if (!cooperativeLoad)
        {
            Assert.False(shutdown.IsCompleted);
            Assert.False(provider.Engine.Disposed);
            Assert.Equal(0, provider.ReleaseCount);
        }

        provider.Engine.FinishLoad.TrySetResult();
        await Task.WhenAll(start, shutdown).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, provider.Engine.DecodeCount);
        Assert.True(provider.Engine.Disposed);
        Assert.False(provider.RunningAtRelease);
        Assert.Equal(1, provider.ReleaseCount);
        Assert.False(main.Transcribe.IsRunning);
        Assert.False(main.Session.IsLoaded);
        Assert.False(main.Transcribe.CanStart);

        // A close in progress cannot queue another load after releasing the native backend.
        await main.Transcribe.StartCommand.ExecuteAsync(null);
        await main.Transcribe.RunAgainCommand.ExecuteAsync(null);
        Assert.Equal(1, provider.CreateCount);
    }

    [Fact]
    public async Task CancellingAnUninterruptibleLoadLeavesTheQueueReadyForAnotherStart()
    {
        var provider = new GatedProvider();
        var (main, tone) = Create(provider);
        main.Transcribe.AddFiles([tone]);
        var start = main.Transcribe.StartCommand.ExecuteAsync(null);
        await provider.Engine.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        main.Transcribe.CancelCommand.Execute(null);
        Assert.True(provider.Engine.LoadToken.IsCancellationRequested);
        provider.Engine.FinishLoad.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, provider.Engine.DecodeCount);
        Assert.Equal("Cancelled.", main.Transcribe.StatusMessage);
        Assert.False(main.Transcribe.IsRunning);
        Assert.True(main.Transcribe.CanStart);

        await main.Transcribe.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, provider.CreateCount);
        Assert.Equal(1, provider.Engine.DecodeCount);
        Assert.Equal(Parakeet.Core.Jobs.JobState.Completed, Assert.Single(main.Transcribe.Jobs).State);
        await main.ShutdownAsync();
    }

    [Fact]
    public async Task ALoadFailureRestoresRunAndModelControls()
    {
        var provider = new GatedProvider();
        provider.Engine.FailLoad = true;
        var (main, tone) = Create(provider);
        main.Transcribe.AddFiles([tone]);
        var start = main.Transcribe.StartCommand.ExecuteAsync(null);
        await provider.Engine.LoadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Engine.FinishLoad.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(main.Transcribe.IsRunning);
        Assert.False(main.Models.IsTranscribing);
        Assert.True(main.Transcribe.CanStart);
        Assert.Equal("Controlled load failure.", main.Transcribe.StatusMessage);
        Assert.False(main.Session.IsLoaded);
        Assert.True(provider.Engine.Disposed);
        await main.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCancelsFetchAndWaitsForCleanupEvenWhenTheFetcherReturnsSuccess(bool returnsSuccess)
    {
        var provider = new GatedProvider();
        var fetcher = new GatedFetcher { ReturnsSuccessAfterCancellation = returnsSuccess };
        var (main, tone) = Create(provider, fetcher);
        fetcher.OutputPath = tone;
        main.Transcribe.Url = "https://example.com/controlled-fetch";
        var fetch = main.Transcribe.FetchUrlCommand.ExecuteAsync(null);
        await fetcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var shutdown = main.ShutdownAsync();
        Assert.True(fetcher.Token.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, provider.ReleaseCount);
        Assert.False(main.Transcribe.CanFetchUrl);

        fetcher.Finish.TrySetResult();
        await Task.WhenAll(fetch, shutdown).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(main.Transcribe.IsFetchingUrl);
        Assert.Equal("Cancelled.", main.Transcribe.UrlStatus);
        Assert.Empty(main.Transcribe.Jobs);
        Assert.Equal(1, provider.ReleaseCount);
        Assert.False(main.Transcribe.CanFetchUrl);
        await main.Transcribe.FetchUrlCommand.ExecuteAsync(null);
        Assert.Equal(1, fetcher.CallCount);
    }

    [Fact]
    public async Task StartWaitsForAFetchToFinishBeforeTakingTheQueueSnapshot()
    {
        var provider = new GatedProvider();
        var fetcher = new GatedFetcher();
        var (main, tone) = Create(provider, fetcher);
        fetcher.OutputPath = tone;
        main.Transcribe.AddFiles([tone]);
        main.Transcribe.Url = "https://example.com/controlled-fetch";
        var fetch = main.Transcribe.FetchUrlCommand.ExecuteAsync(null);
        await fetcher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(main.Transcribe.CanStart);
        await main.Transcribe.StartCommand.ExecuteAsync(null);
        await main.Transcribe.RunAgainCommand.ExecuteAsync(null);
        Assert.Equal(0, provider.CreateCount);

        fetcher.Finish.TrySetResult();
        await fetch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(main.Transcribe.CanStart);
        await main.ShutdownAsync();
    }

    [Fact]
    public async Task ShutdownWaitsForMuxCleanupAndRefusesFurtherExports()
    {
        var directory = TestTemp.NewDirectory("uindosill-shutdown-mux");
        var input = Path.Combine(directory, "recording.mp4");
        await File.WriteAllBytesAsync(input, [0]);
        var muxer = new GatedMuxer();
        var viewModel = new TranscribeViewModel(new FakeEngineProvider(), () => new EngineSelection(), muxer: muxer)
        {
            OutputDirectory = Path.Combine(directory, "exports"),
        };
        foreach (var format in viewModel.Formats)
        {
            format.IsSelected = format.Id == "srt";
        }

        var job = new JobViewModel(input);
        job.Complete(new Parakeet.Core.Jobs.JobResult
        {
            Job = new Parakeet.Core.Jobs.TranscriptionJob { InputPath = input },
            State = Parakeet.Core.Jobs.JobState.Completed,
            Document = new TranscriptDocument
            {
                SourceName = input,
                Segments = [new TranscriptSegment
                {
                    Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "test subtitle",
                }],
            },
        });
        viewModel.Jobs.Add(job);
        viewModel.SelectedJob = job;
        Assert.True(viewModel.CanExportFiles);
        Assert.True(viewModel.CanAddToRecording);

        var mux = viewModel.AddToRecordingCommand.ExecuteAsync(null);
        await muxer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = viewModel.StopAsync();

        Assert.True(muxer.Token.IsCancellationRequested);
        Assert.False(stopping.IsCompleted);
        Assert.False(viewModel.CanAddToRecording);
        Assert.False(viewModel.CanExportFiles);
        await viewModel.ExportFilesCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(viewModel.OutputDirectory));

        muxer.Finish.TrySetResult();
        await Task.WhenAll(mux, stopping).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(muxer.SubtitlePath));
        Assert.Empty(job.OutputFiles);
        await viewModel.AddToRecordingCommand.ExecuteAsync(null);
        Assert.Equal(1, muxer.CallCount);
    }

    private static (MainWindowViewModel Main, string Tone) Create(GatedProvider provider, IMediaUrlFetcher? fetcher = null)
    {
        var directory = TestTemp.NewDirectory("uindosill-startup-shutdown");
        var tone = Path.Combine(directory, "input.wav");
        WavWriter.WriteFile(tone, new float[16000], 16000);
        var main = new MainWindowViewModel(
            provider, new LocalModelStore(Path.Combine(directory, "models")), ModelCatalog.Default,
            settings: new AppSettingsStore(Path.Combine(directory, "settings.json")),
            backendsOnDisk: () => [ComputeBackend.Cpu], player: new FakeMediaPlayer(),
            fetcher: fetcher, downloadRoot: Path.Combine(directory, "downloads"));
        return (main, tone);
    }

    private sealed class GatedEngine : ITranscriptionEngine
    {
        public TaskCompletionSource LoadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishLoad { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken LoadToken { get; private set; }
        public bool CooperativeLoad { get; set; }
        public bool FailLoad { get; set; }
        public bool Disposed { get; private set; }
        public int DecodeCount { get; private set; }
        public EngineCapabilities Capabilities { get; } = new()
        {
            EngineName = "controlled", ModelId = "controlled", Backend = ComputeBackend.Cpu,
        };

        public async ValueTask LoadAsync(CancellationToken ct = default)
        {
            LoadToken = ct;
            LoadEntered.TrySetResult();
            // Covers both cooperative managed loaders and native loads with no abort API.
            if (CooperativeLoad)
            {
                await FinishLoad.Task.WaitAsync(ct);
            }
            else
            {
                await FinishLoad.Task;
            }

            if (FailLoad)
            {
                throw new InvalidOperationException("Controlled load failure.");
            }
        }

        public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
            IAudioSource audio, TranscriptionOptions options, IProgress<TranscriptionProgress>? progress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DecodeCount++;
            await Task.Yield();
            yield return new TranscriptSegment
            {
                Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "controlled transcript",
            };
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedProvider : IEngineProvider
    {
        public GatedEngine Engine { get; } = new();
        public Func<bool>? IsRunning { get; set; }
        public bool? RunningAtRelease { get; private set; }
        public int ReleaseCount { get; private set; }
        public int CreateCount { get; private set; }
        public bool IsModelAvailable(EngineSelection selection) => true;
        public ITranscriptionEngine Create(EngineSelection selection) { CreateCount++; return Engine; }
        public bool SupportsSpeakerLabelling => false;
        public ISpeakerLabeller? CreateSpeakerLabeller() => null;
        public SpeakerLabellerLimits? SpeakerLimits => null;
        public bool SupportsDiariserBatchSize => false;
        public bool DiariserRunsInTorch => false;
        public string? DiarisationModelDirectory => null;
        public Task<IReadOnlyList<string>?> AvailableDiariserProvidersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>?>(null);
        public string? DescribeLabeller(ISpeakerLabeller labeller) => null;
        public string? DescribeTranslator(ITranscriptTranslator translator) => null;
        public string? DescribeUnavailable(ModelTask task) => "Not part of this test.";
        public bool SupportsTranslation(ModelDescriptor? recogniser) => false;
        public string? DescribeUnavailableTranslation(ModelDescriptor? recogniser) => "Not part of this test.";
        public ITranscriptTranslator? CreateTranslator(ModelDescriptor? recogniser) => null;
        public bool SupportsTidying => false;
        public ITranscriptTidier? CreateTidier() => null;
        public bool SupportsNeuralSpeechDetection => false;
        public ISpeechDetector? CreateSpeechDetector() => null;
        public void ReleaseBackend() { ReleaseCount++; RunningAtRelease = IsRunning?.Invoke(); }
    }

    private sealed class GatedFetcher : IMediaUrlFetcher
    {
        public bool IsAvailable => true;
        public string? DescribeUnavailable() => null;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string OutputPath { get; set; } = string.Empty;
        public bool ReturnsSuccessAfterCancellation { get; set; }
        public CancellationToken Token { get; private set; }
        public int CallCount { get; private set; }

        public async Task<FetchedMedia> FetchAudioAsync(string url, string root,
            IProgress<UrlFetchProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Token = cancellationToken;
            Entered.TrySetResult();
            await Finish.Task;
            if (!ReturnsSuccessAfterCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new FetchedMedia(OutputPath, "Fetched recording", url);
        }
    }

    private sealed class GatedMuxer : ISubtitleMuxer
    {
        public bool IsAvailable => true;
        public string? DescribeUnavailable() => null;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public string? SubtitlePath { get; private set; }
        public int CallCount { get; private set; }

        public async Task<string> MuxAsync(SubtitleMuxPlan plan, string subtitlePath,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Token = cancellationToken;
            SubtitlePath = subtitlePath;
            Entered.TrySetResult();
            await Finish.Task;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("This mux must be cancelled.");
        }
    }
}
