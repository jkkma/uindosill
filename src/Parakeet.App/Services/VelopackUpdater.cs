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
        : this(ForThisBuild(repositoryUrl))
    {
    }

    /// <summary>
    /// A source that searches the same train this build is on: a prerelease looks for prereleases,
    /// a stable build looks only for stable ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GithubSource</c>'s flag is documented as "if true, pre-releases will be also be searched /
    /// downloaded. If false, only stable releases will be considered", and it is not a preference
    /// but the difference between a working update check and one that cannot succeed. Every release
    /// this project has published so far carries a hyphen in its version, and GitHub marks each of
    /// them a prerelease accordingly, so a fixed <c>false</c> searches a set that is empty by
    /// construction.
    /// </para>
    /// <para>
    /// A fixed <c>true</c> is the other half of the same mistake, deferred: it would offer the next
    /// release candidate to somebody who chose to install 1.0.0, which is exactly what marking rcs
    /// as prereleases exists to prevent. Deciding from the running version keeps each user on the
    /// train they boarded.
    /// </para>
    /// <para>
    /// The manager is built twice on a prerelease build, which is free: <c>CurrentVersion</c> reads
    /// the installed package's own metadata and touches no network, and the first instance is
    /// discarded before any request is made.
    /// </para>
    /// </remarks>
    private static UpdateManager ForThisBuild(string repositoryUrl)
    {
        var stable = new UpdateManager(Source(repositoryUrl, prerelease: false));

        return TracksPrereleases(stable.CurrentVersion?.ToString())
            ? new UpdateManager(Source(repositoryUrl, prerelease: true))
            : stable;
    }

    private static GithubSource Source(string repositoryUrl, bool prerelease) =>
        new(repositoryUrl, accessToken: null, prerelease: prerelease);

    /// <summary>
    /// Whether a version string names a prerelease, read off the string rather than off a version
    /// type so that it cannot drift with the one Velopack happens to return.
    /// </summary>
    /// <remarks>
    /// SemVer puts the prerelease label after a hyphen and build metadata after a plus, and only
    /// the first of those decides the train. <c>1.0.0+5fb4a10</c> is a stable build with a commit
    /// stamped on it, and reading the hyphen out of the metadata rather than the version would put
    /// it on the wrong one. A build that is not installed has no version and tracks stable, which
    /// is what a run from source did before any of this existed.
    /// </remarks>
    internal static bool TracksPrereleases(string? currentVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return false;
        }

        var metadata = currentVersion.IndexOf('+', StringComparison.Ordinal);
        var core = metadata < 0 ? currentVersion : currentVersion[..metadata];

        return core.Contains('-', StringComparison.Ordinal);
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
