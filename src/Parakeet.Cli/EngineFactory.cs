using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp;

namespace Parakeet.Cli;

internal sealed record EngineRequest
{
    public bool Fake { get; init; }

    public string? ModelId { get; init; }

    public string? ModelPath { get; init; }

    public ComputeBackend Backend { get; init; } = ComputeBackend.Vulkan;

    public string? NativeDirectory { get; init; }

    /// <summary>Set GGML_VK_DISABLE_BFLOAT16 before loading. Vulkan only; see ParakeetCppOptions.</summary>
    public bool DisableVulkanBFloat16 { get; init; }

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
                ?? throw new CliUsageException("The model catalogue is empty.");

        var path = context.Store.PathFor(descriptor);
        if (!File.Exists(path))
        {
            throw new CliUsageException(
                $"Model '{descriptor.Id}' is not installed. Run 'uindosill models download {descriptor.Id}' first " +
                $"(it would be at {path}).");
        }

        return (path, descriptor);
    }

    public static ComputeBackend ParseBackend(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ComputeBackend.Vulkan;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "cpu" => ComputeBackend.Cpu,
            "vulkan" or "vk" => ComputeBackend.Vulkan,
            "cuda" or "nvidia" => ComputeBackend.Cuda,
            _ => throw new CliUsageException($"Unknown backend '{value}'. Choose cpu, vulkan or cuda."),
        };
    }
}
