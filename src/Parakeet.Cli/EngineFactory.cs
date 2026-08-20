using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp;
using Parakeet.Engine.ParakeetCpp.Interop;

namespace Parakeet.Cli;

internal sealed record EngineRequest
{
    public bool Fake { get; init; }

    public string? ModelId { get; init; }

    public string? ModelPath { get; init; }

    /// <summary>Defaults to the same tier a bare <c>--backend</c> resolves to, so the two cannot drift.</summary>
    public ComputeBackend Backend { get; init; } = EngineFactory.DefaultBackend();

    public string? NativeDirectory { get; init; }

    /// <summary>
    /// Set GGML_VK_DISABLE_BFLOAT16 before loading. Vulkan only, on by default; see
    /// ParakeetCppOptions and <see cref="EngineFactory.ParseVulkanBFloat16"/>.
    /// </summary>
    public bool DisableVulkanBFloat16 { get; init; } = true;

    public bool WarmUp { get; init; } = true;

    public int BatchSize { get; init; } = 4;
}

internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message)
        : base(message)
    {
    }

    public CliUsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CliUsageException()
    {
    }
}

internal static class EngineFactory
{
    public static ITranscriptionEngine Create(CliContext context, EngineRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Fake)
        {
            return new FakeTranscriptionEngine();
        }

        var (path, descriptor) = ResolveModel(context, request);

        return new ParakeetCppEngine(new ParakeetCppOptions
        {
            ModelPath = path,
            Backend = request.Backend,
            NativeDirectory = request.NativeDirectory,
            DisableVulkanBFloat16 = request.DisableVulkanBFloat16,
            WarmUp = request.WarmUp,
            BatchSize = request.BatchSize,
            ModelId = descriptor?.Id ?? Path.GetFileNameWithoutExtension(path),
            Quantisation = descriptor?.Quantisation,
        });
    }

    public static (string Path, ModelDescriptor? Descriptor) ResolveModel(CliContext context, EngineRequest request)
    {
        if (request.ModelPath is { Length: > 0 } explicitPath)
        {
            if (!File.Exists(explicitPath))
            {
                throw new CliUsageException($"Model file not found: {explicitPath}");
            }

            return (explicitPath, null);
        }

        var descriptor = request.ModelId is { Length: > 0 } id
            ? context.Catalog.TryGet(id, out var found)
                ? found
                : throw new CliUsageException(
                    $"Unknown model '{id}'. Run 'uindosill models list' to see the catalogue.")
            : context.Catalog.Recommended
                ?? throw new CliUsageException("The model catalogue has no transcription model.");

        if (descriptor.Task != ModelTask.Transcription)
        {
            throw new CliUsageException(
                $"'{descriptor.Id}' is a {descriptor.Task.ToString().ToLowerInvariant()} model, not a transcription model; " +
                "it cannot be loaded as the ASR engine. Run 'uindosill models list' to see which entries transcribe.");
        }

        var path = context.Store.PathFor(descriptor);
        if (!File.Exists(path))
        {
            throw new CliUsageException(
                $"Model '{descriptor.Id}' is not installed. Run 'uindosill models download {descriptor.Id}' first " +
                $"(it would be at {path}).");
        }

        return (path, descriptor);
    }

    /// <summary>
    /// Resolves the two Vulkan bf16 flags to the option. The workaround is on unless
    /// <c>--vk-bf16</c> asks for bf16 to be left enabled; <c>--vk-disable-bf16</c> is the default
    /// spelled out, kept so commands written before it became the default still run. Both at once
    /// is a contradiction, reported rather than resolved by precedence.
    /// </summary>
    public static bool ParseVulkanBFloat16(bool disableRequested, bool keepRequested)
    {
        if (disableRequested && keepRequested)
        {
            throw new CliUsageException("--vk-disable-bf16 and --vk-bf16 contradict each other.");
        }

        return !keepRequested;
    }

    /// <summary>
    /// The named backend, or — when nothing is named — the fastest tier whose binaries are on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default was an unconditional Vulkan until 2026-08-20, which meant a build carrying the
    /// CUDA drop ran at RTF 0.0110 where 0.0064 was available unless every invocation said
    /// <c>--backend cuda</c>. The <c>cuda</c> directory is what the second channel adds and the
    /// default channel omits, so its presence is a decision somebody already made — an 818 MB
    /// installer against 82 MB — rather than something to make them repeat per command.
    /// </para>
    /// <para>
    /// This changes what a script that omits <c>--backend</c> runs on, which is why the loaded
    /// backend is now reported whenever it is not the one asked for. <c>--backend vulkan</c> pins
    /// the old behaviour exactly.
    /// </para>
    /// </remarks>
    public static ComputeBackend ParseBackend(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultBackend();
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "cpu" => ComputeBackend.Cpu,
            "vulkan" or "vk" => ComputeBackend.Vulkan,
            "cuda" or "nvidia" => ComputeBackend.Cuda,
            _ => throw new CliUsageException($"Unknown backend '{value}'. Choose cpu, vulkan or cuda."),
        };
    }

    /// <summary>
    /// What <c>--backend</c> resolves to when it is not given. Shares its rule with the window's
    /// first-run default so one install does not disagree with itself about which tier it runs.
    /// </summary>
    public static ComputeBackend DefaultBackend() => ParakeetNativeLibrary.PreferredBackendOnDisk();
}
