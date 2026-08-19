namespace Parakeet.App.Services;

/// <summary>
/// The updater for a copy that no installer put there: a build from source, the CLI zip unpacked by
/// hand, the visual designer, or a headless test run.
/// </summary>
/// <remarks>
/// It is the default rather than an opt-in, so that nothing which merely constructs the window can
/// reach the network. <c>Program.Main</c> — the only entry point an installed copy has — replaces it
/// with the real one.
/// </remarks>
public sealed class NotInstalledUpdater : IAppUpdater
{
    public bool IsInstalled => false;

    public string CurrentVersion => "not installed";

    public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task DownloadAsync(IProgress<int>? progress, CancellationToken ct) => Task.CompletedTask;

    public void ApplyAndRestart() =>
        throw new InvalidOperationException("This copy was not installed, so there is nothing to update.");
}
