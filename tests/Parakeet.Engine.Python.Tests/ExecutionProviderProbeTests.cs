using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// The probe behind the window's speaker-provider picker, driven through a real
/// <see cref="PythonSidecar"/> against the scripted stand-in.
/// </summary>
/// <remarks>
/// <b>Why this exists at all.</b> The Settings tab offered a CUDA row on 2026-08-27 that could not
/// work on any machine: the bundle pins <c>onnxruntime-webgpu</c>, whose wheel carries the WebGPU
/// and CPU providers and no CUDA one, so the row was offered on the strength of hardware rather than
/// of the installed runtime. These tests hold the correction — that the menu is built from what the
/// runtime reports, and that a probe which cannot answer leaves the caller offering what it did
/// before rather than emptying the menu.
/// <para>
/// Every case goes through <see cref="SidecarExecutionProviders.ProbeAsync(PythonSidecar,
/// System.Threading.CancellationToken)"/>, the uncached entry point, so nothing here touches the
/// process-wide answer the window uses and these can run beside anything else.
/// </para>
/// </remarks>
public class ExecutionProviderProbeTests
{
    private static object ScriptReplying(string providersReply) => new
    {
        rules = new[]
        {
            new { op = "hello", emit = new[] { FakeSidecarProcess.Handshake } },
            new { op = "providers", emit = new[] { providersReply } },
        },
    };

    [Fact]
    public async Task TheBundlesOwnProviderSetComesBackAsCpuAndWebgpuAndNoCuda()
    {
        // Verbatim what `onnxruntime-webgpu` 1.27.0 reports on this project's bundle. The absence of
        // CUDA is the whole point: an NVIDIA card does not put a CUDA provider in this wheel.
        using var fake = FakeSidecarProcess.Scripted(ScriptReplying(
            """{"id":{id},"type":"result","available":["WebGpuExecutionProvider","CPUExecutionProvider"],"usable":["WebGpuExecutionProvider","CPUExecutionProvider"],"onnxruntime":"1.27.0"}"""));

        await using var sidecar = new PythonSidecar(fake.Resolution);

        var providers = await SidecarExecutionProviders.ProbeAsync(sidecar);

        Assert.NotNull(providers);
        Assert.Equal(["cpu", "webgpu"], providers);
        Assert.DoesNotContain("cuda", providers!);
    }

    [Fact]
    public async Task ACudaCapableRuntimeIsReportedAsSuch()
    {
        // The other half: where the runtime does carry it, the row must appear. A menu filtered by
        // a hard-coded "the bundle has no CUDA" would be wrong here and would go stale silently.
        using var fake = FakeSidecarProcess.Scripted(ScriptReplying(
            """{"id":{id},"type":"result","available":["CUDAExecutionProvider","CPUExecutionProvider"],"onnxruntime":"1.29.0"}"""));

        await using var sidecar = new PythonSidecar(fake.Resolution);

        var providers = await SidecarExecutionProviders.ProbeAsync(sidecar);

        Assert.Equal(["cpu", "cuda"], providers);
    }

    [Fact]
    public async Task ProvidersThisProjectDoesNotSpeakAreLeftOut()
    {
        // An unknown name must not reach a menu. `ExecutionProviders.Parse` turns one into CPU,
        // which is right for reading a backend off a finished run and wrong for offering a choice.
        using var fake = FakeSidecarProcess.Scripted(ScriptReplying(
            """{"id":{id},"type":"result","available":["TensorrtExecutionProvider","VitisAIExecutionProvider","CPUExecutionProvider"]}"""));

        await using var sidecar = new PythonSidecar(fake.Resolution);

        Assert.Equal(["cpu"], await SidecarExecutionProviders.ProbeAsync(sidecar));
    }

    [Fact]
    public async Task AReplyWithoutTheListIsNotEstablishedRatherThanEmpty()
    {
        // Null and empty are different answers to the window: null keeps every row on offer, empty
        // would strand a user with no way to choose. A malformed reply must produce the first.
        using var fake = FakeSidecarProcess.Scripted(ScriptReplying(
            """{"id":{id},"type":"result","onnxruntime":"1.27.0"}"""));

        await using var sidecar = new PythonSidecar(fake.Resolution);

        Assert.Null(await SidecarExecutionProviders.ProbeAsync(sidecar));
    }

    [Fact]
    public async Task ASidecarThatFailsItsHandshakeIsNotEstablishedRatherThanAThrow()
    {
        // A probe is a convenience on a settings page. It must never be the reason a window cannot
        // draw one, so the failure is swallowed into "not established".
        using var fake = FakeSidecarProcess.Scripted(new
        {
            rules = new[]
            {
                new { op = "hello", emit = new[] { """{"id":{id},"type":"result","protocol":99}""" } },
            },
        });

        await using var sidecar = new PythonSidecar(fake.Resolution);

        Assert.Null(await SidecarExecutionProviders.ProbeAsync(sidecar));
    }
}
