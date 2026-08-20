using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Parakeet.App.Services;
using Parakeet.Core.Licensing;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private ComputeBackend _backend = ComputeBackend.Vulkan;

    [ObservableProperty]
    private int _selectedTab;

    public MainWindowViewModel(
        IEngineProvider engines,
        IModelStore? store = null,
        ModelCatalog? catalog = null,
        IAppUpdater? updater = null,
        AppSettingsStore? settings = null)
    {
        ArgumentNullException.ThrowIfNull(engines);

        var modelStore = store ?? new LocalModelStore();
        var modelCatalog = catalog ?? ModelCatalog.Default;

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
        Updates = new UpdatesViewModel(
            updater ?? new NotInstalledUpdater(),
            settings,
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

    public IReadOnlyList<ComputeBackend> Backends { get; } =
        [ComputeBackend.Vulkan, ComputeBackend.Cuda, ComputeBackend.Cpu];

    public string BackendExplanation =>
        "Vulkan is the default: it runs on NVIDIA, AMD and Intel with only a normal graphics driver. " +
        "CUDA is opt-in and needs its own runtime files. CPU always works and is the fallback.";

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
