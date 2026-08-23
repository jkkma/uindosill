namespace Parakeet.Core.Models;

/// <summary>
/// The one place this product keeps files that belong to the user rather than to the build.
/// </summary>
/// <remarks>
/// <para>
/// There is exactly one copy of this path because there are two things under it that must agree:
/// the model weights (700 MB to 1.3 GiB, and a further ~474 MB once the diariser ships) and the
/// application's own settings. Both have to survive an update, and the way they survive is by not
/// being inside the install directory — see <c>docs/GOTCHAS.md</c> gotcha 8. An uninstall is the
/// opposite case: this directory goes with the product, but through
/// <c>Parakeet.App.Services.UninstallCleanup</c> and its guards, never as a side effect of where
/// the installer happened to be pointed.
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

    /// <summary>
    /// <c>%LOCALAPPDATA%\Uindosill</c> on Windows, and the XDG equivalent elsewhere, always resolved
    /// through the platform API so redirected and roaming profiles keep working — never a hardcoded
    /// <c>%USERPROFILE%\.cache</c>, which is how managed Windows fleets get broken.
    /// </summary>
    public static string RootDirectory()
    {
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
