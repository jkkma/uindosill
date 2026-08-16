using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp.Interop;

namespace Parakeet.Engine.ParakeetCpp.Tests;

/// <summary>
/// The bf16 Vulkan workaround. None of these load a model — what is checked is the plumbing that
/// decides whether the knob is set, and the mechanism that makes a value visible to native code.
/// </summary>
public class VulkanWorkaroundTests
{
    private const string Variable = ParakeetCppEngine.VulkanDisableBFloat16Variable;

    [Fact]
    public void TheWorkaroundIsOffByDefault()
    {
        // Turning it on for every Vulkan device would change the configuration every measured
        // Vulkan figure in docs/UNPROVEN.md was taken under, on hardware this project cannot
        // re-measure from the machine that found the bug.
        var options = new ParakeetCppOptions { ModelPath = "x.gguf" };

        Assert.False(options.DisableVulkanBFloat16);
    }

    [Fact]
    public async Task NothingIsReportedAsAppliedBeforeALoadIsAttempted()
    {
        await using var engine = new ParakeetCppEngine(new ParakeetCppOptions
        {
            ModelPath = "x.gguf",
            Backend = ComputeBackend.Vulkan,
            DisableVulkanBFloat16 = true,
        });

        // The option is a request. The property records what actually happened, and nothing has
        // happened yet — the knob is set inside LoadAsync, because ggml reads it during device
        // initialisation and that runs once per process.
        Assert.False(engine.VulkanBFloat16WorkaroundApplied);
    }

    [Fact]
    public void SettingAVariableReachesTheManagedViewAndReportsSuccess()
    {
        var name = $"UINDOSILL_TEST_{Guid.NewGuid():N}";
        try
        {
            Assert.False(NativeEnvironment.IsSet(name));

            var ok = NativeEnvironment.Set(name, "1");

            Assert.True(ok);
            Assert.True(NativeEnvironment.IsSet(name));
            Assert.Equal("1", Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void AnEmptyVariableDoesNotCountAsSet()
    {
        // getenv treats an empty value as present, but an empty knob is not a decision anyone made,
        // and treating it as one would let a stray "" suppress the workaround silently.
        var name = $"UINDOSILL_TEST_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(name, string.Empty);

            Assert.False(NativeEnvironment.IsSet(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void TheKnobIsTheOneGgmlReads() =>
        // Spelled out rather than asserted loosely: this exact name is what the vendored
        // parakeet.dll contains, and a typo here would be a silent no-op on the machines that
        // need it.
        Assert.Equal("GGML_VK_DISABLE_BFLOAT16", Variable);
}
