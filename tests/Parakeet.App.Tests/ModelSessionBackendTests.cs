using Parakeet.App.Services;
using Parakeet.App.ViewModels;
using Parakeet.Core.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Models;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Tidying;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;

namespace Parakeet.App.Tests;

public class ModelSessionBackendTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLoadReportsWhetherTheProviderBoundItsBackend(bool bindsBackend)
    {
        var provider = new ControlledProvider();
        provider.Engine.OnLoad = _ =>
        {
            provider.IsBackendFixed = bindsBackend;
            throw new InvalidOperationException("Controlled model load failure.");
        };
        await using var session = new ModelSession(provider);
        var models = new ModelsViewModel(
            new LocalModelStore(TestTemp.NewDirectory("uindosill-backend-failure")),
            ModelCatalog.Default,
            session: session);
        var finalNotificationSawFixedBackend = false;
        session.Changed += (_, _) => finalNotificationSawFixedBackend = !session.IsBusy && session.IsBackendFixed;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.LoadAsync(new EngineSelection()));

        Assert.False(session.IsLoaded);
        Assert.True(provider.Engine.Disposed);
        Assert.Equal(bindsBackend, session.IsBackendFixed);
        Assert.Equal(bindsBackend, finalNotificationSawFixedBackend);
        if (bindsBackend)
        {
            Assert.Contains("Restart Uindosill to change the backend", models.BackendNote, StringComparison.Ordinal);
            Assert.Contains("native library has initialized", models.BackendNote, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("Choose a backend before loading", models.BackendNote, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledLoadReportsWhetherTheProviderBoundItsBackend(bool bindsBackend)
    {
        var provider = new ControlledProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Engine.OnLoad = async ct =>
        {
            provider.IsBackendFixed = bindsBackend;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        await using var session = new ModelSession(provider);
        using var cancelled = new CancellationTokenSource();

        var loading = session.LoadAsync(new EngineSelection(), cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);

        Assert.False(session.IsLoaded);
        Assert.True(provider.Engine.Disposed);
        Assert.Equal(bindsBackend, session.IsBackendFixed);
    }

    [Fact]
    public async Task FailureBeforeCreatingAnEngineDoesNotFixTheBackend()
    {
        var provider = new ControlledProvider();
        await using var session = new ModelSession(provider)
        {
            BeforeLoadAsync = () => Task.FromException(new InvalidOperationException("Earlier work failed.")),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.LoadAsync(new EngineSelection()));

        Assert.Equal(0, provider.CreateCount);
        Assert.False(session.IsBackendFixed);
        Assert.False(session.IsLoaded);
    }

    [Fact]
    public async Task AProviderAlreadyBoundInThisProcessReportsItBeforeLoadingAModel()
    {
        var provider = new ControlledProvider { IsBackendFixed = true };
        await using var session = new ModelSession(provider);

        Assert.True(session.IsBackendFixed);
        Assert.False(session.IsLoaded);
    }

    [Fact]
    public async Task SuccessfulFakeLoadRetainsItsBackendStateAfterUnload()
    {
        await using var session = new ModelSession(new FakeEngineProvider());
        await session.LoadAsync(new EngineSelection());
        await session.UnloadAsync();

        Assert.True(session.IsBackendFixed);
        Assert.False(session.IsLoaded);
    }

    private sealed class ControlledEngine : ITranscriptionEngine
    {
        public Func<CancellationToken, ValueTask> OnLoad { get; set; } = _ => ValueTask.CompletedTask;
        public bool Disposed { get; private set; }
        public EngineCapabilities Capabilities => new() { EngineName = "controlled", Backend = ComputeBackend.Cpu };
        public ValueTask LoadAsync(CancellationToken ct = default) => OnLoad(ct);
        public IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
            IAudioSource audio, TranscriptionOptions options, IProgress<TranscriptionProgress>? progress = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ControlledProvider : IEngineProvider
    {
        public ControlledEngine Engine { get; } = new();
        public bool IsBackendFixed { get; set; }
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
        public void ReleaseBackend() { }
    }
}
