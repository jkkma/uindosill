using CommunityToolkit.Mvvm.ComponentModel;
using Parakeet.App.Services;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.LlamaServer;
using Parakeet.Engine.ParakeetCpp.Interop;
using Parakeet.Engine.Python;

namespace Parakeet.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppSettingsStore _settings;

    /// <summary>The real provider when this window built one, so the model picker can list what
    /// is on disk. Null under the fake provider the tests supply, and the picker is then empty
    /// but for its automatic row.</summary>
    private readonly LlamaAnswerEngineProvider? _llamaAnswerEngines;

    /// <summary>Asked which diariser is chosen, for the settings that apply to only one of them.</summary>
    private readonly IEngineProvider _engines;

    /// <summary>
    /// Builds the thing that derives the diariser's ONNX graphs. A factory rather than an instance
    /// because each export runs its own short-lived sidecar, and a seam rather than a `new` because
    /// the headless tests exercise this path and must not start a Python.
    /// </summary>
    private readonly Func<IDiariserGraphExporter> _graphExporter;

    [ObservableProperty]
    private ComputeBackend _backend = ComputeBackend.Vulkan;

    /// <summary>The Settings toggle: the ask model thinks before answering. Off as shipped —
    /// the measured cost on integrated graphics is minutes per question.</summary>
    [ObservableProperty]
    private bool _askThinking;

    /// <summary>The Settings picker: where answers are drawn from, or that the question decides.
    /// Automatic as shipped — the register's decision 3 router.</summary>
    [ObservableProperty]
    private AskModePreference _askMode;

    /// <summary>The Settings picker: how much evidence an answer is built from. Thorough as
    /// shipped — see <see cref="AskEvidenceDepth"/> for the measured trade.</summary>
    [ObservableProperty]
    private AskEvidenceDepth _askEvidence;

    /// <summary>The .gguf chosen for asking, or null for "the largest one there".</summary>
    [ObservableProperty]
    private string? _askModelFileName;

    /// <summary>
    /// The Hugging Face access token, for the one model whose repository is gated, or null when
    /// nobody has supplied one.
    /// </summary>
    /// <remarks>
    /// <b>This is a credential in a settings file, so the box that edits it says so rather than
    /// looking like any other field.</b> It is stored as written — see
    /// <see cref="AppSettings.HuggingFaceToken"/> for why nothing here pretends to encrypt it — and
    /// it is sent to <c>huggingface.co</c> and to nowhere else. <c>HF_TOKEN</c> in the environment
    /// wins over whatever is stored here, so a machine already set up for the hub needs this box at
    /// all only if it is not.
    /// </remarks>
    [ObservableProperty]
    private string? _huggingFaceToken;

    /// <summary>The Settings picker: where a mixture's expert layers run. Automatic as shipped —
    /// the Vulkan loader is asked which kind of graphics this is.</summary>
    [ObservableProperty]
    private MoeExpertPlacement _askExpertPlacement;

    /// <summary>The Settings picker: which execution provider labels speakers, or null for
    /// automatic. Separate from <see cref="Backend"/>, which is the recogniser's and whose
    /// backends are a different runtime's.</summary>
    [ObservableProperty]
    private string? _diarisationProvider;

    /// <summary>The Settings picker: how much audio the second diariser holds at once, or null
    /// for the model's own value. A memory setting; see <see cref="AppSettings.DiarisationBatchSize"/>
    /// for why it is not a speed one.</summary>
    [ObservableProperty]
    private int? _diarisationBatchSize;

    [ObservableProperty]
    private int _selectedTab;

    public MainWindowViewModel(
        IEngineProvider engines,
        IModelStore? store = null,
        ModelCatalog? catalog = null,
        IAppUpdater? updater = null,
        AppSettingsStore? settings = null,
        Func<IReadOnlyList<ComputeBackend>>? backendsOnDisk = null,
        IMediaPlayer? player = null,
        Parakeet.App.Services.Tools.IMediaUrlFetcher? fetcher = null,
        string? downloadRoot = null,
        IAnswerEngineProvider? answerEngines = null,
        Func<IDiariserGraphExporter>? graphExporter = null)
    {
        ArgumentNullException.ThrowIfNull(engines);

        _graphExporter = graphExporter
            ?? (static () => new SidecarDiariserGraphExporterAdapter());

        var modelStore = store ?? new LocalModelStore();
        var modelCatalog = catalog ?? ModelCatalog.Default;
        _settings = settings ?? new AppSettingsStore();

        // The field, not the property: assigning the property here would fire OnBackendChanged and
        // write the file on every launch, including the launches where nothing was chosen.
        var loaded = _settings.Load();
        _backend = loaded.Backend
            ?? ParakeetNativeLibrary.PreferredBackend(
                backendsOnDisk?.Invoke() ?? ParakeetNativeLibrary.BackendsPresentOnDisk());
        _askThinking = loaded.AskThinking;
        _askMode = loaded.AskMode;
        _askEvidence = loaded.AskEvidence;
        _askModelFileName = loaded.AskModelFileName;
        _huggingFaceToken = loaded.HuggingFaceToken;
        _askExpertPlacement = loaded.AskExpertPlacement;
        _diarisationProvider = loaded.DiarisationProvider;
        _diarisationBatchSize = loaded.DiarisationBatchSize;

        _engines = engines;

        Session = new ModelSession(engines);

        Models = new ModelsViewModel(modelStore, modelCatalog, session: Session, backend: () => Backend);
        Transcribe = new TranscribeViewModel(
            engines,
            () => new EngineSelection
            {
                Backend = Backend,

                // The highlighted row only when it is one that can transcribe, and the catalogue's
                // recommendation otherwise. The Models list holds the diariser and the translator
                // too, and highlighting one of those to read its licence used to make this
                // selection name a model Start cannot run — which mattered the moment Start began
                // loading for itself rather than refusing.
                Model = Models.SelectedDescriptor is { Task: ModelTask.Transcription } chosen
                    ? chosen
                    : modelCatalog.Recommended,
            },
            Session,
            fetcher,
            downloadRoot);

        // The queue is handed over rather than copied: the Ask tab plays and reads the same rows
        // the Transcribe tab is filling, so a transcript that finishes while this tab is open
        // fills in where it stands. Two collections would need reconciling, and getting that
        // wrong shows a transcript beside the wrong recording. The session goes with it for R9 —
        // the chat's first question unloads the transcription model through it — and the
        // IsRunning probe, together with the session's own busy flag for the stretch a load
        // spends inside its await, is what keeps the two model loads from overlapping.
        // Kept as well as handed over, so the model picker can ask it what is on the disk. The
        // tests supply their own provider and leave this null; the picker is then just its
        // automatic row, which is the honest thing to show when nothing can enumerate a folder.
        if (answerEngines is null)
        {
            _llamaAnswerEngines = new LlamaAnswerEngineProvider(
                modelStore,
                () => AskThinking,
                () => AskMode,
                () => AskModelFileName,
                () => AskExpertPlacement,
                () => AppSettings.WindowsFor(AskEvidence));
        }

        Ask = new AskViewModel(
            Transcribe.Jobs,
            player ?? MediaPlayers.ForThisBuild(),
            Session,
            answerEngines ?? _llamaAnswerEngines,
            () => Transcribe.IsRunning);

        // The output folder outlives the run — chosen once, restored at every launch — but only
        // while the directory is really there: the folder people choose is often a removable
        // drive, and a restored path with nothing behind it would aim every export at a location
        // that cannot take one. Missing means the box goes blank AND the file forgets it, so the
        // stale choice cannot come back by itself a week later.
        if (_settings.Load().OutputDirectory is { Length: > 0 } savedOutput)
        {
            if (Directory.Exists(savedOutput))
            {
                Transcribe.OutputDirectory = savedOutput;
            }
            else
            {
                _settings.Update(current => current with { OutputDirectory = null });
            }
        }

        // Saved as it changes rather than at exit, because this application has a documented way
        // of dying abruptly (gotcha 19) and a setting saved on close is lost exactly then.
        Transcribe.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscribeViewModel.OutputDirectory))
            {
                var chosen = Transcribe.OutputDirectory;
                _settings.Update(current => current with
                {
                    OutputDirectory = string.IsNullOrWhiteSpace(chosen) ? null : chosen,
                });
            }
        };

        // Load and unload have to be shut off for the duration of a batch: the running jobs hold
        // the engine an unload would dispose out from under them.
        Transcribe.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscribeViewModel.IsRunning))
            {
                Models.IsTranscribing = Transcribe.IsRunning;

                // The symmetric half of the residency rule: a transcription starting mid-chat
                // takes the language model's child down. Fire-and-forget because this handler
                // cannot await and the kill is fast; the chat says what happened, and best-effort
                // is recorded as such in docs/PHASES.md rather than promised as more.
                if (Transcribe.IsRunning)
                {
                    _ = Ask.Chat.OnTranscriptionStartedAsync();
                }
            }
        };

        // Both opt-ins are gated on a model being on disk, and the Models tab is where those
        // arrive and leave. Without this the Transcribe tab answers that question once, at
        // construction, and never again — so a checkbox stays greyed out with a hint telling the
        // user to install the model they have just installed, and stays lit after a removal until
        // the batch fails. The two tabs are siblings and neither should know about the other, so
        // the wiring is here, where they are both already known.
        //
        // By task, and each to its own refresh: the diariser and the translator are separate
        // downloads that come and go independently, and one call for both would light a checkbox
        // whose model is still missing.
        foreach (var model in Models.Models)
        {
            var task = model.Descriptor.Task;

            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ModelViewModel.IsInstalled))
                {
                    return;
                }

                switch (task)
                {
                    case ModelTask.Diarisation:
                        Transcribe.RefreshSpeakerAvailability();
                        break;
                    case ModelTask.Translation:
                        Transcribe.RefreshTranslationAvailability();
                        break;
                    case ModelTask.VoiceActivity:
                        Transcribe.RefreshSpeechDetectionAvailability();
                        break;

                    // The answering entry, from 2026-08-27. It has to be named rather than left to
                    // the default below, which refreshes the transcribe panel: installing a
                    // language model would have lit up a control that does not use it and left the
                    // Ask panel still saying it has no model until the next selection change.
                    case ModelTask.Answering:
                        Ask.Chat.RefreshDocument();
                        break;

                    // The transcription entry joined this on 2026-08-23, when Start began loading
                    // for itself: whether Start is live now depends on weights being on disk, so
                    // downloading them has to light the button and removing them has to darken it.
                    default:
                        Transcribe.RefreshModelAvailability();
                        break;
                }
            };
        }

        // ShutdownAsync is handed to the updater rather than left to the window's close handler:
        // applying an update replaces the process without a Closing event, so the backend release
        // that avoids the teardown abort has to be reached from there too.
        // _settings rather than the parameter: both view models write the same file, and handing
        // one a null it resolves for itself would let the two disagree about which file that is
        // the moment anything overrides the path.
        var appUpdater = updater ?? new NotInstalledUpdater();

        Updates = new UpdatesViewModel(
            appUpdater,
            _settings,
            shutdown: ShutdownAsync);

        // Everything the About window says, built here because this is the only place that knows
        // all three of the version, the model directory and the settings file. It is handed the
        // values rather than the objects: the About window is opened by a Settings-tab button and
        // has no business reaching the queue, the session or the updater through its DataContext.
        About = new AboutViewModel(
            appUpdater.CurrentVersion,
            modelStore.RootDirectory,
            _settings.Path);
    }

    public TranscribeViewModel Transcribe { get; }

    public ModelsViewModel Models { get; }

    /// <summary>
    /// The v2 tab: a recording with a transport, its transcript as cues that seek it, and the
    /// chat panel that asks it questions through a local language model.
    /// </summary>
    public AskViewModel Ask { get; }

    /// <summary>The launch check, the notice it produces, and the setting that switches it off.</summary>
    public UpdatesViewModel Updates { get; }

    /// <summary>
    /// What the About window shows. Not a tab: the Licences page retired into that window on
    /// 2026-08-23, and it is opened from the Settings tab rather than reached along the switcher.
    /// </summary>
    public AboutViewModel About { get; }

    /// <summary>The one loaded model, shared by the Models tab that controls it and the
    /// Transcribe tab that uses it.</summary>
    public ModelSession Session { get; }

    /// <summary>
    /// Everything that has to happen before the process may exit: stop a running batch and wait
    /// for it, then dispose the session, which unloads the model and releases the process-level
    /// backend while the GPU driver is still alive.
    /// </summary>
    /// <remarks>
    /// The wait is real. The ABI has no abort hook, so a cancelled batch still finishes the native
    /// call it is inside — up to one batch of segments, which on CPU can be several seconds. Not
    /// waiting would mean releasing the backend under a decode that then recreates it, and the
    /// exit abort this exists to prevent comes back. The window stays open with a status line for
    /// that long, and a second close request while this is in progress closes it regardless.
    /// </remarks>
    public async Task ShutdownAsync()
    {
        if (Transcribe.IsRunning)
        {
            Transcribe.StatusMessage = "Closing — waiting for the segment being decoded to finish…";
            Transcribe.CancelCommand.Execute(null);

            while (Transcribe.IsRunning)
            {
                await Task.Delay(50).ConfigureAwait(true);
            }
        }

        // The language model's child first: it is a process, the kill is fast, and the job object
        // would catch an abrupt death anyway — this is the orderly version of the same end.
        await Ask.Chat.ReleaseEngineAsync().ConfigureAwait(true);

        // Before the session, and it is not arbitrary: the audio device is a COM object activated
        // on this thread, and it has to be released while the process still has one. It is also
        // the cheaper of the two, so a window closing during playback goes quiet at once rather
        // than after however long the engine takes to unload.
        Ask.Dispose();

        await Session.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Remembers the choice. A backend that has to be re-picked on every launch is a backend most
    /// users never pick, which is what made the CUDA channel's speed opt-in twice over.
    /// </summary>
    partial void OnBackendChanged(ComputeBackend value) =>
        _settings.Update(current => current with { Backend = value });

    /// <summary>Remembers the choice; the engine picks it up at the next question.</summary>
    partial void OnAskThinkingChanged(bool value) =>
        _settings.Update(current => current with { AskThinking = value });

    /// <summary>Keeps the bound row in step when the mode is set from anywhere else.</summary>
    partial void OnAskModeChanged(AskModePreference value)
    {
        OnPropertyChanged(nameof(SelectedAskMode));
        PersistAskMode(value);
    }

    /// <summary>Remembers the depth. The panel reads it at the next question — no new engine is
    /// needed, because the evidence count is a prompt fact and not a child-process argument.</summary>
    partial void OnAskEvidenceChanged(AskEvidenceDepth value)
    {
        OnPropertyChanged(nameof(SelectedAskEvidence));
        _settings.Update(current => current with { AskEvidence = value });
    }

    /// <summary>Remembers the model; the panel builds a fresh engine at the next question.</summary>
    partial void OnAskModelFileNameChanged(string? value) =>
        _settings.Update(current => current with { AskModelFileName = value });

    /// <summary>
    /// Remembers the token. Whitespace is stored as nothing, so clearing the box removes the key
    /// rather than persisting a blank that would be sent as an empty bearer credential — which the
    /// hub answers with a 401 that reads as "your token is wrong" rather than "you have not set one".
    /// </summary>
    /// <remarks>
    /// Read at each download rather than captured, so pasting a token makes the gated entry
    /// installable without restarting, and clearing it takes effect just as immediately. See
    /// <c>ModelsViewModel</c>'s installer factory.
    /// </remarks>
    partial void OnHuggingFaceTokenChanged(string? value) =>
        _settings.Update(current => current with
        {
            HuggingFaceToken = string.IsNullOrWhiteSpace(value) ? null : value.Trim(),
        });

    /// <summary>
    /// Keeps the bound row in step and remembers the choice. The panel drops an engine built
    /// under the other placement, so this takes effect at the next question — the placement is
    /// the child process's environment and cannot be changed under a running one.
    /// </summary>
    partial void OnAskExpertPlacementChanged(MoeExpertPlacement value)
    {
        OnPropertyChanged(nameof(SelectedAskExpertPlacement));
        _settings.Update(current => current with { AskExpertPlacement = value });
    }

    /// <summary>
    /// Keeps the bound row in step and remembers the choice. Takes effect at the next recording:
    /// the provider is fixed when the sidecar loads the model, and a labeller already loaded under
    /// another one is not re-negotiated under this.
    /// </summary>
    partial void OnDiarisationProviderChanged(string? value)
    {
        // The list first: a stored-but-unregistered choice appears in it as a marked row, so the
        // rows depend on this value and the bound SelectedItem must be re-resolved after them.
        OnPropertyChanged(nameof(DiarisationProviders));
        OnPropertyChanged(nameof(SelectedDiarisationProvider));
        OnPropertyChanged(nameof(DiarisationProviderExplanation));
        _settings.Update(current => current with { DiarisationProvider = value });

        // The graphics option needs a one-time preparation, and this is where a person asked for
        // it. Started here rather than offered as a second control the user has to find: choosing
        // the row is the whole of the intent, and a setting that silently does not work until you
        // run something is not a setting.
        if (value == "webgpu" && !SpeakerGraphsInstalled && !IsPreparingSpeakerGraphs)
        {
            _ = PrepareSpeakerGraphsAsync();
        }
    }

    /// <summary>Whether the graphics option's one-time preparation has already been done.</summary>
    /// <remarks>
    /// A file check, read fresh every time rather than cached: the models folder is not this
    /// application's alone, and the graphs go with the model when somebody removes it.
    /// </remarks>
    public bool SpeakerGraphsInstalled =>
        DiariserGraphs.AreInstalled(_engines.DiarisationModelDirectory);

    /// <summary>True while the graphs are being prepared, so the picker can show it is working.</summary>
    [ObservableProperty]
    private bool _isPreparingSpeakerGraphs;

    /// <summary>
    /// What the preparation is doing, or what went wrong. Null when there is nothing to say.
    /// </summary>
    [ObservableProperty]
    private string? _speakerGraphsMessage;

    partial void OnIsPreparingSpeakerGraphsChanged(bool value) =>
        OnPropertyChanged(nameof(DiarisationProviderExplanation));

    partial void OnSpeakerGraphsMessageChanged(string? value) =>
        OnPropertyChanged(nameof(HasSpeakerGraphsMessage));

    /// <summary>Whether there is a preparation message to draw. Bound to the line's visibility.</summary>
    public bool HasSpeakerGraphsMessage => SpeakerGraphsMessage is { Length: > 0 };

    /// <summary>
    /// Prepares the graphics option, once, and puts the choice back if it cannot be prepared.
    /// </summary>
    /// <remarks>
    /// <b>Reverting on failure is the part that matters.</b> A stored <c>webgpu</c> whose graphs
    /// are missing fails at load, which is after the recording has been read — so a preparation
    /// that did not work has to take the setting down with it rather than leave a choice that will
    /// break the next transcription.
    /// </remarks>
    private async Task PrepareSpeakerGraphsAsync()
    {
        if (_engines.DiarisationModelDirectory is not { Length: > 0 } directory)
        {
            SpeakerGraphsMessage = "Speaker labelling needs its model installed first.";
            DiarisationProvider = null;
            return;
        }

        IsPreparingSpeakerGraphs = true;
        SpeakerGraphsMessage = "Preparing the speaker model for your graphics. This happens once.";

        try
        {
            var exporter = _graphExporter();

            // ConfigureAwait(true): everything after this touches bound properties.
            await exporter.ExportAsync(directory, progress: null).ConfigureAwait(true);

            SpeakerGraphsMessage = null;
        }
        catch (Exception exception)
        {
            // The message lands in the window verbatim, so it is user copy: what happened, and what
            // they are left with.
            SpeakerGraphsMessage =
                "Could not prepare the speaker model for your graphics, so speaker labelling has "
                + $"gone back to automatic. {exception.Message}";
            DiarisationProvider = null;
        }
        finally
        {
            IsPreparingSpeakerGraphs = false;
            OnPropertyChanged(nameof(SpeakerGraphsInstalled));
            OnPropertyChanged(nameof(DiarisationProviderExplanation));
        }
    }

    /// <summary>
    /// Re-offers the rows once the probe has answered, in ItemsSource-then-SelectedItem order.
    /// </summary>
    partial void OnRegisteredDiariserProvidersChanged(IReadOnlyList<string>? value)
    {
        OnPropertyChanged(nameof(DiarisationProviders));
        OnPropertyChanged(nameof(SelectedDiarisationProvider));
        OnPropertyChanged(nameof(DiarisationProviderExplanation));
    }

    /// <summary>True once a probe has been started, so opening the tab repeatedly asks once.</summary>
    private bool _diariserProviderProbeStarted;

    /// <summary>
    /// Asks the sidecar what ONNX Runtime registered, off the UI thread, at most once per window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Started when the Settings page is opened rather than at launch: the op reports each engine's
    /// automatic resolution as well as the raw list, and getting that honestly costs the engines'
    /// imports — seconds of torch — which is not a cost to put on every start of an application
    /// most of whose users never open this page.
    /// </para>
    /// <para>
    /// A null answer leaves the flag down so a later visit tries again. "Not established" is
    /// usually a sidecar that has not been installed yet or a machine that was busy, and both of
    /// those stop being true without the window restarting.
    /// </para>
    /// </remarks>
    private async Task ProbeDiariserProvidersAsync()
    {
        if (_diariserProviderProbeStarted)
        {
            return;
        }

        _diariserProviderProbeStarted = true;

        // ConfigureAwait(true): the continuation raises property changes for bound controls, which
        // belongs on the UI thread.
        var registered = await _engines.AvailableDiariserProvidersAsync().ConfigureAwait(true);
        if (registered is null)
        {
            _diariserProviderProbeStarted = false;
            return;
        }

        RegisteredDiariserProviders = registered;
    }

    /// <summary>The twin of the above, and for the same reason.</summary>
    partial void OnDiarisationBatchSizeChanged(int? value)
    {
        OnPropertyChanged(nameof(SelectedDiarisationBatchSize));
        _settings.Update(current => current with { DiarisationBatchSize = value });
    }

    /// <summary>Remembers the choice; the panel reads it at the next question.</summary>
    private void PersistAskMode(AskModePreference value) =>
        _settings.Update(current => current with { AskMode = value });

    /// <summary>
    /// Re-reads the model directory whenever the Models tab is opened.
    /// </summary>
    /// <remarks>
    /// The tab answers questions about files on a disk that this application is not the only writer
    /// of — another copy of it, an older version's leftovers, Explorer — and it used to answer them
    /// from a reading taken when the window opened and never taken again. Opening the tab is the
    /// moment a person is about to trust what it says, so it is the moment to look.
    /// </remarks>
    partial void OnSelectedTabChanged(int value)
    {
        if (value == ModelsTabIndex)
        {
            Models.Refresh();
        }
        else if (value == AskTabIndex)
        {
            // The same principle for the chat panel's cover: it tells the user to put a .gguf in
            // the models folder "and come back here", and switching to this tab is the coming
            // back — the refresh re-runs the availability check, in both directions.
            Ask.Chat.RefreshDocument();
        }
        else if (value == SettingsTabIndex)
        {
            // And the same again for the model picker, which lists files on that same disk.
            OnPropertyChanged(nameof(AskModels));
            OnPropertyChanged(nameof(SelectedAskModel));

            // The batch setting belongs to one of the two diarisers, and which one is chosen is
            // changed on the Models tab — so the control's enabled state is stale exactly when
            // somebody has just been there. Opening this page is when they are about to read it.
            OnPropertyChanged(nameof(CanSetDiarisationBatchSize));
            OnPropertyChanged(nameof(DiarisationBatchSizeHint));

            // And the provider rows, which cannot be known without asking the runtime. Deliberately
            // not awaited: the page draws now with every row on offer and narrows when the answer
            // arrives, rather than blocking a tab switch on a torch import.
            _ = ProbeDiariserProvidersAsync();

            // **Repairs a stored choice whose graphs are gone.** The only way to store `webgpu` is
            // to have prepared it once, so this fires for one situation: the speaker model was
            // removed and reinstalled, taking its `onnx` subdirectory with it. Left alone the next
            // transcription would read the whole recording and then fail in the sidecar; done here
            // it costs a minute on a page the person is already looking at.
            if (DiarisationProvider == "webgpu" && !SpeakerGraphsInstalled && !IsPreparingSpeakerGraphs)
            {
                _ = PrepareSpeakerGraphsAsync();
            }
        }
    }

    /// <summary>Where the Models page sits in the TabControl. The switcher's order is its own.</summary>
    private const int ModelsTabIndex = 1;

    /// <summary>Where the Ask page sits in the TabControl.</summary>
    private const int AskTabIndex = 4;

    /// <summary>Where the Settings page sits — the model picker reads the disk when it opens.</summary>
    private const int SettingsTabIndex = 5;

    public IReadOnlyList<ComputeBackend> Backends { get; } =
        [ComputeBackend.Vulkan, ComputeBackend.Cuda, ComputeBackend.Cpu];

    /// <summary>The ask-mode picker's rows. Plain names, not enum spellings: this list is read by
    /// someone choosing, not by a developer reading code.</summary>
    public IReadOnlyList<AskModeChoice> AskModes { get; } =
    [
        new(AskModePreference.Automatic, "Decide from my question"),
        new(AskModePreference.Retrieval, "The parts that matched"),
        new(AskModePreference.WholeTranscript, "The whole transcript"),
    ];

    /// <summary>
    /// The evidence-depth picker's rows. The labels say what the reader gets rather than how many
    /// windows it is: the count is an implementation number, and the thing being chosen is how
    /// carefully the recording is searched before the model answers.
    /// </summary>
    public IReadOnlyList<AskEvidenceChoice> AskEvidenceLevels { get; } =
    [
        new(AskEvidenceDepth.Thorough, "Search more of the recording"),
        new(AskEvidenceDepth.Balanced, "In between"),
        new(AskEvidenceDepth.Fast, "Answer faster"),
    ];

    /// <summary>The twin of <see cref="SelectedAskMode"/>, and for the same reason.</summary>
    public AskEvidenceChoice SelectedAskEvidence
    {
        get => AskEvidenceLevels.First(choice => choice.Depth == AskEvidence);
        set => AskEvidence = value.Depth;
    }

    /// <summary>The picker binds a row rather than the enum, and this keeps the two in step.</summary>
    public AskModeChoice SelectedAskMode
    {
        get => AskModes.First(choice => choice.Mode == AskMode);
        set => AskMode = value.Mode;
    }

    /// <summary>
    /// The expert-placement picker's rows. Named for what a person sees happen — where the work
    /// runs — rather than for the two environment variables underneath.
    /// </summary>
    public IReadOnlyList<AskExpertPlacementChoice> AskExpertPlacements { get; } =
    [
        new(MoeExpertPlacement.Automatic, "Decide from my graphics"),
        new(MoeExpertPlacement.Device, "On the graphics card"),
        new(MoeExpertPlacement.SystemMemory, "In system memory"),
    ];

    /// <summary>The twin of <see cref="SelectedAskMode"/>, and for the same reason.</summary>
    public AskExpertPlacementChoice SelectedAskExpertPlacement
    {
        get => AskExpertPlacements.First(choice => choice.Placement == AskExpertPlacement);
        set => AskExpertPlacement = value.Placement;
    }

    /// <summary>
    /// The ask-model picker's rows: every .gguf in the models folder, largest first, under a
    /// row for letting the application choose. Re-read whenever the Settings page is opened,
    /// because the folder is not this application's alone to write.
    /// </summary>
    public IReadOnlyList<AskModelChoice> AskModels
    {
        get
        {
            var rows = new List<AskModelChoice> { new(null, "The largest one there") };
            if (_llamaAnswerEngines is { } provider)
            {
                rows.AddRange(provider.AvailableModelFileNames().Select(name => new AskModelChoice(name, name)));
            }

            // A name chosen before the file went away still shows, so the picker explains the
            // setting rather than silently reverting to a row the person did not choose.
            if (AskModelFileName is { Length: > 0 } chosen
                && !rows.Any(row => string.Equals(row.FileName, chosen, StringComparison.OrdinalIgnoreCase)))
            {
                rows.Add(new AskModelChoice(chosen, chosen + " (not in the folder)"));
            }

            return rows;
        }
    }

    public AskModelChoice SelectedAskModel
    {
        get => AskModels.FirstOrDefault(
                row => string.Equals(row.FileName, AskModelFileName, StringComparison.OrdinalIgnoreCase))
            ?? AskModels[0];
        set => AskModelFileName = value?.FileName;
    }

    public string AskModelExplanation =>
        "Which model answers your questions. Bigger is not always slower — a mixture-of-experts "
        + "model can answer faster than a smaller dense one. Whichever you pick is used from your "
        + "next question.";

    public string AskModeExplanation =>
        "Deciding from your question sends summaries and \"what are the main topics\" across the "
        + "whole recording, and everything else through the parts that matched — which is faster. "
        + "A long recording is covered by an even sample of it rather than read minute by minute, "
        + "and the answer says so; reading every word of a long one is only done when you ask for "
        + "it, because it can take a while.";

    public string AskExpertPlacementExplanation =>
        "Some models split their work into experts and run only a few of them for each word. They "
        + "are fastest on a graphics card with room to hold them; where there is no room — "
        + "graphics built into the processor, or a model bigger than the card — they run from "
        + "system memory instead, and a model that tries anyway may fail to start. Automatic "
        + "weighs the model against your graphics. Dense models are unaffected either way, and "
        + "whichever you pick is used from your next question.";

    public string BackendExplanation =>
        "Vulkan is the default: it runs on NVIDIA, AMD and Intel with only a normal graphics driver. " +
        "CUDA is used automatically when this build has it, and needs its own runtime files. " +
        "CPU always works and is the fallback. Whichever you pick is remembered.";

    /// <summary>
    /// The speaker-provider picker's rows. Automatic first, and it is the shipped choice.
    /// </summary>
    /// <remarks>
    /// The names are <see cref="AppSettings.DiarisationProviders"/>'s, which is the list this
    /// window offers rather than everything the sidecar accepts; that property says what is left
    /// out and why.
    /// </remarks>
    private static readonly IReadOnlyList<DiarisationProviderChoice> AllDiarisationProviders =
    [
        new(null, "Automatic"),
        new("cpu", "CPU"),
        new("cuda", "CUDA (NVIDIA)"),
        new("webgpu", "Graphics (WebGPU)"),
    ];

    /// <summary>
    /// What ONNX Runtime registered here, or null until the probe has answered. Null keeps every
    /// row on offer — see <see cref="IEngineProvider.AvailableDiariserProvidersAsync"/> for why a
    /// failed probe must not empty the picker.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string>? _registeredDiariserProviders;

    /// <summary>
    /// The rows on offer: Automatic, the CPU, and CUDA.
    /// </summary>
    /// <remarks>
    /// <b>Three fixed rows, because the diariser has one vocabulary.</b> It is a torch pipeline, so
    /// these are torch devices; <c>webgpu</c> and <c>dml</c> are ONNX Runtime execution providers
    /// and the sidecar refuses them by name rather than falling back to the CPU.
    /// <para>
    /// <b>This filtered by what ONNX Runtime had registered until 2026-08-27, and that machinery is
    /// gone with the engine it served.</b> Two corrections landed on it that day — a CUDA row the
    /// bundle's <c>onnxruntime-webgpu</c> wheel could not have provided, then a WebGPU row the torch
    /// pipeline would have refused — and shelving the ONNX diariser removed the question both were
    /// answering. What went with them is a real capability: <b>nothing now checks whether this
    /// machine can use the CUDA row before offering it.</b> A torch build without CUDA answers at
    /// load, which is later and worse than not offering it, and restoring the check means asking
    /// torch rather than ONNX Runtime — which nothing does. <c>docs/UNPROVEN.md</c> carries it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DiarisationProviderChoice> DiarisationProviders => AllDiarisationProviders;

    /// <summary>The twin of <see cref="SelectedAskExpertPlacement"/>, and for the same reason.</summary>
    public DiarisationProviderChoice SelectedDiarisationProvider
    {
        get => DiarisationProviders.FirstOrDefault(choice => choice.Provider == DiarisationProvider)
               ?? DiarisationProviders[0];
        set => DiarisationProvider = value.Provider;
    }

    /// <summary>
    /// The batch-size picker's rows. The model's own value first, which is the shipped choice.
    /// </summary>
    /// <remarks>
    /// Three sizes rather than a free number, because these are the only ones anything was ever
    /// observed at — on DiariZen, which is not the engine this control now drives. **Nothing has
    /// been observed on the pyannote pipeline at any of them**, including whether the labels are
    /// invariant to the choice, which is what used to make choosing between them safe. The rows are
    /// kept because they are still plausible window counts and because removing the control would
    /// hide a setting the sidecar accepts; the explanation beside them no longer promises anything.
    /// "Windows" rather than "batch" because the thing being counted is sixteen-second windows of
    /// the recording, which is something a person can picture.
    /// </remarks>
    public IReadOnlyList<DiarisationBatchSizeChoice> DiarisationBatchSizes { get; } =
    [
        new(null, "The model's own setting"),
        new(8, "8 windows — least memory"),
        new(16, "16 windows"),
        new(32, "32 windows — most memory"),
    ];

    /// <summary>The twin of the above.</summary>
    public DiarisationBatchSizeChoice SelectedDiarisationBatchSize
    {
        get => DiarisationBatchSizes.FirstOrDefault(choice => choice.Size == DiarisationBatchSize)
               ?? DiarisationBatchSizes[0];
        set => DiarisationBatchSize = value.Size;
    }

    /// <summary>
    /// True when the chosen speaker model has a batch size to set. Drawn disabled otherwise, with
    /// the reason beside it, on the same terms as the neural speech detection box.
    /// </summary>
    public bool CanSetDiarisationBatchSize => _engines.SupportsDiariserBatchSize;

    /// <summary>Why the batch picker is disabled, or null when it is not.</summary>
    public string? DiarisationBatchSizeHint =>
        CanSetDiarisationBatchSize
            ? null
            // Until 2026-08-27 this said the setting belonged to one of two speaker models and not
            // to the built-in one. There is one model, it is not built in, and this property is
            // false only when speaker labelling is unavailable at all — so the sentence named a
            // cause that could not be the cause.
            : "This setting needs the speaker model installed and the bundled Python present.";

    /// <summary>
    /// What the choices mean, naming only the ones on offer.
    /// </summary>
    /// <remarks>
    /// Built from the offered rows rather than written once, because the earlier fixed text
    /// explained CUDA on machines where no CUDA row exists — a paragraph about a control that is
    /// not there reads as a missing feature rather than as an absent one.
    /// </remarks>
    public string DiarisationProviderExplanation
    {
        get
        {
            var offered = DiarisationProviders.Select(row => row.Provider).ToArray();
            var text =
                "Automatic is your processor, because the Python that ships with Uindosill has no "
                + "graphics build of the speaker model's runtime. Nothing here has been measured on "
                + "any option.";

            if (offered.Contains("webgpu"))
            {
                // **The sentence here promised the opposite until 2026-08-28**, and it was
                // Sortformer's: "it groups voices slightly differently, so the labels will not
                // match". On the pipeline that ships, the two routes were measured against each
                // other on a five-minute recording and produced the same turns to the millisecond
                // with the same speakers, so that warning described a model nobody is choosing.
                // One recording is not a promise, which is why this says "on what has been tried".
                text += " Graphics (WebGPU) does the heavy part on your graphics card and finishes "
                    + "sooner. On what has been tried it gave the same speakers and the same times "
                    + "as automatic. It needs a one-time preparation, which starts when you choose "
                    + "it and takes about a minute.";

                if (IsPreparingSpeakerGraphs)
                {
                    text += " Preparing it now.";
                }
            }

            if (offered.Contains("cuda"))
            {
                // **The alternative sentence here was Sortformer's measurement**, taken on AMI
                // test: "changes the labels in the same way". That engine went to
                // `attic/sortformer/` on 2026-08-27 and `DiariserRunsInTorch` is unconditionally
                // true now, so only one arm was ever reachable afterwards and the other is gone.
                // Nothing has been measured on any provider for the pipeline that remains, which is
                // what this says rather than borrowing a finding from a model nobody is choosing.
                text += " CUDA runs on an NVIDIA card. Whether it changes the labels has not been checked.";
            }

            // **No availability claim, because this list no longer makes one.** While the diariser
            // was an ONNX graph the rows were filtered by what ONNX Runtime had registered, and the
            // two sentences that stood here reported that filter's state. `DiariserRunsInTorch` is
            // unconditionally true since 2026-08-27, so `DiarisationProviders` returns a fixed
            // torch device list and the probe's answer is never applied — saying "only what this
            // machine can run is listed" would be asserting a check that does not happen. CUDA is
            // offered whether or not this torch build has it, and naming it is how somebody finds
            // out.
            text += " CUDA is offered whether or not this computer has it; choosing it will say so "
                + "if it cannot be used.";

            return text + " Takes effect at your next recording.";
        }
    }

    // **The 11 GB figure and the "same labels either way" assurance were DiariZen's**, measured on
    // the engine this setting used to belong to. That engine was shelved on 2026-08-27 and this
    // control now applies to the pyannote pipeline, on which neither has been measured — so both
    // sentences left rather than being re-pointed at a model they were never about.
    public string DiarisationBatchSizeExplanation =>
        "How much of the recording the speaker model holds at once. Fewer windows need less memory, "
        + "which is worth choosing if labelling a long recording runs the machine out of it; more "
        + "windows need more. How much either costs on this model, and whether the setting changes "
        + "the labels, has not been measured. Takes effect at your next recording.";

}

