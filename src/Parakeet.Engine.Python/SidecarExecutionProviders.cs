using System.Text.Json;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.Python;

/// <summary>
/// Which execution providers this machine's ONNX Runtime can actually reach, asked of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked rather than inferred, and the alternative is a second copy of a pin.</b> The bundle
/// pins <c>onnxruntime-webgpu</c>, whose wheel carries the WebGPU and CPU providers and no CUDA
/// one — so a host that reasoned "this build is NVIDIA-capable" from the presence of a card would
/// be wrong on every machine. It could equally hard-code "the bundle has no CUDA", which is true
/// today and is exactly the kind of fact that goes stale in a requirements file without anyone
/// touching the code that repeats it. Only onnxruntime knows what onnxruntime registered, so
/// onnxruntime is asked.
/// </para>
/// <para>
/// <b>It costs a sidecar start, which is why it is cached and never on a hot path.</b> The
/// <c>providers</c> op reports each engine's <c>auto</c> resolution as well as the raw list, and
/// getting that honestly costs the engines' imports — seconds of torch. One probe per process, off
/// the UI thread, is the price of a control that does not offer what cannot work.
/// </para>
/// <para>
/// <b>A failed probe reports null, not an empty list, and the difference is the whole point.</b>
/// Empty would mean "this machine can reach nothing", which would empty a picker and strand a user
/// with no way to choose; null means "not established", and the caller is expected to keep offering
/// what it offered before. A missing interpreter, a sidecar that will not start and a probe that
/// timed out are all "not established".
/// </para>
/// </remarks>
public static class SidecarExecutionProviders
{
    /// <summary>
    /// How long to wait for the probe before giving up on it. Generous because the op imports torch
    /// on a cold file cache, and abandoning it early would report "not established" on a machine
    /// that is merely slow — which is the one answer that costs a user a control they could use.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>ONNX Runtime's names for the providers this project speaks, and ours for them.</summary>
    /// <remarks>
    /// The inverse of <see cref="ExecutionProviders.Parse"/>'s mapping, and deliberately not derived
    /// from it: that one turns an unknown name into <see cref="ComputeBackend.Cpu"/>, which is right
    /// for reading a backend off a completed run and wrong here, where an unknown name must simply
    /// not appear in a menu.
    /// </remarks>
    private static readonly (string Runtime, string Protocol)[] Names =
    [
        ("CPUExecutionProvider", "cpu"),
        ("WebGpuExecutionProvider", "webgpu"),
        ("CUDAExecutionProvider", "cuda"),
        ("DmlExecutionProvider", "dml"),
    ];

    private static IReadOnlyList<string>? _cached;
    private static bool _probed;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// The protocol names of the providers ONNX Runtime registered here, or null when that could
    /// not be established. Probed once per process; every later call returns the same answer.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> QueryAsync(CancellationToken ct = default)
    {
        if (_probed)
        {
            return _cached;
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_probed)
            {
                return _cached;
            }

            _cached = await ProbeCoreAsync(null, ct).ConfigureAwait(false);

            // Set after the probe, so a cancellation does not cache "not established" forever and
            // leave a picker permanently short on a machine that was merely interrupted.
            _probed = !ct.IsCancellationRequested;
            return _cached;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// One uncached probe of a sidecar the caller owns. Returns null when it cannot be established.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately outside the cache</b>, which is what makes it safe to call from tests running
    /// in parallel: a shared static answer written by one test and read by another is a flake
    /// waiting for a slow machine. The cached entry point is for the window, which wants one answer
    /// per process; this one is for anybody holding a specific sidecar and wanting a fresh answer
    /// from it.
    /// </remarks>
    public static Task<IReadOnlyList<string>?> ProbeAsync(PythonSidecar sidecar, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        return ProbeCoreAsync(sidecar, ct);
    }

    /// <summary>Forgets the cached answer. For a runtime that has been replaced under a running window.</summary>
    public static void Reset()
    {
        _cached = null;
        _probed = false;
    }

    private static async Task<IReadOnlyList<string>?> ProbeCoreAsync(PythonSidecar? supplied, CancellationToken ct)
    {
        PythonSidecar? own = null;
        try
        {
            if (supplied is null)
            {
                if (!PythonRuntime.TryResolve(out var resolution, out _) || resolution is null)
                {
                    return null;
                }

                own = new PythonSidecar(resolution);
            }

            var sidecar = supplied ?? own!;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            await sidecar.StartAsync(timeout.Token).ConfigureAwait(false);
            var reply = await sidecar.SendAsync("providers", _ => { }, null, timeout.Token)
                .ConfigureAwait(false);

            return Read(reply);
        }
#pragma warning disable CA1031 // A probe that throws must not stop the window; it reports "not established".
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The protocol names in <c>available</c>, in this project's order rather than the runtime's.
    /// </summary>
    /// <remarks>
    /// <c>available</c> and not <c>usable</c>: that field is the sidecar's opinion about what may be
    /// chosen <i>automatically</i> and excludes DirectML on measured grounds, which is a different
    /// question from what a person may name. Filtering a menu by it would hide a provider this
    /// project deliberately allows somebody to ask for.
    /// </remarks>
    private static IReadOnlyList<string>? Read(JsonElement reply)
    {
        if (!reply.TryGetProperty("available", out var available)
            || available.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in available.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } name)
            {
                registered.Add(name);
            }
        }

        return registered.Count == 0
            ? null
            : Names.Where(pair => registered.Contains(pair.Runtime)).Select(pair => pair.Protocol).ToArray();
    }
}
