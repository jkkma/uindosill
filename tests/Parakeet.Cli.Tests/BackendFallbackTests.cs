using Parakeet.Core.Audio;
using Parakeet.Core.Transcription;

namespace Parakeet.Cli.Tests;

/// <summary>
/// The line a transcribe run prints when the native loader did not give it the backend it asked
/// for, and — the part that was broken — the moment it is allowed to look.
/// </summary>
/// <remarks>
/// <para>
/// The check shipped on 2026-08-20 reading <c>Capabilities.Backend</c> off an engine that
/// <c>EngineFactory</c> had just constructed. A parakeet.cpp engine answers that with the backend
/// it was <em>asked</em> for until <c>LoadAsync</c> rewrites it from the loader, so the comparison
/// was the request against itself, the guard always returned, and a machine carrying the CUDA drop
/// with no working driver behind it fell to CPU in silence — the exact case the line exists for.
/// </para>
/// <para>
/// Nothing here loads a model or touches a native library: <see cref="StubEngine"/> is the whole
/// failure in miniature, reporting the requested backend before the load and the fallen-back one
/// after, so a check made too early reads it as agreement and this file goes red. That is the only
/// way CI can hold the timing — the real thing needs the Windows natives and weights, and
/// <c>--fake</c> is excluded from the check by design.
/// </para>
/// </remarks>
public class BackendFallbackTests
{
    [Fact]
    public async Task TheBackendIsReadAfterTheLoadAndNotBefore()
    {
        await using var engine = new StubEngine(ComputeBackend.Cuda, ComputeBackend.Cpu);

        var message = await TranscribeCommand.LoadAndDescribeBackendAsync(
            engine, ComputeBackend.Cuda, wasNamed: true, TestContext.Current.CancellationToken);

        Assert.Equal(1, engine.LoadCount);
        Assert.NotNull(message);
        Assert.Contains("fell back to cpu", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineThatGotWhatItAskedForSaysNothing()
    {
        await using var engine = new StubEngine(ComputeBackend.Cuda, ComputeBackend.Cuda);

        Assert.Null(await TranscribeCommand.LoadAndDescribeBackendAsync(
            engine, ComputeBackend.Cuda, wasNamed: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ANamedBackendIsQuotedBackAsTheUsersOwnChoice()
    {
        var message = TranscribeCommand.DescribeBackendFallback(
            ComputeBackend.Cuda, ComputeBackend.Cpu, wasNamed: true);

        Assert.Equal(
            "cuda was requested but the native loader fell back to cpu." +
            "  Vulkan is not tried after CUDA; pass --backend vulkan for the other GPU tier.",
            message);
    }

    [Fact]
    public void ABackendNobodyTypedIsExplainedRatherThanBlamedOnTheUser()
    {
        // The default resolves to the fastest tier whose binaries are on disk, so on the CUDA drop
        // this fires at somebody who typed nothing at all. Telling them "cuda was requested" would
        // be telling them they did something they did not do.
        var message = TranscribeCommand.DescribeBackendFallback(
            ComputeBackend.Cuda, ComputeBackend.Cpu, wasNamed: false);

        Assert.NotNull(message);
        Assert.StartsWith("cuda was chosen automatically", message, StringComparison.Ordinal);
        Assert.Contains("fell back to cpu", message, StringComparison.Ordinal);
    }

    [Fact]
    public void LandingOnVulkanIsNotToldToTryVulkan()
    {
        var message = TranscribeCommand.DescribeBackendFallback(
            ComputeBackend.Cuda, ComputeBackend.Vulkan, wasNamed: true);

        Assert.Equal("cuda was requested but the native loader fell back to vulkan.", message);
    }

    [Fact]
    public void TheVulkanHintIsOnlyForCudaRequests()
    {
        // The loader's chain is CUDA then CPU. A Vulkan request that fell to CPU has no third tier
        // left to suggest, and suggesting the one it just failed on would be nonsense.
        var message = TranscribeCommand.DescribeBackendFallback(
            ComputeBackend.Vulkan, ComputeBackend.Cpu, wasNamed: true);

        Assert.Equal("vulkan was requested but the native loader fell back to cpu.", message);
    }

    /// <summary>
    /// An engine whose reported backend changes at load, which is the one behaviour of the real
    /// one that this check depends on.
    /// </summary>
    private sealed class StubEngine(ComputeBackend requested, ComputeBackend afterLoad) : ITranscriptionEngine
    {
        public int LoadCount { get; private set; }

        public EngineCapabilities Capabilities { get; private set; } = new()
        {
            EngineName = "stub",
            ModelId = "stub-model",
            Backend = requested,
        };

        public ValueTask LoadAsync(CancellationToken ct = default)
        {
            LoadCount++;
            Capabilities = Capabilities with { Backend = afterLoad };
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
            IAudioSource audio,
            TranscriptionOptions options,
            IProgress<TranscriptionProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This stub exists for the load, not for a decode.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
