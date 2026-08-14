using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parakeet.Core.Licensing;
using Parakeet.Core.Models;

namespace Parakeet.App.ViewModels;

public sealed partial class ModelViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public ModelViewModel(ModelDescriptor descriptor, bool installed)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        _isInstalled = installed;
        _status = installed ? "Installed" : "Not installed";
    }

    public ModelDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public string DisplayName => Descriptor.DisplayName;

    public string Licence => Descriptor.License;

    public string Notes => Descriptor.Notes ?? string.Empty;

    public string Languages => Descriptor.Languages.Count == 0
        ? "unspecified"
        : string.Join(" ", Descriptor.Languages);

    /// <summary>
    /// Shown next to every entry whose provenance has not been checked. Silence here would let
    /// a guessed URL and an unpinned digest look exactly like a verified one.
    /// </summary>
    public string Provenance => (Descriptor.Verified, Descriptor.Sha256 is not null) switch
    {
        (true, true) => "Verified against the repository, digest pinned.",
        (true, false) => "Catalogue entry checked, but no digest is pinned — the download cannot be verified.",
        (false, true) => "Digest pinned, but the catalogue entry itself was never checked.",
        (false, false) => "Unverified: file name, size and digest were never checked against the repository. " +
                          "Downloading requires the explicit unverified opt-in below.",
    };

    public bool NeedsUnverifiedOptIn => Descriptor.Sha256 is null;
}

public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly IModelStore _store;
    private readonly ModelCatalog _catalog;
    private readonly Func<ModelInstaller> _installerFactory;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    private ModelViewModel? _selected;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _allowUnverified;

    public ModelsViewModel(IModelStore store, ModelCatalog catalog, Func<ModelInstaller>? installerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);

        _store = store;
        _catalog = catalog;
        _installerFactory = installerFactory ?? (() => new ModelInstaller(store));

        Models = [.. catalog.Models.Select(m => new ModelViewModel(m, store.IsInstalled(m)))];
        Selected = Models.FirstOrDefault(m => m.IsInstalled) ?? Models.FirstOrDefault();
    }

    public ObservableCollection<ModelViewModel> Models { get; }

    public string ModelDirectory => _store.RootDirectory;

    /// <summary>The CC BY 4.0 notice, rendered in the application rather than only in a repo file.</summary>
    public string Attribution => string.Join(
        Environment.NewLine,
        Attributions.ById.Values.Select(a => a.ToPlainText(Environment.NewLine)));

    public ModelDescriptor? SelectedDescriptor => Selected?.Descriptor;

    [RelayCommand]
    private void CancelDownload() => _cancellation?.Cancel();

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Selected is not { } model || model.IsBusy)
        {
            return;
        }

        if (model.NeedsUnverifiedOptIn && !AllowUnverified)
        {
            StatusMessage =
                "This catalogue entry has no pinned SHA-256, so the download cannot be verified. " +
                "Tick 'allow unverified' to install it anyway.";
            return;
        }

        model.IsBusy = true;
        model.Status = "Starting";
        StatusMessage = null;
        _cancellation = new CancellationTokenSource();

        try
        {
            using var installer = _installerFactory();
            var progress = new Progress<ModelInstallProgress>(p =>
            {
                model.Progress = (p.Fraction ?? 0) * 100;
                model.Status = p.Phase switch
                {
                    ModelInstallPhase.Connecting => "Connecting",
                    ModelInstallPhase.Downloading => Describe(p),
                    ModelInstallPhase.Verifying => "Verifying checksum",
                    ModelInstallPhase.Installing => "Installing",
                    _ => "Done",
                };
            });

            var result = await installer.InstallAsync(
                model.Descriptor,
                new ModelInstallOptions { AllowUnverified = AllowUnverified },
                progress,
                _cancellation.Token).ConfigureAwait(true);

            model.IsInstalled = true;
            model.Status = "Installed";
            model.Progress = 100;
            StatusMessage = model.Descriptor.Sha256 is null
                ? $"Installed. Its SHA-256 is {result.Sha256} — pin that in the catalogue so the next install is checked."
                : "Installed and verified.";
        }
        catch (OperationCanceledException)
        {
            model.Status = "Cancelled";
            StatusMessage = "Download cancelled. Partial progress is kept and will resume.";
        }
        catch (ModelInstallException ex)
        {
            model.Status = "Failed";
            StatusMessage = ex.Message;
        }
        finally
        {
            model.IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not { } model)
        {
            return;
        }

        var removed = _store.Remove(model.Descriptor);
        model.IsInstalled = false;
        model.Progress = 0;
        model.Status = "Not installed";
        StatusMessage = removed ? $"Removed {model.Id}." : $"{model.Id} was not installed.";
    }

    public void Refresh()
    {
        foreach (var model in Models)
        {
            model.IsInstalled = _store.IsInstalled(model.Descriptor);
        }
    }

    private static string Describe(ModelInstallProgress progress)
    {
        var speed = progress.BytesPerSecond is { } bps
            ? string.Create(CultureInfo.InvariantCulture, $" at {bps / 1024 / 1024:0.0} MiB/s")
            : string.Empty;

        var remaining = progress.Remaining is { } left
            ? string.Create(CultureInfo.InvariantCulture, $", {left:hh\\:mm\\:ss} left")
            : string.Empty;

        return $"Downloading{speed}{remaining}";
    }
}
