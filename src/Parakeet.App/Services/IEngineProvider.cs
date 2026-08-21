using Parakeet.Core.Diarisation;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;
using Parakeet.Engine.Marian;
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
    /// What the speaker opt-in's labeller can and cannot do, without loading it. Null when there is
    /// no labeller to describe.
    /// </summary>
    /// <remarks>
    /// Here rather than read off a created labeller because the window needs these answers while
    /// nothing is running: the cap and the length the labels are established to are what the
    /// speaker-count field and the long-recording warning are drawn from, and both have to be in
    /// front of a person <em>before</em> they spend twenty minutes on a batch. Capabilities are
    /// built in a labeller's constructor and cost nothing — <see cref="ISpeakerLabeller.LoadAsync"/>
    /// is where the weights come in — so this is a cheap question with an expensive answer to get
    /// any other way.
    /// </remarks>
    SpeakerLabellerCapabilities? SpeakerLimits { get; }

    /// <summary>
    /// True when this provider can hand out a translator. Shown the same way
    /// <see cref="SupportsSpeakerLabelling"/> is: the checkbox is disabled with a reason rather
    /// than hidden, because the reason — a model that has not been downloaded — is one the user
    /// can act on from the tab next door.
    /// </summary>
    bool SupportsTranslation { get; }

    /// <summary>The translator behind the English opt-in, or null when <see cref="SupportsTranslation"/> is false.</summary>
    ITranscriptTranslator? CreateTranslator();

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
    /// The limits of the labeller this provider would hand out, read off one that is built and
    /// never loaded.
    /// </summary>
    /// <remarks>
    /// Built rather than declared here on purpose. The cap is the model's geometry and the bound is
    /// where its evidence stops, and both are stated once, in the labeller that owns them — a copy
    /// in this file would be a second place for the fifty minutes to live and the one that goes
    /// stale when a measurement moves it. Constructing a labeller costs a record: the weights arrive
    /// in <see cref="ISpeakerLabeller.LoadAsync"/>, which is not called here, so the instance holds
    /// nothing to release and is left to the collector rather than disposed through a property that
    /// cannot await.
    /// </remarks>
    public SpeakerLabellerCapabilities? SpeakerLimits => CreateSpeakerLabeller()?.Capabilities;

    /// <summary>
    /// True when the translation checkpoint is installed, and false with a reason when it is not.
    /// </summary>
    /// <remarks>
    /// A question about the files on disk, like <see cref="SupportsSpeakerLabelling"/> — and here
    /// it is nine of them rather than one, which is why it goes through the store's own
    /// <c>IsInstalled</c> rather than a <c>File.Exists</c>. A partial install is not installed:
    /// the graphs load out of an assembled directory and a set missing its tokenizer loads until
    /// it does not.
    /// </remarks>
    public bool SupportsTranslation =>
        ModelCatalog.Default.TranslationModels.FirstOrDefault() is { } model && _store.IsInstalled(model);

    public ITranscriptTranslator? CreateTranslator()
    {
        if (ModelCatalog.Default.TranslationModels.FirstOrDefault() is not { } model)
        {
            return null;
        }

        // The same all-or-nothing question as above, asked again at the point of use: the Models
        // tab can remove the entry between the window opening and Start being pressed.
        if (!_store.IsInstalled(model))
        {
            return null;
        }

        return new MarianTranscriptTranslator(new MarianTranslatorOptions
        {
            ModelDirectory = _store.PathFor(model),
            ModelId = model.Id,
            SourceLanguages = model.Languages,
        });
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
    private readonly FakeSpeakerLabellerOptions _speakers;

    /// <summary>
    /// <paramref name="speakers"/> is how a test gives the canned labeller the shipping one's
    /// shape — a cap, a length its labels are established to, a count it cannot be told — which is
    /// what the window's speaker-count field and long-recording warning are drawn from. Its default
    /// has none of those, as this provider has always behaved.
    /// </summary>
    public FakeEngineProvider(
        FakeEngineOptions? options = null, FakeSpeakerLabellerOptions? speakers = null)
    {
        _options = options ?? FakeEngineOptions.Default;
        _speakers = speakers ?? FakeSpeakerLabellerOptions.Default;
        _speakers.Validate();
    }

    /// <summary>How many times the backend was released, so a test can see that shutdown got here.</summary>
    public int ReleaseCount { get; private set; }

    public bool IsModelAvailable(EngineSelection selection) => true;

    public ITranscriptionEngine Create(EngineSelection selection) => new FakeTranscriptionEngine(_options);

    /// <summary>The canned labeller, so the window's opt-in runs end to end in the headless tests.</summary>
    public bool SupportsSpeakerLabelling => true;

    public ISpeakerLabeller? CreateSpeakerLabeller() => new FakeSpeakerLabeller(_speakers);

    public SpeakerLabellerCapabilities? SpeakerLimits => CreateSpeakerLabeller()?.Capabilities;

    /// <summary>The canned translator, for the same reason: the English opt-in runs end to end
    /// here with no 1.34 GiB checkpoint in CI, and its output is visibly not English.</summary>
    public bool SupportsTranslation => true;

    public ITranscriptTranslator? CreateTranslator() => new FakeTranscriptTranslator();

    public void ReleaseBackend() => ReleaseCount++;
}
