using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parakeet.App.Services;
using Parakeet.Core.Licensing;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.ViewModels;

public sealed partial class ModelViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isInstalled;

    [ObservableProperty]
    private double _progress;

    /// <summary>
    /// True for the one entry currently in memory. The list showed "Installed" against every
    /// downloaded model and nothing about which of them was actually loaded, which is the state
    /// the user is choosing from.
    /// </summary>
    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
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

    /// <summary>
    /// Downloading is only meaningful for a model that is not already here. Binding the button to
    /// <see cref="IsBusy"/> alone left Download live on an installed entry, offering to re-fetch
    /// 1.34 GiB over a file the store already has — beside a Remove button that was correctly
    /// disabled on the opposite condition. A cancelled download does not set
    /// <see cref="IsInstalled"/>, so the resume path keeps its enabled button.
    /// </summary>
    public bool CanDownload => !IsBusy && !IsInstalled;
}

public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly IModelStore _store;
    private readonly ModelCatalog _catalog;
    private readonly Func<ModelInstaller> _installerFactory;
    private readonly ModelSession? _session;
    private readonly Func<ComputeBackend>? _backend;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoad))]
    [NotifyPropertyChangedFor(nameof(CanUnload))]
    private ModelViewModel? _selected;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _allowUnverified;

    /// <summary>
    /// Blocks load and unload while a transcription is in flight, because the running batch is
    /// holding the very engine an unload would dispose.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoad))]
    [NotifyPropertyChangedFor(nameof(CanUnload))]
    private bool _isTranscribing;

    public ModelsViewModel(
        IModelStore store,
        ModelCatalog catalog,
        Func<ModelInstaller>? installerFactory = null,
        ModelSession? session = null,
        Func<ComputeBackend>? backend = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);

        _store = store;
        _catalog = catalog;
        _installerFactory = installerFactory ?? (() => new ModelInstaller(store));
        _session = session;
        _backend = backend;

        Models = [.. catalog.Models.Select(m => new ModelViewModel(m, store.IsInstalled(m)))];
        Selected = Models.FirstOrDefault(m => m.IsInstalled) ?? Models.FirstOrDefault();

        if (_session is not null)
        {
            _session.Changed += (_, _) => SyncSession();
        }
    }

    public ObservableCollection<ModelViewModel> Models { get; }

    public string ModelDirectory => _store.RootDirectory;

    /// <summary>The CC BY 4.0 notice, rendered in the application rather than only in a repo file.</summary>
    public string Attribution => string.Join(
        Environment.NewLine,
        Attributions.ById.Values.Select(a => a.ToPlainText(Environment.NewLine)));

    public ModelDescriptor? SelectedDescriptor => Selected?.Descriptor;

    /// <summary>
    /// What is in memory right now, in one line, naming the backend the engine reported rather
    /// than the one that was requested. Those differ when the native loader falls back, and the
    /// difference is the whole reason somebody asks whether a run used the GPU.
    /// </summary>
    public string LoadedSummary
    {
        get
        {
            if (_session is null)
            {
                return "No model session in this window.";
            }

            if (_session.IsBusy)
            {
                return "Loading…";
            }

            if (!_session.IsLoaded)
            {
                return "Nothing loaded. Choose a model and press Load before transcribing.";
            }

            var name = _session.Model?.DisplayName ?? "a model";
            var backend = _session.LoadedBackend?.ToString().ToLowerInvariant() ?? "unknown backend";
            var took = _session.LoadDuration is { } d
                ? string.Create(CultureInfo.InvariantCulture, $", loaded in {d.TotalSeconds:0.0} s")
                : string.Empty;

            var fellBack = _session.RequestedBackend is { } requested && _session.LoadedBackend != requested
                ? $"  ⚠ {requested.ToString().ToLowerInvariant()} was requested — the native loader fell back."
                : string.Empty;

            return $"Loaded: {name} on {backend}{took}.{fellBack}";
        }
    }

    public bool IsLoaded => _session?.IsLoaded ?? false;

    public bool IsSessionBusy => _session?.IsBusy ?? false;

    /// <summary>
    /// The backend cannot change after the first load in a process, so the window says that rather
    /// than offering a control that silently does nothing.
    /// </summary>
    public string BackendNote => _session?.IsBackendFixed == true
        ? "The backend is fixed for this process once a model has loaded — restart to change it."
        : "Choose the backend before loading. It cannot be changed again without restarting.";

    public bool CanLoad =>
        _session is not null && !_session.IsBusy && !IsTranscribing && Selected is { IsInstalled: true };

    public bool CanUnload => _session is { IsLoaded: true, IsBusy: false } && !IsTranscribing;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_session is null || !CanLoad || Selected is not { } model)
        {
            return;
        }

        StatusMessage = null;

        try
        {
            await _session.LoadAsync(new EngineSelection
            {
                Backend = _backend?.Invoke() ?? ComputeBackend.Vulkan,
                Model = model.Descriptor,
            }).ConfigureAwait(true);

            StatusMessage = _session.IsLoaded ? null : "The model did not load.";
        }
#pragma warning disable CA1031 // A load failure belongs on screen next to the button that caused it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task UnloadAsync()
    {
        if (_session is null || !CanUnload)
        {
            return;
        }

        await _session.UnloadAsync().ConfigureAwait(true);
        StatusMessage = "Unloaded. The weights are out of memory.";
    }

    /// <summary>Mirrors the session onto the observable surface the window binds to.</summary>
    private void SyncSession()
    {
        var loadedId = _session?.Model?.Id;

        foreach (var model in Models)
        {
            model.IsLoaded = loadedId is not null && model.Id == loadedId;
        }

        OnPropertyChanged(nameof(LoadedSummary));
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsSessionBusy));
        OnPropertyChanged(nameof(BackendNote));
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(CanUnload));
    }

    [RelayCommand]
    private void CancelDownload() => _cancellation?.Cancel();

    [RelayCommand]
    private async Task DownloadAsync()
    {
        // The same condition the button binds to, so a command reached any other way — a keyboard
        // accelerator, a test, a future context menu — cannot do what the disabled button will not.
        if (Selected is not { } model || !model.CanDownload)
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

        // Deleting the file under a loaded engine leaves the window claiming a model is resident
        // while its weights are gone from disk — recoverable only by noticing, which is the kind of
        // quiet inconsistency this application exists to not produce.
        if (model.IsLoaded)
        {
            StatusMessage = "That model is loaded. Unload it first, then remove it.";
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
