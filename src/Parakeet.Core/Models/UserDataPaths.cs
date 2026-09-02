namespace Parakeet.Core.Models;

/// <summary>
/// The one place this product keeps files that belong to the user rather than to the build.
/// </summary>
/// <remarks>
/// <para>
/// There is exactly one copy of this path because there are two things under it that must agree:
/// the model weights (700 MB to 1.3 GiB, and a further ~474 MB once the diariser ships) and the
/// application's own settings. Both have to survive an update, and the way they survive is by not
/// being inside the install directory — see <c>docs/GOTCHAS.md</c> gotcha 8. They survive an
/// uninstall for the same reason, and that is now a decision rather than a side effect: an
/// uninstall hook that deleted this directory shipped for one night and was withdrawn, because
/// nothing this product does unattended may delete a user's files.
/// </para>
/// <para>
/// This is <b>not</b> the Velopack install root, and it must never become it. Velopack installs a
/// Windows application under <c>%LOCALAPPDATA%\&lt;package id&gt;</c> and its uninstall deletes that
/// directory recursively, so a package id equal to the last segment here would hand this
/// directory's fate to that guardless delete — and to every update's rewriting of the install
/// tree besides. <c>PackagingIdentity</c> holds the id that keeps them apart, and a test holds
/// the two against each other.
/// </para>
/// </remarks>
public static class UserDataPaths
{
    /// <summary>
    /// The single directory name this product owns under local application data.
    /// </summary>
    /// <remarks>
    /// Declared as a constant on its own line because a second reader outside the compiler needs
    /// it: <c>scripts/package-windows.ps1</c> reads this literal out of this file to check that the
    /// Velopack package id is not the same name, and refuses to build an installer if it is. That
    /// is a strange thing for a script to do, and the alternative was worse — a second copy of the
    /// name in the script, which would go stale exactly when it mattered and fail open.
    /// </remarks>
    public const string DirectoryName = "Uindosill";

    /// <summary>Environment variable that overrides the location, for portable installs and tests.</summary>
    /// <remarks>
    /// The models directory and the settings file have had one of these for as long as the suite
    /// has needed to stay out of the user's files; everything else under this root had none, so a
    /// caller that asked this class rather than one of those two reached the real profile whatever
    /// a test had redirected. That was measured rather than reasoned about on 2026-09-01: the App
    /// suite, on a machine with the cuda natives vendored and an NVIDIA driver, opened a window per
    /// test and downloaded 122 MB of the CUDA pack into the maintainer's own
    /// <c>%LOCALAPPDATA%\Uindosill</c> — <c>MainWindowViewModel.CudaPackRoot</c> is this method,
    /// and neither redirect covered it. One variable here covers every caller of the one place,
    /// present and future, which is the reasoning <c>TestUserData</c> already gives for redirecting
    /// unconditionally rather than at each call site.
    /// </remarks>
    public const string DirectoryEnvironmentVariable = "UINDOSILL_USER_DATA_DIR";

    /// <summary>
    /// <c>%LOCALAPPDATA%\Uindosill</c> on Windows, and the XDG equivalent elsewhere, always resolved
    /// through the platform API so redirected and roaming profiles keep working — never a hardcoded
    /// <c>%USERPROFILE%\.cache</c>, which is how managed Windows fleets get broken. Overridden
    /// whole by <see cref="DirectoryEnvironmentVariable"/>, which the models directory and the
    /// settings file then follow, because both are defined against this.
    /// </summary>
    public static string RootDirectory()
    {
        if (Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is { Length: > 0 } redirected)
        {
            return redirected;
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrEmpty(localAppData))
        {
            // A profile with no local application data is broken, but falling back to the
            // current directory would scatter 670 MB blobs wherever the app happened to start.
            return Path.Combine(Path.GetTempPath(), DirectoryName);
        }

        return Path.Combine(localAppData, DirectoryName);
    }
}
