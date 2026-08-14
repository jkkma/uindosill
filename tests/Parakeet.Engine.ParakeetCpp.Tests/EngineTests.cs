using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp.Interop;

namespace Parakeet.Engine.ParakeetCpp.Tests;

public class ParakeetCppEngineTests
{
    private static ParakeetCppOptions Options(string modelPath) => new() { ModelPath = modelPath };

    [Fact]
    public void EmptyModelPathIsRejectedAtConstruction() =>
        Assert.Throws<ArgumentException>(() => new ParakeetCppEngine(Options(string.Empty)));

    [Fact]
    public void BatchSizeBelowOneIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ParakeetCppEngine(new ParakeetCppOptions { ModelPath = "model.gguf", BatchSize = 0 }));

    [Fact]
    public async Task MissingModelFileIsReportedBeforeAnyNativeCall()
    {
        // Checked in managed code first: reaching the native loader for a file that is not there
        // turns a clear "model not installed" into a load failure nobody can act on.
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.gguf");
        await using var engine = new ParakeetCppEngine(Options(path));

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await engine.LoadAsync());
    }

    [Fact]
    public void DefaultBackendIsVulkanNotCuda()
    {
        // Vulkan runs on three vendors with an ordinary graphics driver and needs no 553 MB
        // CUDA runtime download, so it is the default GPU tier and CUDA is opt-in.
        var options = new ParakeetCppOptions { ModelPath = "x.gguf" };
        Assert.Equal(ComputeBackend.Vulkan, options.Backend);
    }

    [Fact]
    public void CapabilitiesDeclareTheLimitsHonestly()
    {
        using var _ = new object() as IDisposable;
        var engine = new ParakeetCppEngine(Options("x.gguf"));
        var capabilities = engine.Capabilities;

        Assert.True(capabilities.SupportsWordTimestamps);
        Assert.True(capabilities.SupportsBatchDecode);

        // Both false, both verified against the header rather than assumed: there is no abort
        // hook and no thread-count parameter anywhere in ABI v6.
        Assert.False(capabilities.SupportsDecodeCancellation);
        Assert.False(capabilities.SupportsThreadCount);

        Assert.Equal(TimeSpan.FromSeconds(30), capabilities.MaxSingleDecodeLength);
    }

    [Fact]
    public void WarmUpIsOnByDefault()
    {
        // Without it the first decode pays arena allocation and graph construction, and every
        // first benchmark number is inflated.
        Assert.True(new ParakeetCppOptions { ModelPath = "x.gguf" }.WarmUp);
    }
}

public class ParakeetNativeLibraryTests
{
    [Fact]
    public void AbiVersionThisBindingTargetsIsSix() =>
        Assert.Equal(6, NativeMethods.ExpectedAbiVersion);

    [Fact]
    public void LoadFailureListsWhereItLookedAndPointsAtTheVendoringDocs()
    {
        ParakeetNativeLibrary.Configure(
            ComputeBackend.Cpu,
            allowFallback: false,
            nativeDirectory: Path.Combine(Path.GetTempPath(), $"no-native-{Guid.NewGuid():N}"));

        var exception = Record.Exception(() => ParakeetNativeLibrary.EnsureLoadedAndCompatible());

        // On a machine that happens to have the library installed this succeeds; that is a
        // legitimate outcome, so only the failure shape is asserted.
        if (exception is null)
        {
            Assert.NotNull(ParakeetNativeLibrary.LoadedPath);
            return;
        }

        var load = Assert.IsType<ParakeetNativeLoadException>(exception);
        Assert.Contains("docs/NATIVE-BINARIES.md", load.Message, StringComparison.Ordinal);
        Assert.Contains("Paths tried:", load.Message, StringComparison.Ordinal);
        Assert.NotEmpty(ParakeetNativeLibrary.AttemptedPaths);
    }

    [Fact]
    public void SearchIncludesPerBackendSubdirectories()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"no-native-{Guid.NewGuid():N}");
        ParakeetNativeLibrary.Configure(ComputeBackend.Vulkan, allowFallback: true, nativeDirectory: directory);

        try
        {
            ParakeetNativeLibrary.EnsureLoadedAndCompatible();
        }
        catch (ParakeetNativeLoadException)
        {
            // Expected on a machine without the native library.
        }

        var attempts = ParakeetNativeLibrary.AttemptedPaths;
        if (attempts.Count == 0)
        {
            Assert.SkipWhen(true, "The native library is installed on this machine, so nothing was searched.");
            return;
        }

        Assert.Contains(attempts, p => p.Contains("vulkan", StringComparison.Ordinal));
        Assert.Contains(attempts, p => p.Contains("cpu", StringComparison.Ordinal));
    }

    [Fact]
    public void AbiMismatchMessageExplainsWhyItRefusesRatherThanAdapts()
    {
        var exception = new ParakeetAbiMismatchException(6, 5);

        Assert.Equal(6, exception.Expected);
        Assert.Equal(5, exception.Actual);
        Assert.Contains("corrupts memory", exception.Message, StringComparison.Ordinal);
    }
}
