using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Parakeet.App.Services;

namespace Parakeet.App.ViewModels;

/// <summary>
/// The update check, the notice it produces, and the one setting that switches it off.
/// </summary>
/// <remarks>
/// <para>
/// The shape is fixed by a decision rather than by taste (<c>docs/PHASES.md</c>, *Decisions taken
/// 2026-08-16*, item 4): one HTTPS request to GitHub Releases at launch, a visible notice when
/// there is something newer, download and restart <b>only</b> on a click, and a setting that turns
/// the check off. Velopack's silent download-and-apply was considered and not chosen, which is why
/// <c>SetAutoApplyOnStartup(false)</c> is in <c>Program</c>: nothing installs itself here.
/// </para>
/// <para>
/// That launch request is the only thing this application does on the network without being asked,
/// and the README and the notice below both say so.
/// </para>
/// </remarks>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly IAppUpdater _updater;
    private readonly AppSettingsStore _settings;
    private readonly Func<Task>? _shutdown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isBusy;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(Notice))]
    private string? _availableVersion;

    private bool _checkOnLaunch;

    /// <summary>
    /// Set once the shutdown has run, so a failure after it can say what state the window is in.
    /// It never goes back to false: the engine cannot be brought back within this process.
    /// </summary>
    private bool _engineWasReleased;

    public UpdatesViewModel(IAppUpdater updater, AppSettingsStore? settings = null, Func<Task>? shutdown = null)
    {
        ArgumentNullException.ThrowIfNull(updater);

        _updater = updater;
        _settings = settings ?? new AppSettingsStore();
        _shutdown = shutdown;
        _checkOnLaunch = _settings.Load().CheckForUpdatesOnLaunch;

        _status = updater.IsInstalled
            ? "No check has run yet."
            : "This copy was not installed by the installer - a build from source, or the zip unpacked by "
              + "hand - so there is nothing here to update. Newer versions are on the releases page.";
    }

    /// <summary>What this build calls itself, shown whether or not anything newer exists.</summary>
    public string CurrentVersion => _updater.CurrentVersion;

    /// <summary>False for a build that did not arrive through the installer.</summary>
    public bool IsSupported => _updater.IsInstalled;

    public bool IsUpdateAvailable => AvailableVersion is not null;

    public bool CanCheck => IsSupported && !IsBusy;

    public bool CanInstall => IsSupported && !IsBusy && IsUpdateAvailable;

    /// <summary>The banner text. Empty when there is nothing to say, which is when it is hidden.</summary>
    public string Notice => AvailableVersion is { } version
        ? $"Version {version} is available. This is {CurrentVersion}."
        : string.Empty;

    /// <summary>
    /// Whether the one unprompted network request happens at all. Written through on every change,
    /// because a setting that is only saved on a clean exit is a setting that does not hold.
    /// </summary>
    public bool CheckOnLaunch
    {
        get => _checkOnLaunch;
        set
        {
            if (!SetProperty(ref _checkOnLaunch, value))
            {
                return;
            }

            // Say so when it did not stick. A read-only profile, a sync agent holding the handle
            // or a second copy of the application writing at the same moment all make this fail,
            // and a switch that silently forgets is worse than one that will not move: the next
            // launch would make the request the user just declined.
            // Update rather than Save: constructing a fresh AppSettings here would write this one
            // field and reset the backend the user chose on the Models tab back to unset.
            if (!_settings.Update(current => current with { CheckForUpdatesOnLaunch = value }))
            {
                Status = $"That setting could not be saved to {_settings.Path}, so it will go back to "
                    + "its previous value the next time Uindosill starts.";
            }
        }
    }

    /// <summary>
    /// The launch check. Does nothing at all when the setting is off or this is not an installed
    /// copy — no request is made, not a request whose answer is discarded.
    /// </summary>
    public async Task CheckOnLaunchAsync(CancellationToken ct = default)
    {
        if (!CheckOnLaunch || !IsSupported)
        {
            return;
        }

        await CheckAsync(ct).ConfigureAwait(true);
    }

    /// <remarks>
    /// No <c>CanExecute</c> on the attribute, and the button binds <see cref="CanCheck"/> to
    /// <c>IsEnabled</c> instead — which is how every other button in this window is gated. A
    /// generated <c>CanExecute</c> is only re-queried when the command is told to, and
    /// <c>[NotifyPropertyChangedFor]</c> does not tell it, so the two together produce a button
    /// that is correct once and then frozen.
    /// </remarks>
    [RelayCommand]
    private Task Check() => CheckAsync(CancellationToken.None);

    private async Task CheckAsync(CancellationToken ct)
    {
        IsBusy = true;
        Status = "Checking for updates…";

        try
        {
            AvailableVersion = await _updater.CheckAsync(ct).ConfigureAwait(true);
            Status = AvailableVersion is null
                ? $"Up to date - this is {CurrentVersion}."
                : Notice;
        }
        catch (OperationCanceledException)
        {
            Status = "The check was cancelled.";
        }
#pragma warning disable CA1031 // A failed update check must never be more than a line of text.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Offline, rate-limited, or GitHub is down. None of that stops the product working.
            Status = $"Could not check for updates: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Download the newer release and restart into it. The click is the consent; nothing before it
    /// touches the installed files.
    /// </summary>
    [RelayCommand]
    private async Task Install()
    {
        if (!CanInstall)
        {
            return;
        }

        IsBusy = true;
        Progress = 0;
        Status = $"Downloading {AvailableVersion}…";

        try
        {
            await _updater.DownloadAsync(
                new Progress<int>(p => Progress = p),
                CancellationToken.None).ConfigureAwait(true);

            Status = "Restarting to finish the update…";

            // Before the process is replaced, everything the window would do on a normal close has
            // to happen: stop a running batch, unload the model, and release the native backend
            // while the GPU driver is still alive. ApplyAndRestart does not come back, so skipping
            // this would reach the native static teardown with a backend resident and abort with
            // 0xC0000409 — gotcha 19, arrived at from a direction the close handler does not cover.
            if (_shutdown is not null)
            {
                await _shutdown().ConfigureAwait(true);
                _engineWasReleased = true;
            }

            _updater.ApplyAndRestart();
        }
#pragma warning disable CA1031 // A failed update leaves the installed copy exactly as it was.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Two different failures, and telling them apart matters to whoever reads this line.
            // Before the shutdown, nothing has changed and the application carries on working.
            // After it, the batch is cancelled, the model is unloaded and the native backend is
            // released — so the window is still up but cannot transcribe, and the only way back is
            // to close and reopen it. Reporting that as "the update could not be installed" would
            // leave someone pressing Start on a dead engine.
            Status = _engineWasReleased
                ? $"The update could not be applied: {ex.Message}. This copy has already shut its "
                  + "engine down to make way for it, so close Uindosill and open it again before "
                  + "transcribing anything."
                : $"The update could not be installed: {ex.Message}. Nothing was changed.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
