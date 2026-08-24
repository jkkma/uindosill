using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer;

/// <summary>One vendored server drop: where it is and which backend it carries.</summary>
public sealed record LlamaServerInstall
{
    public required string Directory { get; init; }

    public required string ExecutablePath { get; init; }

    public required ComputeBackend Backend { get; init; }
}

/// <summary>
/// Finds the vendored <c>llama-server.exe</c>, the way the ASR loader finds <c>parakeet.dll</c>:
/// <c>native/win-x64/llm/&lt;backend&gt;/</c> under the application's own directory, best backend
/// first, and the caller is told which one was taken — a fallback that does not say it fell back
/// is a CUDA machine silently running CPU.
/// </summary>
public static class LlamaServerLocator
{
    /// <summary>
    /// Best first: CUDA is the desktop tier, Vulkan the portable default, CPU beneath both —
    /// the same order as `docs/NATIVE-BINARIES.md` gives the ASR tier. Presence on disk is the
    /// only test made here; whether the backend works on this machine is only ever shown by
    /// running it.
    /// </summary>
    public static IReadOnlyList<ComputeBackend> ProbeOrder { get; } =
        [ComputeBackend.Cuda, ComputeBackend.Vulkan, ComputeBackend.Cpu];

    /// <summary>
    /// The drop for <paramref name="backend"/>, or the first present in <see cref="ProbeOrder"/>
    /// when null. Null when nothing is vendored — a state the caller reports, not an error here,
    /// because a clone without natives is the normal state of a clone.
    /// </summary>
    public static LlamaServerInstall? TryFind(ComputeBackend? backend = null, string? root = null)
    {
        root ??= Path.Combine(AppContext.BaseDirectory, "native", "win-x64", "llm");

        IReadOnlyList<ComputeBackend> candidates = backend is { } chosen ? [chosen] : ProbeOrder;
        foreach (var candidate in candidates)
        {
            var directory = Path.Combine(root, BackendDirectoryName(candidate));
            var executable = Path.Combine(directory, "llama-server.exe");
            if (File.Exists(executable))
            {
                return new LlamaServerInstall
                {
                    Directory = directory,
                    ExecutablePath = executable,
                    Backend = candidate,
                };
            }
        }

        return null;
    }

    /// <summary>The directory name `scripts/vendor-llm-natives.ps1` unpacks each backend into.</summary>
    public static string BackendDirectoryName(ComputeBackend backend) => backend switch
    {
        ComputeBackend.Cpu => "cpu",
        ComputeBackend.Vulkan => "vulkan",
        ComputeBackend.Cuda => "cuda",
        _ => throw new ArgumentOutOfRangeException(
            nameof(backend), backend, "The llama-server tier has cpu, vulkan and cuda drops; nothing else is vendored."),
    };
}
