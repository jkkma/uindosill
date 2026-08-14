using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp;

namespace Parakeet.App.Services;

public sealed record EngineSelection
{
    public ComputeBackend Backend { get; init; } = ComputeBackend.Vulkan;

    public ModelDescriptor? Model { get; init; }

    public string? ModelPath { get; init; }
}

/// <summary>
/// Creates the engine the window will drive.
/// </summary>
/// <remarks>
/// An interface rather than a constructor call so the headless UI tests exercise the real
/// window, the real job queue and the real formatters against the canned engine. Without this
/// seam the only way to test the application is to install 670 MB of weights in CI.
/// </remarks>
public interface IEngineProvider
{
    /// <summary>True when a real model is available to load.</summary>
    bool IsModelAvailable(EngineSelection selection);

    ITranscriptionEngine Create(EngineSelection selection);
}

public sealed class EngineProvider : IEngineProvider
{
    private readonly IModelStore _store;

    public EngineProvider(IModelStore? store = null) => _store = store ?? new LocalModelStore();

    public bool IsModelAvailable(EngineSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.ModelPath is { Length: > 0 } path)
        {
            return File.Exists(path);
        }

        return selection.Model is { } model && _store.IsInstalled(model);
    }

    public ITranscriptionEngine Create(EngineSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var path = selection.ModelPath is { Length: > 0 } explicitPath
            ? explicitPath
            : selection.Model is { } model
                ? _store.PathFor(model)
                : throw new InvalidOperationException("No model selected.");

        return new ParakeetCppEngine(new ParakeetCppOptions
        {
            ModelPath = path,
            Backend = selection.Backend,
            ModelId = selection.Model?.Id ?? Path.GetFileNameWithoutExtension(path),
            Quantisation = selection.Model?.Quantisation,
        });
    }
}

/// <summary>Hands out the canned engine. Used by tests and by the demo mode.</summary>
public sealed class FakeEngineProvider : IEngineProvider
{
    private readonly FakeEngineOptions _options;

    public FakeEngineProvider(FakeEngineOptions? options = null) => _options = options ?? FakeEngineOptions.Default;

    public bool IsModelAvailable(EngineSelection selection) => true;

    public ITranscriptionEngine Create(EngineSelection selection) => new FakeTranscriptionEngine(_options);
}
