using Velopack;
using Velopack.Sources;

namespace Parakeet.App.Services;

/// <summary>
/// <see cref="IAppUpdater"/> against the GitHub Releases feed the installer was published to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The channel is not set here, and that is deliberate.</b> Two flavours ship from one publish —
/// the default carries the cpu and vulkan natives, the second adds cuda — and Velopack records the
/// channel a release was packed with, so an installed copy asks for its own flavour without being
/// told. <c>UpdateOptions.ExplicitChannel</c> is documented as "should usually be left null …
/// the default channel will be whatever channel was specified on the command line when building
/// this release … users automatically receive updates from the same channel they installed from"
/// (Velopack 1.2.0 XML docs). Setting it would silently move a CUDA user onto the default flavour
/// and take the runtime away.
/// </para>
/// <para>
/// No access token: the repository is public, and an unauthenticated GitHub API caller gets 60
/// requests an hour by IP. One request per launch is nowhere near that, and a token in a desktop
/// binary is a token anybody can read.
/// </para>
/// </remarks>
public sealed class VelopackUpdater : IAppUpdater
{
    /// <summary>The repository the releases are published to.</summary>
    public const string RepositoryUrl = "https://github.com/jkkma/uindosill";

    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    public VelopackUpdater(string repositoryUrl = RepositoryUrl)
        : this(new UpdateManager(new GithubSource(repositoryUrl, accessToken: null, prerelease: false)))
    {
    }

    internal VelopackUpdater(UpdateManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? "not installed";

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        // CheckForUpdatesAsync takes no cancellation token in 1.2.0, so the token is honoured
        // either side of it rather than inside it: a launch check that is still in flight when the
        // window closes finishes its request and then stops.
        ct.ThrowIfCancellationRequested();

        _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(true);

        ct.ThrowIfCancellationRequested();

        return _pending?.TargetFullRelease?.Version?.ToString();
    }

    public async Task DownloadAsync(IProgress<int>? progress, CancellationToken ct)
    {
        var update = _pending
            ?? throw new InvalidOperationException("Nothing to download: CheckAsync found no newer release.");

        await _manager.DownloadUpdatesAsync(
            update,
            progress is null ? null : progress.Report,
            ct).ConfigureAwait(true);
    }

    public void ApplyAndRestart()
    {
        var update = _pending
            ?? throw new InvalidOperationException("Nothing to apply: CheckAsync found no newer release.");

        _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }
}
