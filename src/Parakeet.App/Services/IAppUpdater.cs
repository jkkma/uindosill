namespace Parakeet.App.Services;

/// <summary>
/// Whether a newer build of this application exists, and the two steps that install one.
/// </summary>
/// <remarks>
/// <para>
/// A port rather than a direct call into Velopack, for one reason that matters and one that does
/// not. The one that matters: an update check reaches the network, and every test in this
/// repository runs with no network — so the window must be drivable with this replaced. The one
/// that does not: it would let the updater be swapped, which nobody intends to do.
/// </para>
/// <para>
/// Stateful on purpose. <see cref="CheckAsync"/> remembers what it found, and the two steps after
/// it act on that; the sequence is exactly the one the user is offered — check, then a click to
/// download, then a restart — and passing the found release back in through the interface would
/// only mean putting a Velopack type in it.
/// </para>
/// </remarks>
public interface IAppUpdater
{
    /// <summary>
    /// False when this build did not arrive through the installer — a developer's <c>dotnet run</c>,
    /// or the CLI zip unpacked by hand. Nothing below may be called then; there is no install for
    /// an update to replace.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>What this build calls itself. Shown whether or not an update exists.</summary>
    string CurrentVersion { get; }

    /// <summary>
    /// One HTTPS request. Returns the newer version's number, or null when this build is current.
    /// </summary>
    Task<string?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads what <see cref="CheckAsync"/> last found. Progress is 0 to 100.</summary>
    Task DownloadAsync(IProgress<int>? progress, CancellationToken ct);

    /// <summary>
    /// Applies the downloaded release and restarts. This does not return: the process is replaced.
    /// Anything that has to happen before the application exits has to have happened already.
    /// </summary>
    void ApplyAndRestart();
}
