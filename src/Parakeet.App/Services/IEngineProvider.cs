using Parakeet.Core.Diarisation;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp;
using Parakeet.Engine.ParakeetCpp.Interop;
using Parakeet.Engine.Sortformer;

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

    /// <summary>
    /// True when this provider can hand out a speaker labeller. False is a real answer, and the
    /// window shows it as one: the checkbox for the opt-in is disabled with a reason rather than
    /// hidden, because a setting that quietly does nothing is worse than one that says why not.
    /// </summary>
    bool SupportsSpeakerLabelling { get; }

    /// <summary>The labeller behind the speaker opt-in, or null when <see cref="SupportsSpeakerLabelling"/> is false.</summary>
    ISpeakerLabeller? CreateSpeakerLabeller();

    /// <summary>
    /// Frees whatever the engine technology keeps alive for the whole process, once every engine
    /// it created has been disposed. For parakeet.cpp that is the process-global compute backend,
    /// which on CUDA must be released while the driver is still up or the process aborts on exit
    /// (gotcha 19). Called by <see cref="ModelSession.DisposeAsync"/>, which is to say at shutdown.
    /// </summary>
    void ReleaseBackend();
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

        return selection.Model is { Task: ModelTask.Transcription } model && _store.IsInstalled(model);
    }

    public ITranscriptionEngine Create(EngineSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.Model is { } chosen && chosen.Task != ModelTask.Transcription)
        {
            throw new InvalidOperationException(
                $"'{chosen.Id}' is a {chosen.Task.ToString().ToLowerInvariant()} model and cannot be loaded as the transcription engine.");
        }

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

    /// <summary>
    /// True when the diarisation model is installed, and false with a reason when it is not.
    /// </summary>
    /// <remarks>
    /// Deliberately a question about the file on disk rather than about the build. The checkbox is
    /// disabled with a reason rather than hidden, and "download it" is a reason a user can act on,
    /// where "this build cannot" is not. It also means the box comes alive the moment the download
    /// finishes rather than at the next release.
    /// </remarks>
    public bool SupportsSpeakerLabelling =>
        ModelCatalog.Default.DiarisationModels.FirstOrDefault() is { } model && _store.IsInstalled(model);

    public ISpeakerLabeller? CreateSpeakerLabeller()
    {
        if (ModelCatalog.Default.DiarisationModels.FirstOrDefault() is not { } model)
        {
            return null;
        }

        var path = _store.PathFor(model);
        return File.Exists(path)
            ? new SortformerSpeakerLabeller(new SortformerLabellerOptions { ModelPath = path, ModelId = model.Id })
            : null;
    }

    /// <summary>
    /// A no-op until a model has been loaded in this process — the native library is not loaded
    /// before then, so there is nothing to release and nothing is touched.
    /// </summary>
    public void ReleaseBackend() => ParakeetNativeLibrary.TryShutdownBackend();
}

/// <summary>Hands out the canned engine. Used by tests and by the demo mode.</summary>
public sealed class FakeEngineProvider : IEngineProvider
{
    private readonly FakeEngineOptions _options;

    public FakeEngineProvider(FakeEngineOptions? options = null) => _options = options ?? FakeEngineOptions.Default;

    /// <summary>How many times the backend was released, so a test can see that shutdown got here.</summary>
    public int ReleaseCount { get; private set; }

    public bool IsModelAvailable(EngineSelection selection) => true;

    public ITranscriptionEngine Create(EngineSelection selection) => new FakeTranscriptionEngine(_options);

    /// <summary>The canned labeller, so the window's opt-in runs end to end in the headless tests.</summary>
    public bool SupportsSpeakerLabelling => true;

    public ISpeakerLabeller? CreateSpeakerLabeller() => new FakeSpeakerLabeller();

    public void ReleaseBackend() => ReleaseCount++;
}
