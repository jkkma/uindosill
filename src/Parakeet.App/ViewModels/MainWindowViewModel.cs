using CommunityToolkit.Mvvm.ComponentModel;
using Parakeet.App.Services;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp.Interop;

namespace Parakeet.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppSettingsStore _settings;

    /// <summary>The real provider when this window built one, so the model picker can list what
    /// is on disk. Null under the fake provider the tests supply, and the picker is then empty
    /// but for its automatic row.</summary>
    private readonly LlamaAnswerEngineProvider? _llamaAnswerEngines;

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

    /// <summary>The .gguf chosen for asking, or null for "the largest one there".</summary>
    [ObservableProperty]
    private string? _askModelFileName;

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
        IAnswerEngineProvider? answerEngines = null)
    {
        ArgumentNullException.ThrowIfNull(engines);

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
        _askModelFileName = loaded.AskModelFileName;

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
                modelStore, () => AskThinking, () => AskMode, () => AskModelFileName);
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

    /// <summary>Remembers the model; the panel builds a fresh engine at the next question.</summary>
    partial void OnAskModelFileNameChanged(string? value) =>
        _settings.Update(current => current with { AskModelFileName = value });

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

    /// <summary>The picker binds a row rather than the enum, and this keeps the two in step.</summary>
    public AskModeChoice SelectedAskMode
    {
        get => AskModes.First(choice => choice.Mode == AskMode);
        set => AskMode = value.Mode;
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
        "Deciding from your question sends summaries and \"what are the main topics\" through the "
        + "whole recording, and everything else through the parts that matched — which is faster. "
        + "A long recording is only read whole when you ask for it, because that can take a while.";

    public string BackendExplanation =>
        "Vulkan is the default: it runs on NVIDIA, AMD and Intel with only a normal graphics driver. " +
        "CUDA is used automatically when this build has it, and needs its own runtime files. " +
        "CPU always works and is the fallback. Whichever you pick is remembered.";

}

/// <summary>One row of the ask-mode picker: the setting, under the name a person reads.</summary>
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

