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

    public MainWindowViewModel(IEngineProvider engines, IModelStore? store = null, ModelCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(engines);

        var modelStore = store ?? new LocalModelStore();
        var modelCatalog = catalog ?? ModelCatalog.Default;

        Models = new ModelsViewModel(modelStore, modelCatalog);
        Transcribe = new TranscribeViewModel(engines, () => new EngineSelection
        {
            Backend = Backend,
            Model = Models.SelectedDescriptor,
        });
    }

    public TranscribeViewModel Transcribe { get; }

    public ModelsViewModel Models { get; }

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
