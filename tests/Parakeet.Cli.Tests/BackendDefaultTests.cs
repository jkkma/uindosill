using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp.Interop;

namespace Parakeet.Cli.Tests;

/// <summary>
/// What <c>--backend</c> means when it is given, and when it is not.
/// </summary>
/// <remarks>
/// A bare <c>--backend</c> meant Vulkan unconditionally until 2026-08-20, so a build carrying the
/// CUDA drop ran at RTF 0.0110 where 0.0064 was available unless every single invocation said so.
/// It now resolves to the fastest tier whose binaries are present, sharing one rule with the
/// window's first-run default — <see cref="ParakeetNativeLibrary.PreferredBackend"/> — so an install
/// cannot disagree with itself about which tier it runs.
/// </remarks>
public class BackendDefaultTests
{
    [Theory]
    [InlineData("cpu", ComputeBackend.Cpu)]
    [InlineData("vulkan", ComputeBackend.Vulkan)]
    [InlineData("vk", ComputeBackend.Vulkan)]
    [InlineData("cuda", ComputeBackend.Cuda)]
    [InlineData("nvidia", ComputeBackend.Cuda)]
    [InlineData("  CUDA  ", ComputeBackend.Cuda)]
    public void ANamedBackendIsTakenLiterally(string value, ComputeBackend expected) =>
        Assert.Equal(expected, EngineFactory.ParseBackend(value));

    [Fact]
    public void AnUnknownBackendIsRefusedRatherThanDefaulted()
    {
        // Refused, not silently resolved to the default: a typo that quietly ran somewhere else
        // would be a benchmark attributed to the wrong tier, which this repository has a rule about.
        var exception = Assert.Throws<CliUsageException>(() => EngineFactory.ParseBackend("metal"));

        Assert.Contains("Choose cpu, vulkan or cuda", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingNamedResolvesToTheSharedDefault(string? value) =>
        Assert.Equal(EngineFactory.DefaultBackend(), EngineFactory.ParseBackend(value));

    [Fact]
    public void TheDefaultIsTheSameRuleTheWindowUses() =>
        // Not "both happen to say Vulkan on this machine" — the same function. Two copies of this
        // rule is one install answering the question two ways depending on which front end asked.
        Assert.Equal(ParakeetNativeLibrary.PreferredBackendOnDisk(), EngineFactory.DefaultBackend());

    [Fact]
    public void TheRequestRecordDefaultsToTheSameThingToo() =>
        // The record's initialiser is dead code today — every call site passes ParseBackend — and
        // that is exactly why it would rot into a second answer if it were left as a literal.
        Assert.Equal(EngineFactory.DefaultBackend(), new EngineRequest().Backend);

    [Theory]
    [InlineData(ComputeBackend.Cuda)]
    [InlineData(ComputeBackend.Cpu, ComputeBackend.Vulkan, ComputeBackend.Cuda)]
    public void CudaWinsWhenItIsThere(params ComputeBackend[] present) =>
        Assert.Equal(ComputeBackend.Cuda, ParakeetNativeLibrary.PreferredBackend(present));

    [Fact]
    public void TheDefaultChannelResolvesToVulkan() =>
        Assert.Equal(
            ComputeBackend.Vulkan,
            ParakeetNativeLibrary.PreferredBackend([ComputeBackend.Cpu, ComputeBackend.Vulkan]));

    [Fact]
    public void NoNativesAtAllStillResolvesToVulkan() =>
        // A build from source with nothing vendored: resolve to what always shipped and let the
        // loader's own message name what is missing, rather than settling for the slowest tier.
        Assert.Equal(ComputeBackend.Vulkan, ParakeetNativeLibrary.PreferredBackend([]));

    [Fact]
    public void ACpuOnlyDropResolvesToCpu() =>
        Assert.Equal(ComputeBackend.Cpu, ParakeetNativeLibrary.PreferredBackend([ComputeBackend.Cpu]));

    [Fact]
    public void TheProbeReportsOnlyWhatIsOnDisk()
    {
        // Whatever the test host has beside it, the answer has to be a subset of the three real
        // tiers and never invent one. The suite runs with no natives vendored, so in CI this is
        // empty; asserting the shape rather than the content is what keeps it true either way.
        var present = ParakeetNativeLibrary.BackendsPresentOnDisk();

        Assert.All(present, backend => Assert.Contains(backend, Enum.GetValues<ComputeBackend>()));
        Assert.Equal(present.Distinct().Count(), present.Count);
    }
}
