using Parakeet.Core.Diarisation;
using Parakeet.Core.Models;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Transcription;
using Parakeet.Core.Tidying;
using Parakeet.Core.Translation;
using Parakeet.Engine.ParakeetCpp;
using Parakeet.Engine.ParakeetCpp.Interop;
using Parakeet.Engine.Python;
using Parakeet.Engine.LlamaServer;
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
/// seam the only way to test the application is to install the catalogue's full recogniser
/// weights — 1.34 GiB — in CI.
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
    /// True when the chosen diariser's batch size can be set.
    /// </summary>
    /// <remarks>
    /// Asked of the provider rather than decided in the view model, on the same reasoning as
    /// <see cref="SupportsSpeakerLabelling"/>: it is a fact about the catalogue entry and the model
    /// store, and a window that worked it out from an id would be a second place that answer lives.
    /// <para>
    /// <b>It distinguished the two diarisers until 2026-08-27 and now tracks availability alone.</b>
    /// The engine that refused the field — its batching was its exported graph's geometry, so a
    /// control left enabled for it would have turned every load into an error — is in
    /// <c>attic/sortformer/</c>. The remaining implementation is
    /// <see cref="SupportsSpeakerLabelling"/>, so false now means the model is not installed or
    /// there is no bundled Python. Kept as its own member because a second entry is the obvious next
    /// thing to want and it would need the distinction back.
    /// </para>
    /// </remarks>
    bool SupportsDiariserBatchSize { get; }

    /// <summary>
    /// True when the chosen diariser is the torch pipeline, whose execution-provider vocabulary is
    /// torch devices rather than ONNX Runtime providers.
    /// </summary>
    /// <remarks>
    /// <b>Unconditionally true since 2026-08-27, and kept rather than folded away.</b> It was added
    /// hours earlier because the two diarisers did not accept the same words: the ONNX graph took
    /// <c>cpu</c>, <c>cuda</c>, <c>webgpu</c> or <c>dml</c>, while the pyannote pipeline is torch on
    /// both stages with no ONNX route and <i>refuses</i> <c>webgpu</c> and <c>dml</c> outright
    /// rather than falling back — deliberately, because somebody who picked one and silently got the
    /// CPU has been told nothing. The graph is in <c>attic/sortformer/</c>, so only one vocabulary
    /// remains.
    /// <para>
    /// <b>What that costs is worth naming.</b> With this always true, the picker returns a fixed
    /// torch device list and never applies
    /// <see cref="AvailableDiariserProvidersAsync"/>'s answer — so a CUDA row is offered whether or
    /// not this machine's torch build has one, and the window no longer claims otherwise. Restoring
    /// a real availability filter means asking torch rather than ONNX Runtime, which nothing does.
    /// </para>
    /// </remarks>
    bool DiariserRunsInTorch { get; }

    /// <summary>
    /// The directory of the diarisation model an ask would load, or null when none is installed.
    /// </summary>
    /// <remarks>
    /// Exposed for one caller and one question: whether the exported ONNX graphs are beside those
    /// weights, which is what decides if the Settings window may offer a GPU row. The window is
    /// given the directory rather than the answer because the same directory is what an export
    /// would write into, and handing out two derived facts where one path serves would let them
    /// disagree.
    /// </remarks>
    string? DiarisationModelDirectory { get; }

    /// <summary>
    /// The execution providers this machine's ONNX Runtime registered, as protocol names, or null
    /// when that could not be established.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null and empty are different answers and the window depends on the difference.</b> Null
    /// is "not established" — no interpreter, a sidecar that would not start, a probe still running
    /// — and the caller keeps offering what it offered before, because a control emptied by a
    /// failed probe is worse than one that occasionally offers too much. Empty would mean the
    /// runtime registered nothing, which cannot happen: the CPU provider is always there.
    /// </para>
    /// <para>
    /// <b>Why this is asked at all.</b> The bundle pins <c>onnxruntime-webgpu</c>, whose wheel has
    /// no CUDA provider, so a CUDA row offered on the strength of an NVIDIA card would fail on every
    /// machine including one with the card. Which providers exist is a property of the installed
    /// runtime and not of the hardware, and only that runtime can answer it.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>?> AvailableDiariserProvidersAsync(CancellationToken ct = default);

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
    /// True when this provider can hand out a translator for what <paramref name="recogniser"/>
    /// writes. Shown the same way <see cref="SupportsSpeakerLabelling"/> is: the checkbox is
    /// disabled with a reason rather than hidden, because the reason — a model that has not been
    /// downloaded — is one the user can act on from the tab next door.
    /// </summary>
    /// <remarks>
    /// A question about the recogniser since 2026-09-04, when the catalogue gained a second
    /// translation entry: the European recogniser's transcripts go to the many-to-one checkpoint
    /// and the Japanese recogniser's to the Japanese one, chosen by
    /// <see cref="ModelCatalog.TranslationModelsFor"/>. Null is a recogniser nobody has chosen —
    /// the catalogue's recommendation stands in for it, exactly as it does for Start.
    /// </remarks>
    bool SupportsTranslation(ModelDescriptor? recogniser);

    /// <summary>
    /// Why the English opt-in is not available for <paramref name="recogniser"/>, or null when it
    /// is. The translation twin of <see cref="DescribeUnavailable"/>, separate because its answer
    /// depends on which recogniser is selected and the other tasks' do not.
    /// </summary>
    string? DescribeUnavailableTranslation(ModelDescriptor? recogniser);

    /// <summary>
    /// The translator behind the English opt-in for what <paramref name="recogniser"/> writes, or
    /// null when <see cref="SupportsTranslation"/> is false for it.
    /// </summary>
    ITranscriptTranslator? CreateTranslator(ModelDescriptor? recogniser);

    /// <summary>
    /// True when this provider can hand out a tidier: the tidying entry is installed and a
    /// <c>llama-server</c> drop is vendored. Shown the way the other opt-ins are — a checkbox
    /// disabled with a reason rather than hidden.
    /// </summary>
    bool SupportsTidying { get; }

    /// <summary>The tidier behind "Clean up the transcript", or null when <see cref="SupportsTidying"/> is false.</summary>
    ITranscriptTidier? CreateTidier();

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
        : this(store, python, null)
    {
    }

    /// <summary>
    /// The same, plus which diariser the user picked in the Models tab.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a stored id because the setting can change while the window is open —
    /// picking a diariser is one click on a tab the user is already looking at — and a value read
    /// once at construction would go stale exactly then. Null, and a delegate returning null, both
    /// mean "nobody has chosen"; see <see cref="AppSettings.DiarisationModelId"/>.
    /// </remarks>
    /// <remarks>
    /// <paramref name="diariserSettings"/> is a delegate for the same reason as
    /// <paramref name="preferredDiarisationModelId"/>: both live in the settings file, both can
    /// change while the window is open, and both are read at the moment a labeller is built rather
    /// than stored here. <c>(null, null)</c> is the shipped state — automatic provider, the model's
    /// own batch size — and is what a null delegate returns.
    /// </remarks>
    public EngineProvider(
        IModelStore? store,
        Func<(bool Found, string? Reason)>? python,
        Func<string?>? preferredDiarisationModelId,
        Func<(string? Provider, int? BatchSize)>? diariserSettings = null)
    {
        _store = store ?? new LocalModelStore();
        _python = new Lazy<(bool Found, string? Reason)>(python ?? (() =>
            PythonRuntime.TryResolve(out _, out var reason) ? (true, null) : (false, reason)));
        _preferredDiarisationModelId = preferredDiarisationModelId ?? (() => null);
        _diariserSettings = diariserSettings ?? (() => (null, null));
    }

    private readonly Func<string?> _preferredDiarisationModelId;

    private readonly Func<(string? Provider, int? BatchSize)> _diariserSettings;

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

    /// <summary>
    /// The diariser a run would use: the one the user picked when it is installed, otherwise the
    /// first installed entry, otherwise null.
    /// </summary>
    /// <remarks>
    /// <b>A picked entry that is not installed falls through rather than winning.</b> The id can
    /// outlive its files — removed from the Models tab, or a settings file carried to another
    /// machine — and honouring it then would turn speaker labelling off with no way for the user to
    /// see why. Falling through to whatever is installed keeps the feature working and leaves the
    /// stored choice intact for when its model comes back.
    /// </remarks>
    private ModelDescriptor? DiarisationModel
    {
        get
        {
            var installed = ModelCatalog.Default.DiarisationModels
                .Where(m => PathForInstalledOrBundled(m) is not null)
                .ToList();

            if (_preferredDiarisationModelId() is { Length: > 0 } preferred)
            {
                var chosen = installed.FirstOrDefault(
                    m => string.Equals(m.Id, preferred, StringComparison.OrdinalIgnoreCase));
                if (chosen is not null)
                {
                    return chosen;
                }
            }

            return installed.FirstOrDefault();
        }
    }

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

        var (provider, batchSize) = _diariserSettings();

        return new SidecarSpeakerLabeller(new SidecarLabellerOptions
        {
            // The same order as everywhere else: the user's own copy, then the installer's.
            ModelPath = PathForInstalledOrBundled(model)!,
            ModelId = model.Id,

            // The Settings tab's own control, not the Models tab's backend list. That one is
            // parakeet.cpp's — Vulkan, CUDA, CPU — and naming it here would offer the diariser
            // backends it has no path to; the two runtimes overlap only in the word "CPU". This
            // window passed a hardcoded `auto` until the separate setting existed.
            //
            // Null still means `auto`, which is the CPU.
            Provider = provider ?? "auto",

            // Null unless somebody chose, which means the pipeline's own value. The guard that
            // stood here — sending it only to the engine that accepted it — went with the engine
            // that refused it, whose batching was its exported graph's geometry.
            BatchSize = batchSize,
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
            // One entry does this job since the Sortformer entry went to attic/ on 2026-08-27, so
            // the sentence describes it. It is only ever read when it is not installed.
            ModelTask.Diarisation => (DiarisationModel is not null,
                "Speaker labelling needs its own model, which is not installed yet. Install it from the Models "
                + "tab: a 31 MiB download with no limit on the number of voices, which needs a free Hugging "
                + "Face account before it can be fetched."),
            // Translation is answered by DescribeUnavailableTranslation, because its answer depends
            // on which recogniser is selected and none of these do.
            ModelTask.VoiceActivity => (VoiceActivityModel is not null,
                "Neural speech detection needs its own model, which is not installed yet. Install it from the Models "
                + "tab; it is a 2.2 MiB download and hears pauses under music that the loudness gate cannot."),
            ModelTask.Tidying => (TidyingModel is not null,
                "Tidying up the transcript needs its own model, which is not installed yet. Install it from the "
                + "Models tab; it is a 3.9 GiB download and runs beside the speech recogniser."),
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
        // The tidy's second question is the language-model engine rather than the Python: the
        // model is served by the vendored llama-server, and a build without one has nothing to
        // run it in.
        if (task is ModelTask.Tidying && LlamaServerLocator.TryFind() is null)
        {
            return "The model is installed, but this build does not include the language-model engine that runs it.";
        }

        if (task is ModelTask.Diarisation && !HasBundledPython)
        {
            return DescribeMissingPython();
        }

        return null;
    }

    /// <summary>
    /// The sentence for a sidecar opt-in whose model is installed and whose interpreter is not.
    /// </summary>
    /// <remarks>
    /// Not a download, and not something the Models tab can fix. The resolver's own reason leads,
    /// because it names what was actually looked for — the two bundle directories, or an override
    /// that points at nothing — where a sentence written before it looked can only guess; until
    /// 2026-08-22 this guessed "reinstall", which is the wrong advice when UINDOSILL_PYTHON is set
    /// to a path that does not exist.
    /// </remarks>
    private string DescribeMissingPython()
    {
        var reason = _python.Value.Reason;
        return reason is { Length: > 0 }
            ? "The model is installed, but the Python this feature runs in was not found. " + reason
            : "The model is installed, but the Python this feature runs in is not beside the application. "
              + "It ships with uindosill rather than being something to install, so this copy is incomplete: "
              + "reinstalling is the repair.";
    }

    /// <inheritdoc />
    public string? DescribeLabeller(ISpeakerLabeller labeller)
    {
        ArgumentNullException.ThrowIfNull(labeller);

        // **Four sentences stood here and went with the diariser on 2026-08-27**, matching the
        // command line's: a fallback report, a measured warning about cuda and DirectML, one about
        // an ONNX speaker embedder, and a parity result. All four described the ONNX diariser now in
        // `attic/sortformer/`, and this pipeline is torch on both stages — nothing to fall back
        // from, no provider measured to move the answer, no second path to compare against.
        //
        // There is nothing left to say, and the window says nothing rather than saying that
        // nothing is wrong: no figure of any kind has been produced on this engine.
        // `docs/UNPROVEN.md` is where that is kept visible.
        return null;
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

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately not gated on <see cref="SupportsSpeakerLabelling"/>: that also asks whether the
    /// bundled Python is present, and a machine without it still has weights on disk whose graphs a
    /// window may want to describe. The one question here is where the model is.
    /// </remarks>
    public string? DiarisationModelDirectory =>
        DiarisationModel is { } model ? PathForInstalledOrBundled(model) : null;

    /// <inheritdoc />
    /// <remarks>
    /// <b>Answers about a runtime the diariser no longer uses, and is kept for one reason.</b> This
    /// probes ONNX Runtime for the providers it registered, so that the Settings tab could not offer
    /// a row that had no chance of working. The diariser is torch now; <see cref="DiariserRunsInTorch"/>
    /// is true, and the view model returns its fixed device list before this is consulted. It is
    /// still implemented rather than removed because the translator is still an ONNX graph and this
    /// is the only probe of what the bundle's wheel actually registered — but nothing on the
    /// diariser path reads it, and a caller should not infer from its existence that one does.
    /// </remarks>
    public Task<IReadOnlyList<string>?> AvailableDiariserProvidersAsync(CancellationToken ct = default) =>
        HasBundledPython
            ? SidecarExecutionProviders.QueryAsync(ct: ct)
            : Task.FromResult<IReadOnlyList<string>?>(null);

    /// <inheritdoc />
    /// <remarks>
    /// Unconditional since 2026-08-27: the batch size applied to one of two diarisers and now
    /// applies to the only one. Kept as a capability rather than folded away because the Settings
    /// tab asks it, and an engine that cannot take one is what it exists to answer for.
    /// </remarks>
    public bool SupportsDiariserBatchSize => SupportsSpeakerLabelling;

    /// <inheritdoc />
    /// <remarks>
    /// Unconditional for the same reason, and true rather than false: the ONNX diariser went to
    /// <c>attic/sortformer/</c> and what remains is torch on both stages.
    /// </remarks>
    public bool DiariserRunsInTorch => true;

    /// <summary>
    /// True when the translation checkpoint for what <paramref name="recogniser"/> writes is
    /// installed, and false with a reason when it is not.
    /// </summary>
    /// <remarks>
    /// A question about the files on disk, like <see cref="SupportsSpeakerLabelling"/> — and here
    /// it is nine of them rather than one, which is why it goes through the store's own
    /// <c>IsInstalled</c> rather than a <c>File.Exists</c>. A partial install is not installed:
    /// the graphs load out of an assembled directory and a set missing its tokenizer loads until
    /// it does not. And the same second question, for the same reason: the translator runs in the
    /// bundled Python too, so without one there is nothing behind the opt-in.
    /// </remarks>
    public bool SupportsTranslation(ModelDescriptor? recogniser) =>
        TranslationModelFor(recogniser, out _) is not null && HasBundledPython;

    /// <summary>
    /// The translation entry for <paramref name="recogniser"/>, installed — or null and the
    /// sentence that says why not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which entry is <see cref="ModelCatalog.TranslationModelsFor"/>'s answer, on the recogniser's
    /// declared languages; a recogniser nobody has chosen is the catalogue's recommendation, as it
    /// is for Start. One entry in the catalogue is the answer whoever the recogniser is, which is
    /// how it stood until 2026-09-04 and how a build with one entry still behaves.
    /// </para>
    /// <para>
    /// The reasons are the ones a user can act on, and each names the model it is about: "download
    /// the Japanese translator" is a different instruction from "download the European one", and a
    /// sentence that said "the translation model" would send half its readers to the wrong row.
    /// A recogniser that declares no languages — a sideloaded one — gets the one reason nobody can
    /// act on from the Models tab, said as such.
    /// </para>
    /// </remarks>
    private ModelDescriptor? TranslationModelFor(ModelDescriptor? recogniser, out string? whyNot)
    {
        var catalogue = ModelCatalog.Default;
        recogniser ??= catalogue.Recommended;

        ModelDescriptor? entry;
        if (catalogue.TranslationModels.Count <= 1)
        {
            entry = catalogue.TranslationModels.FirstOrDefault();
            if (entry is null)
            {
                whyNot = "This build's catalogue has no translation model.";
                return null;
            }
        }
        else if (recogniser is null || recogniser.Languages.Count == 0)
        {
            whyNot = "Which translator to use depends on the speech model, and the selected one does not say " +
                     "which languages it writes. Pick one of the catalogue's speech models on the Models tab.";
            return null;
        }
        else
        {
            var matching = catalogue.TranslationModelsFor(recogniser.Languages);
            if (matching.Count != 1)
            {
                whyNot = matching.Count == 0
                    ? $"No translation model reads what {recogniser.DisplayName} writes."
                    : $"{matching.Count} translation models read what {recogniser.DisplayName} writes, and nothing " +
                      "here chooses between them.";
                return null;
            }

            entry = matching[0];
        }

        if (!_store.IsInstalled(entry))
        {
            var size = entry.TotalSizeBytes is { } bytes ? $"a {ByteSize.Describe(bytes)} download" : "a download";
            whyNot = $"An English version needs the '{entry.DisplayName}' model, which is not installed yet. " +
                     $"Install it from the Models tab; it is {size} and reads " +
                     $"{DescribeLanguages(entry)} into English only.";
            return null;
        }

        whyNot = null;
        return entry;
    }

    private static string DescribeLanguages(ModelDescriptor entry) => entry.Languages.Count switch
    {
        0 => "its own languages",
        1 when string.Equals(entry.Languages[0], "ja", StringComparison.OrdinalIgnoreCase) => "Japanese",
        1 => entry.Languages[0],
        _ => $"{entry.Languages.Count} languages",
    };

    /// <inheritdoc />
    public string? DescribeUnavailableTranslation(ModelDescriptor? recogniser)
    {
        if (TranslationModelFor(recogniser, out var whyNot) is null)
        {
            return whyNot;
        }

        // The interpreter is the second question, on the terms DescribeUnavailable gives for the
        // speaker opt-in: not a download, and not something the Models tab can fix.
        return HasBundledPython ? null : DescribeMissingPython();
    }

    public ITranscriptTranslator? CreateTranslator(ModelDescriptor? recogniser)
    {
        // The same all-or-nothing question as above, asked again at the point of use: the Models
        // tab can remove the entry between the window opening and Start being pressed.
        if (TranslationModelFor(recogniser, out _) is not { } model || !HasBundledPython)
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

            // What the catalogue says the checkpoint reads in front of a source, held against what
            // the sidecar finds in its vocabulary at load.
            DeclaredTargetToken = model.TargetToken,
            HasDeclaredTargetToken = true,
        });
    }

    /// <summary>
    /// True when the tidying entry is installed and a llama-server drop is vendored to run it.
    /// </summary>
    /// <remarks>
    /// The same two-question shape as the sidecar opt-ins, with the engine in place of the
    /// interpreter: the model is a download the Models tab can make, and the drop is part of the
    /// build. Both are read at the moment they are asked, because the Models tab can add or remove
    /// the entry while the window is open.
    /// </remarks>
    public bool SupportsTidying => TidyingModel is not null && LlamaServerLocator.TryFind() is not null;

    private ModelDescriptor? TidyingModel =>
        ModelCatalog.Default.TidyingModels.FirstOrDefault() is { } model && _store.IsInstalled(model)
            ? model
            : null;

    public ITranscriptTidier? CreateTidier()
    {
        if (TidyingModel is not { } model || LlamaServerLocator.TryFind() is null)
        {
            return null;
        }

        // The weights, never the head: the engine pairs the head with the weights beside it.
        var weights = model.Files.FirstOrDefault(file => !DraftModelLocator.IsDraftHead(file.FileName));
        if (weights is null)
        {
            return null;
        }

        // The best vendored drop, as the Ask tab takes it, and the measured number of lines in
        // flight. The context, the slots and the head are decided in the one place the command
        // line uses too.
        return LlamaServerTranscriptTidier.Create(
            Path.Combine(_store.PathFor(model), weights.FileName),
            backend: null,
            serverRoot: null,
            TidyOptions.Default.Concurrency);
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
    private readonly FakeTidierOptions _tidier;

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
        FakeTranslatorOptions? translator = null,
        FakeTidierOptions? tidier = null)
    {
        _options = options ?? FakeEngineOptions.Default;
        _speakers = speakers ?? FakeSpeakerLabellerOptions.Default;
        _speakers.Validate();
        _translator = translator ?? FakeTranslatorOptions.Default;
        _tidier = tidier ?? FakeTidierOptions.Default;
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

    /// <summary>
    /// False: the canned labeller is neither diariser and has no pipeline to batch. A demo mode
    /// that offered the control would be offering a setting that reaches nothing.
    /// </summary>
    public bool SupportsDiariserBatchSize => false;

    /// <summary>
    /// True, matching the real provider rather than describing the canned labeller.
    /// </summary>
    /// <remarks>
    /// It was false while this meant "which of two diarisers is loaded" and the canned one was
    /// neither. It now means "what vocabulary does the speaker-provider picker speak", and a demo
    /// mode that answered false would offer rows the shipping product does not have. The canned
    /// labeller still runs on no runtime at all; that is simply not what this asks.
    /// </remarks>
    public bool DiariserRunsInTorch => true;

    /// <summary>Null — the canned provider has no model directory, so it has no graphs either.</summary>
    public string? DiarisationModelDirectory => null;

    /// <summary>
    /// Null — "not established". The canned engine runs on no execution provider at all, and
    /// answering with a list would put this provider in the business of describing a machine it
    /// never touches. Null is also what keeps the picker's full set of rows visible under a test.
    /// </summary>
    public Task<IReadOnlyList<string>?> AvailableDiariserProvidersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>?>(null);

    /// <summary>Nothing to say: the canned labeller runs nowhere and reproduces no published figure.</summary>
    public string? DescribeLabeller(ISpeakerLabeller labeller) => null;

    /// <summary>Nothing is ever unavailable here, which is what makes this provider useful in a test.</summary>
    public string? DescribeUnavailable(ModelTask task) => null;

    /// <summary>Nothing to say: the canned translator runs nowhere and reproduces no published figure.</summary>
    public string? DescribeTranslator(ITranscriptTranslator translator) => null;

    /// <summary>The canned translator, for the same reason: the English opt-in runs end to end
    /// here with no checkpoint in CI, whoever the recogniser is, and its output is visibly not
    /// English.</summary>
    public bool SupportsTranslation(ModelDescriptor? recogniser) => true;

    public string? DescribeUnavailableTranslation(ModelDescriptor? recogniser) => null;

    public ITranscriptTranslator? CreateTranslator(ModelDescriptor? recogniser) => new FakeTranscriptTranslator(_translator);

    /// <summary>The canned tidier, so the opt-in, the stage and the panes run end to end with no model in CI.</summary>
    public bool SupportsTidying => true;

    public ITranscriptTidier? CreateTidier() => new FakeTranscriptTidier(_tidier);

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
