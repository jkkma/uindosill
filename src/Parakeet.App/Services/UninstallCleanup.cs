using Parakeet.Core.Models;

namespace Parakeet.App.Services;

/// <summary>
/// Removes <c>%LOCALAPPDATA%\Uindosill</c> — the models, the settings file, the Python bundle —
/// when the application is uninstalled.
/// </summary>
/// <remarks>
/// <para>
/// This product's data deliberately lives outside the install root so that updates cannot destroy
/// it and Velopack's own recursive uninstall delete cannot reach it — <c>docs/GOTCHAS.md</c>
/// gotcha 8. The price of that separation is that nothing else ever removes the data: an
/// uninstall would leave gigabytes of weights behind with no application left to say where they
/// are or what they were for. This class is the other half of the bargain. <c>Program.cs</c>
/// registers <see cref="Run"/> as the Velopack before-uninstall hook, so the data dies when the
/// product does — but by this product's decision, behind guards, rather than by an installer
/// deleting whatever directory shares its name.
/// </para>
/// <para>
/// Best effort by design. The hook runs inside Velopack's 30-second fast-callback budget, and the
/// uninstall must finish whatever happens here, so a file that will not delete — open in another
/// process, permissions gone strange — is left behind rather than argued with, and everything
/// around it is still removed. Nothing here throws.
/// </para>
/// <para>
/// Only the canonical directory is removed. A models directory redirected with
/// <c>UINDOSILL_MODELS_DIR</c>, or a settings file moved with <c>UINDOSILL_SETTINGS_PATH</c>, is
/// the user's own arrangement in a location of their choosing, and this code has no business
/// deleting a directory it did not name.
/// </para>
/// <para>
/// One consequence is accepted rather than overlooked: the directory is shared with the CLI,
/// which ships as a zip Velopack knows nothing about, so uninstalling the desktop application
/// takes the CLI's models and its downloaded Python bundle too. Sparing them would mean leaving
/// gigabytes behind for everyone to protect an arrangement only some have — the orphan problem
/// this class exists to end — and the recovery is the same download that created them.
/// <c>docs/GOTCHAS.md</c> gotcha 8 records the decision.
/// </para>
/// </remarks>
public static class UninstallCleanup
{
    /// <summary>Deletes the shipping data directory. Called from the uninstall hook only.</summary>
    public static void Run() =>
        DeleteUserData(UserDataPaths.RootDirectory(), PackagingIdentity.InstallRootDirectory());

    /// <summary>
    /// Deletes <paramref name="userDataRoot"/> recursively, unless doing so is obviously wrong.
    /// </summary>
    /// <remarks>
    /// Each guard covers a real mistake rather than a hypothetical one. A root whose last segment
    /// is not <see cref="UserDataPaths.DirectoryName"/> means a refactor or an override pointed
    /// this delete somewhere it was never meant to go, and the answer to that is to delete
    /// nothing. An install root inside the data root would mean taking the running uninstaller
    /// with it. And a root that is itself a reparse point — someone short of disk junctioning the
    /// whole folder onto another drive — is unlinked, not followed: the link is this product's;
    /// whatever it points at is not.
    /// </remarks>
    public static void DeleteUserData(string userDataRoot, string installRoot)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDataRoot));
            if (!string.Equals(Path.GetFileName(root), UserDataPaths.DirectoryName, StringComparison.Ordinal))
            {
                return;
            }

            var directory = new DirectoryInfo(root);
            if (!directory.Exists)
            {
                return;
            }

            var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
            if (IsUnderOrEqual(install, root))
            {
                return;
            }

            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                directory.Delete();
                return;
            }

            DeleteWhatCanBe(directory);
        }
        catch
        {
            // Nothing under this directory is worth failing an uninstall over.
        }
    }

    /// <summary>
    /// The recursive delete, one entry at a time so a single locked file strands only itself.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Directory.Delete(string, bool)"/>: that throws at the first entry it cannot
    /// remove and abandons the rest, which for this directory would mean an open
    /// <c>settings.json</c> stranding four gigabytes of weights beside it. Reparse points are
    /// deleted as entries, never entered — the same rule as at the root, one level down.
    /// </remarks>
    private static void DeleteWhatCanBe(DirectoryInfo directory)
    {
        try
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo child && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    DeleteWhatCanBe(child);
                    continue;
                }

                try
                {
                    // Downloads are never read-only, but an unpacked archive can be, and a
                    // read-only file refuses a plain delete on Windows.
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                    entry.Delete();
                }
                catch
                {
                    // Locked or unreachable; leave it and keep going.
                }
            }
        }
        catch
        {
            // The directory would not enumerate; the attempt on the directory itself is below.
        }

        try
        {
            directory.Delete();
        }
        catch
        {
            // Not empty, because something above survived. It stays, holding what it holds.
        }
    }

    private static bool IsUnderOrEqual(string candidate, string ancestor)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(candidate, ancestor, comparison)
            || candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, comparison);
    }
}
