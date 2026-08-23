using CommunityToolkit.Mvvm.ComponentModel;
using Parakeet.App.Services;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp.Interop;

namespace Parakeet.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppSettingsStore _settings;

    [ObservableProperty]
    private ComputeBackend _backend = ComputeBackend.Vulkan;

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
        string? downloadRoot = null)
    {
        ArgumentNullException.ThrowIfNull(engines);

        var modelStore = store ?? new LocalModelStore();
        var modelCatalog = catalog ?? ModelCatalog.Default;
        _settings = settings ?? new AppSettingsStore();

        // The field, not the property: assigning the property here would fire OnBackendChanged and
        // write the file on every launch, including the launches where nothing was chosen.
        _backend = _settings.Load().Backend
            ?? ParakeetNativeLibrary.PreferredBackend(
                backendsOnDisk?.Invoke() ?? ParakeetNativeLibrary.BackendsPresentOnDisk());

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
        // wrong shows a transcript beside the wrong recording.
        Ask = new AskViewModel(Transcribe.Jobs, player ?? MediaPlayers.ForThisBuild());

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
    /// The v2 tab: a recording with a transport, its transcript as cues that seek it, and a chat
    /// panel that is drawn, disabled and covered by a notice because nothing is behind it yet.
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
    }

    /// <summary>Where the Models page sits in the TabControl. The switcher's order is its own.</summary>
    private const int ModelsTabIndex = 1;

    public IReadOnlyList<ComputeBackend> Backends { get; } =
        [ComputeBackend.Vulkan, ComputeBackend.Cuda, ComputeBackend.Cpu];

    public string BackendExplanation =>
        "Vulkan is the default: it runs on NVIDIA, AMD and Intel with only a normal graphics driver. " +
        "CUDA is used automatically when this build has it, and needs its own runtime files. " +
        "CPU always works and is the fallback. Whichever you pick is remembered.";

}