/// <summary>One row of the speaker-provider picker: the stored name, under the name a person reads.</summary>
/// <remarks>
/// Null is "Automatic" and is a real row rather than a missing value — the same shape the settings
/// file uses, where absent means automatic.
/// </remarks>
public sealed record DiarisationProviderChoice(string? Provider, string Label)
{
    /// <summary>What the picker shows. The list is bound directly, so this is the label.</summary>
    public override string ToString() => Label;
}

/// <summary>One row of the speaker batch-size picker.</summary>
public sealed record DiarisationBatchSizeChoice(int? Size, string Label)
{
    /// <summary>What the picker shows. The list is bound directly, so this is the label.</summary>
    public override string ToString() => Label;
}

/// <summary>One row of the ask-mode picker: the setting, under the name a person reads.</summary>
public sealed record AskEvidenceChoice(AskEvidenceDepth Depth, string Label)
{
    public override string ToString() => Label;
}

public sealed record AskModeChoice(AskModePreference Mode, string Label)
{
    /// <summary>What the picker shows. The list is bound directly, so this is the label.</summary>
    public override string ToString() => Label;
}

/// <summary>One row of the ask-model picker; a null file name is "let the application choose".</summary>
public sealed record AskModelChoice(string? FileName, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row of the expert-placement picker: the setting, under the name a person reads.</summary>
public sealed record AskExpertPlacementChoice(MoeExpertPlacement Placement, string Label)
{
    /// <summary>What the picker shows. The list is bound directly, so this is the label.</summary>
    public override string ToString() => Label;
}

