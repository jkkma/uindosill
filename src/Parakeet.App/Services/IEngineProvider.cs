using Parakeet.Core.Diarisation;
using Parakeet.Core.Models;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;
using Parakeet.Engine.ParakeetCpp;
using Parakeet.Engine.ParakeetCpp.Interop;
using Parakeet.Engine.Python;
using Parakeet.Engine.SileroVad;

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
    /// <para>
    /// The window needs these answers while nothing is running: the cap and the length the labels
    /// are established to are what the speaker-count field and the long-recording warning are drawn
    /// from, and both have to be in front of a person <em>before</em> they spend twenty minutes on
    /// a batch.
    /// </para>
    /// <para>
    /// <b>It is a declaration now, not a read off a constructed labeller.</b> That changed on
    /// 2026-08-21 when the diariser moved into the bundled Python: building one starts nothing, but
    /// its capabilities are the sidecar's to report and it has not been asked yet — and on a machine
    /// with no bundled Python there is nothing to ask at all. So the type is
    /// <see cref="SpeakerLabellerLimits"/>, which has no backend field, rather than the full
    /// capabilities record whose backend would have had to be invented.
    /// </para>
    /// </remarks>
    SpeakerLabellerLimits? SpeakerLimits { get; }

    /// <summary>
    /// What this run's labeller means for the numbers this project publishes, or null when there is
    /// nothing to say. The window's half of what <c>LabellerFactory</c> prints on the command line.
    /// </summary>
    /// <remarks>
    /// On the interface rather than computed in the view model because answering it needs to know
    /// what kind of labeller this is — whether it ran a parity check and what it found — and the
    /// view model is the one part of the application not allowed to know that. Read after
    /// <see cref="ISpeakerLabeller.LoadAsync"/>: before it there is nothing to report, because the
    /// backend is chosen inside the sidecar.
    /// </remarks>
    string? DescribeLabeller(ISpeakerLabeller labeller);

    /// <summary>
    /// The same, for the translator: what this run's provider means for the English, or null when
    /// there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Its own member rather than an overload on a shared name, because the two engines fail in
    /// unlike ways and the sentences are not interchangeable — a diariser that diverges shifts a
    /// diarisation error rate, and a translator that diverges returns different sentences. Read
    /// after the pass, on the same terms as its sibling: the provider is chosen inside the sidecar.
    /// </remarks>
    string? DescribeTranslator(ITranscriptTranslator translator);

    /// <summary>
    /// Why an opt-in is not available, or null when it is. Both engines run in the bundled Python,
    /// so both can be unavailable for either of two reasons and a user is owed the one that is
    /// true.
    /// </summary>
    /// <remarks>
    /// Asked of the provider rather than assembled in the view model, because the two reasons are
    /// facts about this installation — a model that has not been downloaded, an interpreter that is
    /// not beside the application — and the view model cannot see either. Before 2026-08-21 there
    /// was only one reason and the sentence could be a constant; there are two now, and a constant
    /// would tell half the people who read it to fix something that is not broken.
    /// </remarks>
    string? DescribeUnavailable(ModelTask task);

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
    /// True when this provider can hand out a neural speech detector for the segmenter. Shown the
    /// same way the other two opt-ins are: a checkbox disabled with a reason rather than hidden.
    /// One reason only — the model is a download — because the detector runs in this process and
    /// needs no interpreter.
    /// </summary>
    bool SupportsNeuralSpeechDetection { get; }

    /// <summary>
    /// The detector behind the speech-detection box, loaded, or null when
    /// <see cref="SupportsNeuralSpeechDetection"/> is false. Throws <see cref="SpeechDetectorException"/>
    /// when the model is on disk and will not load, which is a sentence for the status line rather
    /// than a silent fall-back to the gate.
    /// </summary>
    ISpeechDetector? CreateSpeechDetector();

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
    private readonly Lazy<(bool Found, string? Reason)> _python;

    /// <summary>
    /// <paramref name="hasBundledPython"/> answers "is there an interpreter to run the engines in",
    /// and exists so the tests can answer it without one.
    /// </summary>
    /// <remarks>
    /// The default asks <see cref="PythonRuntime"/>, which looks beside the application — so in a
    /// test run it looks beside the test host and finds nothing, which would turn both opt-ins off
    /// and make every test about them a test about a missing interpreter instead. Injected rather
    /// than probed through an environment variable because two tests setting one variable is a race,
    /// and because a test that has to arrange a real directory tree to assert a checkbox is a test
    /// nobody will keep.
    /// </remarks>
    public EngineProvider(IModelStore? store = null, Func<bool>? hasBundledPython = null)
        : this(store, hasBundledPython is null ? null : () => (hasBundledPython(), (string?)null))
    {
    }

    /// <summary>
    /// The same, with the reason beside the answer — what <see cref="PythonRuntime.TryResolve"/>
    /// gives and what <see cref="DescribeUnavailable"/> reads, so that a window whose interpreter
    /// is missing says what the resolver found rather than a sentence written before it looked.
    /// </summary>
    public EngineProvider(IModelStore? store, Func<(bool Found, string? Reason)>? python)
    {
        _store = store ?? new LocalModelStore();
        _python = new Lazy<(bool Found, string? Reason)>(python ?? (() =>
            PythonRuntime.TryResolve(out _, out var reason) ? (true, null) : (false, reason)));
    }

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
    /// <para>
    /// Two questions, both about this machine rather than about the build. The checkbox is disabled
    /// with a reason rather than hidden, and both reasons are ones a user can act on: "download it"
    /// from the tab next door, and "reinstall" for the other. It also means the box comes alive the
    /// moment a download finishes rather than at the next release.
    /// </para>
    /// <para>
    /// The interpreter is the second question and it arrived on 2026-08-21 with the sidecar. Without
    /// it the opt-in has nothing behind it, and offering it anyway would mean a batch that fails at
    /// Start rather than a checkbox that says why not.
    /// </para>
    /// </remarks>
    public bool SupportsSpeakerLabelling => DiarisationModel is not null && HasBundledPython;

    /// <summary>
    /// Whether there is an interpreter to run the engines in, asked once.
    /// </summary>
    /// <remarks>
    /// Cached because, unlike a model, a bundled Python cannot appear while the window is open —
    /// there is no button in the Models tab that installs one — and this is read from binding
    /// getters that run on every keystroke in the speaker-count field.
    /// </remarks>
    private bool HasBundledPython => _python.Value.Found;

    private ModelDescriptor? DiarisationModel =>
        ModelCatalog.Default.DiarisationModels.FirstOrDefault() is { } model
        && PathForInstalledOrBundled(model) is not null
            ? model
            : null;

    /// <summary>
    /// True when the speech-detection graph is there to be loaded. One question only, unlike the
    /// two sidecar opt-ins: the detector runs in this process on ONNX Runtime, so there is no
    /// interpreter to be missing.
    /// </summary>
    public bool SupportsNeuralSpeechDetection => VoiceActivityModel is not null;

    private ModelDescriptor? VoiceActivityModel =>
        ModelCatalog.Default.VoiceActivityModels.FirstOrDefault() is { } model
        && PathForInstalledOrBundled(model) is not null
            ? model
            : null;

    /// <summary>
    /// Where a model's weights actually are: the user's own copy first, the copy the installer
    /// carried second, and null when neither exists.
    /// </summary>
    /// <remarks>
    /// The order is the decision. A downloaded copy wins because the user chose to download it and
    /// because it is the copy the Models tab updates and removes; the bundled one is the floor
    /// under a fresh install, so the speech and speaker opt-ins are live the moment the application
    /// first opens rather than after a visit to a tab. See <see cref="BundledModels"/> for which
    /// entries travel and why the other two cannot.
    /// </remarks>
    internal string? PathForInstalledOrBundled(ModelDescriptor model) =>
        _store.IsInstalled(model) ? _store.PathFor(model) : BundledModels.PathFor(model);

    /// <inheritdoc />
    public ISpeechDetector? CreateSpeechDetector() =>
        VoiceActivityModel is { } model && PathForInstalledOrBundled(model) is { } path
            ? new SileroSpeechDetector(path)
            : null;

    public ISpeakerLabeller? CreateSpeakerLabeller()
    {
        if (DiarisationModel is not { } model || !HasBundledPython)
        {
            return null;
        }

        return new SidecarSpeakerLabeller(new SidecarLabellerOptions
        {
            // The same order as everywhere else: the user's own copy, then the installer's.
            ModelPath = PathForInstalledOrBundled(model)!,
            ModelId = model.Id,

            // Chosen inside the sidecar, and no picker for it. The Models tab's backend list is
            // parakeet.cpp's — Vulkan, CUDA, CPU — and has no WebGPU entry, so binding the diariser
            // to it would offer backends it does not have and hide the one it wants; dml is refused
            // outright on the command line, which leaves a picker with nothing to say. This is a
            // design choice rather than a measurement.
            Provider = "auto",
        });
    }

    /// <inheritdoc />
    public string? DescribeTranslator(ITranscriptTranslator translator)
    {
        ArgumentNullException.ThrowIfNull(translator);

        // The window had been running this check and throwing the answer away, which is worse than
        // not running it: the cost was paid and the one thing it buys — a user knowing their English
        // is not the English any published figure describes — was not delivered. The command line
        // has said it since the flag existed.
        if (translator is not SidecarTranscriptTranslator sidecar)
        {
            return null;
        }

        // Why this backend, when `auto` wanted another: the reason the sidecar kept, which until
        // 2026-08-22 it kept only for the case where every candidate failed.
        var fellBack = sidecar.FellBackFrom.Count > 0
            ? $"The translator's preferred backends did not build on this machine ({string.Join("; ", sidecar.FellBackFrom)}), " +
              $"so this run used {translator.Capabilities.Backend.ToString().ToLowerInvariant()}."
            : null;

        // The result describes its own three failing shapes — including the check that could not
        // run, which used to be reported as nothing — and this adds what the English is.
        var parity = sidecar.Parity?.Describe() is { } finding
            ? finding + " The English shown may differ from what another computer would produce for the same recording."
            : null;

        return Join(fellBack, parity);
    }

    /// <summary>The sentences that have something to say, space-separated, or null when none does.</summary>
    private static string? Join(params string?[] sentences)
    {
        var said = string.Join(" ", sentences.Where(sentence => sentence is { Length: > 0 }));
        return said.Length > 0 ? said : null;
    }

    /// <inheritdoc />
    public string? DescribeUnavailable(ModelTask task)
    {
        (bool Installed, string? MissingModel) resolved = task switch
        {
            ModelTask.Diarisation => (DiarisationModel is not null,
                "Speaker labelling needs its own model, which is not installed yet. Install it from the Models tab; "
                + "it is a 453 MiB download and tells apart up to four speakers."),
            ModelTask.Translation => (TranslationModel is not null,
                "An English version needs its own model, which is not installed yet. Install it from the Models tab; "
                + "it is a 1.34 GiB download and reads 25 languages into English only."),
            ModelTask.VoiceActivity => (VoiceActivityModel is not null,
                "Neural speech detection needs its own model, which is not installed yet. Install it from the Models "
                + "tab; it is a 2.2 MiB download and hears pauses under music that the loudness gate cannot."),
            // Not every task runs in the sidecar — transcription is parakeet.cpp — so an unhandled
            // one returns nothing rather than falling through to a sentence about a Python it does
            // not use, which would tell somebody to reinstall over a feature that is working.
            _ => (true, null),
        };

        var (installed, missingModel) = resolved;
        if (missingModel is null)
        {
            return null;
        }

        if (!installed)
        {
            return missingModel;
        }

        // The interpreter is the second question for the two sidecar opt-ins and for nothing else:
        // the speech detector runs in this process, so a missing Python is not its problem and a
        // sentence about one would send somebody to repair a thing that is not broken.
        if (task is ModelTask.Diarisation or ModelTask.Translation && !HasBundledPython)
        {
            // Not a download, and not something the Models tab can fix. The resolver's own reason
            // leads, because it names what was actually looked for — the two bundle directories,
            // or an override that points at nothing — where a sentence written before it looked
            // can only guess; until 2026-08-22 this guessed "reinstall", which is the wrong advice
            // when UINDOSILL_PYTHON is set to a path that does not exist.
            var reason = _python.Value.Reason;
            return reason is { Length: > 0 }
                ? "The model is installed, but the Python this feature runs in was not found. " + reason
                : "The model is installed, but the Python this feature runs in is not beside the application. "
                  + "It ships with uindosill rather than being something to install, so this copy is incomplete: "
                  + "reinstalling is the repair.";
        }

        return null;
    }

    /// <inheritdoc />
    public string? DescribeLabeller(ISpeakerLabeller labeller)
    {
        ArgumentNullException.ThrowIfNull(labeller);

        var sidecar = labeller as SidecarSpeakerLabeller;

        // Why this backend, when `auto` wanted another — first, because it is the sentence that
        // explains the speed of the run, and until 2026-08-22 the sidecar kept the reason only for
        // the case where every candidate failed.
        var fellBack = sidecar is { FellBackFrom.Count: > 0 }
            ? $"The diariser's preferred backends did not build on this machine ({string.Join("; ", sidecar.FellBackFrom)}), " +
              $"so this run used {labeller.Capabilities.Backend.ToString().ToLowerInvariant()}."
            : null;
        var backend = SpeakerLabelling.DescribeBackend(labeller.Capabilities.Backend);

        // The result describes its own three failing shapes, including the check that could not
        // run — which used to be reported as nothing.
        var parity = sidecar?.Parity?.Describe();

        // This window chooses the backend itself, so none of these gets the command line's
        // "use --speaker-backend cpu" remedy: there is no flag here to follow the advice with.
        return Join(fellBack, backend, parity);
    }

    /// <summary>
    /// The limits of the labeller this provider would hand out, declared rather than read off one.
    /// </summary>
    /// <remarks>
    /// Declared because there may be nothing to read: since the diariser moved into the bundled
    /// Python its capabilities are the sidecar's to report, and on a machine with no interpreter
    /// there is no sidecar to ask. The numbers themselves are not stated here — they are
    /// <see cref="SidecarSpeakerLabeller.DeclaredLimits"/>, which is the engine's own copy and is
    /// checked against the sidecar's every time one loads. Only the name comes from here, and it
    /// comes from the catalogue, so it is not a second copy of anything.
    /// </remarks>
    public SpeakerLabellerLimits? SpeakerLimits =>
        SupportsSpeakerLabelling && DiarisationModel is { } model
            ? SidecarSpeakerLabeller.DeclaredLimits with { Name = model.Id }
            : null;

    /// <summary>
    /// True when the translation checkpoint is installed, and false with a reason when it is not.
    /// </summary>
    /// <remarks>
    /// A question about the files on disk, like <see cref="SupportsSpeakerLabelling"/> — and here
    /// it is nine of them rather than one, which is why it goes through the store's own
    /// <c>IsInstalled</c> rather than a <c>File.Exists</c>. A partial install is not installed:
    /// the graphs load out of an assembled directory and a set missing its tokenizer loads until
    /// it does not. And the same second question, for the same reason: the translator runs in the
    /// bundled Python too, so without one there is nothing behind the opt-in.
    /// </remarks>
    public bool SupportsTranslation => TranslationModel is not null && HasBundledPython;

    private ModelDescriptor? TranslationModel =>
        ModelCatalog.Default.TranslationModels.FirstOrDefault() is { } model && _store.IsInstalled(model)
            ? model
            : null;

    public ITranscriptTranslator? CreateTranslator()
    {
        // The same all-or-nothing question as above, asked again at the point of use: the Models
        // tab can remove the entry between the window opening and Start being pressed.
        if (TranslationModel is not { } model || !HasBundledPython)
        {
            return null;
        }

        return new SidecarTranscriptTranslator(new SidecarTranslatorOptions
        {
            ModelDirectory = _store.PathFor(model),
            ModelId = model.Id,

            // Resolved inside the sidecar, and no picker, for the reason CreateSpeakerLabeller
            // gives — with the addition that DirectML here is not merely unproven but measured
            // wrong, on 0 of 32 sentences.
            Provider = "auto",
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
    private readonly FakeTranslatorOptions _translator;

    /// <summary>
    /// <paramref name="speakers"/> is how a test gives the canned labeller the shipping one's
    /// shape — a cap, a length its labels are established to, a count it cannot be told — which is
    /// what the window's speaker-count field and long-recording warning are drawn from. Its default
    /// has none of those, as this provider has always behaved. <paramref name="translator"/> is the
    /// same handle on the canned translator, and exists so a test can make a pass fail.
    /// </summary>
    public FakeEngineProvider(
        FakeEngineOptions? options = null,
        FakeSpeakerLabellerOptions? speakers = null,
        FakeTranslatorOptions? translator = null)
    {
        _options = options ?? FakeEngineOptions.Default;
        _speakers = speakers ?? FakeSpeakerLabellerOptions.Default;
        _speakers.Validate();
        _translator = translator ?? FakeTranslatorOptions.Default;
    }

    /// <summary>How many times the backend was released, so a test can see that shutdown got here.</summary>
    public int ReleaseCount { get; private set; }

    /// <summary>
    /// What <see cref="IsModelAvailable"/> answers. True, as this provider has always behaved.
    /// </summary>
    /// <remarks>
    /// Settable rather than fixed at construction because the interesting case moves during a test:
    /// since Start loads for itself, "no weights on disk" is the only thing that still refuses it,
    /// and a test needs to be able to put the window into that state and then out of it — which is
    /// what installing a model from the tab next door does.
    /// </remarks>
    public bool ModelAvailable { get; set; } = true;

    public bool IsModelAvailable(EngineSelection selection) => ModelAvailable;

    public ITranscriptionEngine Create(EngineSelection selection) => new FakeTranscriptionEngine(_options);

    /// <summary>The canned labeller, so the window's opt-in runs end to end in the headless tests.</summary>
    public bool SupportsSpeakerLabelling => true;

    public ISpeakerLabeller? CreateSpeakerLabeller() => new FakeSpeakerLabeller(_speakers);

    public SpeakerLabellerLimits? SpeakerLimits => CreateSpeakerLabeller()?.Capabilities.Limits;

    /// <summary>Nothing to say: the canned labeller runs nowhere and reproduces no published figure.</summary>
    public string? DescribeLabeller(ISpeakerLabeller labeller) => null;

    /// <summary>Nothing is ever unavailable here, which is what makes this provider useful in a test.</summary>
    public string? DescribeUnavailable(ModelTask task) => null;

    /// <summary>Nothing to say: the canned translator runs nowhere and reproduces no published figure.</summary>
    public string? DescribeTranslator(ITranscriptTranslator translator) => null;

    /// <summary>The canned translator, for the same reason: the English opt-in runs end to end
    /// here with no 1.34 GiB checkpoint in CI, and its output is visibly not English.</summary>
    public bool SupportsTranslation => true;

    public ITranscriptTranslator? CreateTranslator() => new FakeTranscriptTranslator(_translator);

    /// <summary>
    /// The canned detector, so the speech-detection box runs end to end here with no graph in CI
    /// — and, since the box is ticked by default, so that every batch in the suite runs the way a
    /// user's does, with a detector handed to the engine. Its loudness rule behaves like the gate,
    /// so the box changes nothing about what the fake pipeline produces — what a test reads is
    /// <see cref="LastSpeechDetector"/>, which says whether the window handed the engine a
    /// detector at all and what the engine did with it.
    /// </summary>
    public bool SupportsNeuralSpeechDetection => true;

    /// <summary>The detector the last <see cref="CreateSpeechDetector"/> returned, or null before any.</summary>
    public FakeSpeechDetector? LastSpeechDetector { get; private set; }

    public ISpeechDetector? CreateSpeechDetector() => LastSpeechDetector = new FakeSpeechDetector();

    public void ReleaseBackend() => ReleaseCount++;
}
