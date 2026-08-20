using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Parakeet.App.Services;
using Parakeet.Core.Licensing;
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
        Func<IReadOnlyList<ComputeBackend>>? backendsOnDisk = null)
    {
        ArgumentNullException.ThrowIfNull(engines);

        var modelStore = store ?? new LocalModelStore();
        var modelCatalog = catalog ?? ModelCatalog.Default;
        _settings = settings ?? new AppSettingsStore();

        // The field, not the property: assigning the property here would fire OnBackendChanged and
        // write the file on every launch, including the launches where nothing was chosen.
        _backend = _settings.Load().Backend
            ?? BestBackendPresent(backendsOnDisk?.Invoke() ?? ParakeetNativeLibrary.BackendsPresentOnDisk());

        Session = new ModelSession(engines);

        Models = new ModelsViewModel(modelStore, modelCatalog, session: Session, backend: () => Backend);
        Transcribe = new TranscribeViewModel(
            engines,
            () => new EngineSelection
            {
                Backend = Backend,
                Model = Models.SelectedDescriptor,
            },
            Session);

        // Load and unload have to be shut off for the duration of a batch: the running jobs hold
        // the engine an unload would dispose out from under them.
        Transcribe.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscribeViewModel.IsRunning))
            {
                Models.IsTranscribing = Transcribe.IsRunning;
            }
        };

        // The speaker opt-in is gated on the diariser being on disk, and the Models tab is where it
        // arrives and leaves. Without this the Transcribe tab answers that question once, at
        // construction, and never again — so the checkbox stays greyed out with a hint telling the
        // user to install the model they have just installed, and stays lit after a removal until
        // the batch fails. The two tabs are siblings and neither should know about the other, so
        // the wiring is here, where they are both already known.
        foreach (var model in Models.Models.Where(m => m.Descriptor.Task == ModelTask.Diarisation))
        {
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModelViewModel.IsInstalled))
                {
                    Transcribe.RefreshSpeakerAvailability();
                }
            };
        }

        // ShutdownAsync is handed to the updater rather than left to the window's close handler:
        // applying an update replaces the process without a Closing event, so the backend release
        // that avoids the teardown abort has to be reached from there too.
        // _settings rather than the parameter: both view models write the same file, and handing
        // one a null it resolves for itself would let the two disagree about which file that is
        // the moment anything overrides the path.
        Updates = new UpdatesViewModel(
            updater ?? new NotInstalledUpdater(),
            _settings,
            shutdown: ShutdownAsync);
    }

    public TranscribeViewModel Transcribe { get; }

    public ModelsViewModel Models { get; }

    /// <summary>The launch check, the notice it produces, and the setting that switches it off.</summary>
    public UpdatesViewModel Updates { get; }

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

        await Session.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// The backend to start on when the user has never chosen one: the fastest tier whose binaries
    /// are actually on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CUDA outranks Vulkan because its presence is not an accident. The default channel ships cpu
    /// and vulkan; the cuda directory exists only in the second channel, whose installer is 818 MB
    /// against 82 MB, so a user who has it went and got it. Starting them on Vulkan gave away the
    /// 1.7x between the two measured tiers — RTF 0.0064 against 0.0110 on the desktop — and did it
    /// on every launch, because nothing was persisted.
    /// </para>
    /// <para>
    /// Nothing on disk means Vulkan, which is what shipped before any of this and what a build from
    /// source with no vendored natives should still say. Presence is not capability: a CUDA drop
    /// with no working NVIDIA driver behind it loads nothing, and the loader's chain for a CUDA
    /// request is CUDA then CPU rather than CUDA then Vulkan — a rule written when asking for CUDA
    /// was always deliberate. Now that it can be a default, that user lands on CPU rather than
    /// Vulkan for one launch; the Models tab says so with a warning that names the fallback, and
    /// the choice they make instead is remembered.
    /// </para>
    /// </remarks>
    internal static ComputeBackend BestBackendPresent(IReadOnlyList<ComputeBackend> present)
    {
        ArgumentNullException.ThrowIfNull(present);

        if (present.Contains(ComputeBackend.Cuda))
        {
            return ComputeBackend.Cuda;
        }

        if (present.Contains(ComputeBackend.Vulkan) || present.Count == 0)
        {
            return ComputeBackend.Vulkan;
        }

        return present.Contains(ComputeBackend.Cpu) ? ComputeBackend.Cpu : ComputeBackend.Vulkan;
    }

    /// <summary>
    /// Remembers the choice. A backend that has to be re-picked on every launch is a backend most
    /// users never pick, which is what made the CUDA channel's speed opt-in twice over.
    /// </summary>
    partial void OnBackendChanged(ComputeBackend value) =>
        _settings.Update(current => current with { Backend = value });

    public IReadOnlyList<ComputeBackend> Backends { get; } =
        [ComputeBackend.Vulkan, ComputeBackend.Cuda, ComputeBackend.Cpu];

    public string BackendExplanation =>
        "Vulkan is the default: it runs on NVIDIA, AMD and Intel with only a normal graphics driver. " +
        "CUDA is used automatically when this build has it, and needs its own runtime files. " +
        "CPU always works and is the fallback. Whichever you pick is remembered.";

    /// <summary>
    /// The full notice package, shown in the application because the licence requires it to be
    /// present where the material is used, not only in a file in the source repository.
    /// </summary>
    public string LicenceText
    {
        get
        {
            var lines = new List<string>();

            foreach (var attribution in Attributions.ById.Values)
            {
                lines.Add(attribution.ToPlainText(Environment.NewLine));
            }

            lines.Add("Restrictions that come with these weights:");
            lines.AddRange(Attributions.WeightUsageRestrictions.Select(r => "  - " + r));
            lines.Add(string.Empty);
            lines.Add("Third-party components:");

            foreach (var component in Attributions.Components)
            {
                lines.Add($"  {component.Component} — {component.License} — {component.Uri}");

                // The notes carry the qualifying text — which builds ship a component, and on what
                // terms. This panel used to drop them while `uindosill notice` printed them, so the
                // two surfaces disagreed about a licence notice. They are rendered in both now.
                if (component.Notes is { Length: > 0 } notes)
                {
                    lines.Add($"    {notes}");
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string EnvironmentSummary =>
        $"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} " +
        $"({RuntimeInformation.ProcessArchitecture}), {Environment.ProcessorCount} logical processors";

    /// <summary>
    /// Stated plainly in the window rather than buried: the ABI takes no thread count, so a
    /// thread control here would be a slider connected to nothing.
    /// </summary>
    public string ThreadingNote =>
        $"Decode threads are chosen by the engine. The parakeet.cpp ABI takes no thread count, so this build " +
        $"cannot cap them at the recommended {DecodeThreadPlanner.MaxRecommended}.";
}
