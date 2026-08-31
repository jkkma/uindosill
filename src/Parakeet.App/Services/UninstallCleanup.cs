using System.Diagnostics;
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
/// are or what they were for. This class is the other half of the bargain. <c>Program.cs</c> runs
/// <see cref="Run"/> in the Velopack before-uninstall hook behind <c>UninstallPrompt.Ask</c>, so
/// the data dies with the product only on an explicit Yes — the shipped answer on dismissal,
/// timeout, or a desktop with nobody at it is Keep (the decision of 2026-08-29; the guards below
/// apply either way) — rather than by an installer deleting whatever directory shares its name.
/// </para>
/// <para>
/// Best effort by design, and out of the way by necessity. The hook runs inside Velopack's
/// 30-second fast-callback budget and none of the uninstall's remaining steps begin until it
/// returns, so the walk itself is handed to a detached command rather than run here — see
/// <see cref="Run"/> for what overrunning that budget actually costs. A file that will not delete,
/// open in another process or with permissions gone strange, is left behind rather than argued
/// with, and everything around it still goes. Nothing here throws.
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
    /// <summary>
    /// Hands the shipping data directory to a delete that outlives this process. Called from the
    /// uninstall hook only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The delete deliberately does not run here.</b> The hook runs inside Velopack's 30-second
    /// fast-callback budget and this directory is measured in gigabytes: the CUDA pack alone
    /// unpacks to 2.8 GB of small files. A recursive delete that overruns the budget is killed
    /// <i>part-way</i>, and because the callback never returns, every step of the uninstall that
    /// follows it — the shortcuts, the install directory, the registry entry — never runs either.
    /// The application is left installed, and the next attempt does the same to what remains. That
    /// is not a slow uninstall, it is one that cannot finish.
    /// </para>
    /// <para>
    /// So the guards run here, where they are arithmetic over a path and cost nothing, and the walk
    /// is handed to a detached <c>cmd.exe</c>. Velopack removes its own install directory in exactly
    /// this way and for a neighbouring reason: a process cannot delete the directory it is running
    /// from, and this one cannot afford to wait for a directory this size.
    /// </para>
    /// <para>
    /// It still fails towards keeping. If the detached command never starts, or is killed with the
    /// process, the files stay — which is what an uninstall did before any of this existed.
    /// </para>
    /// </remarks>
    public static void Run()
    {
        try
        {
            var target = ResolvedTarget(
                UserDataPaths.RootDirectory(),
                PackagingIdentity.InstallRootDirectory());

            if (target is null)
            {
                return;
            }

            // A junctioned root is unlinked rather than followed, and that is one call rather than
            // a walk. There is no budget to blow in removing a link, so it stays here.
            if ((target.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                target.Delete();
                return;
            }

            ScheduleDelete(target.FullName);
        }
        catch
        {
            // Nothing under this directory is worth failing an uninstall over.
        }
    }

    /// <summary>
    /// The directory this is allowed to delete, or <c>null</c> where deleting would be obviously
    /// wrong.
    /// </summary>
    /// <remarks>
    /// Each guard covers a real mistake rather than a hypothetical one. A root whose last segment
    /// is not <see cref="UserDataPaths.DirectoryName"/> means a refactor or an override pointed
    /// this delete somewhere it was never meant to go, and the answer to that is to delete nothing.
    /// An install root inside the data root would mean taking the running uninstaller with it.
    /// Both are cheap enough to run before anything is scheduled, which is what lets the walk
    /// itself happen somewhere else.
    /// </remarks>
    internal static DirectoryInfo? ResolvedTarget(string userDataRoot, string installRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDataRoot));
        if (!string.Equals(Path.GetFileName(root), UserDataPaths.DirectoryName, StringComparison.Ordinal))
        {
            return null;
        }

        var directory = new DirectoryInfo(root);
        if (!directory.Exists)
        {
            return null;
        }

        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        return IsUnderOrEqual(install, root) ? null : directory;
    }

    /// <summary>
    /// Starts a detached command that removes <paramref name="root"/> once this process is gone,
    /// and returns without waiting for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The short wait is what makes it detached in practice rather than only in principle: the
    /// uninstaller is still working when this returns. Three seconds is Velopack's own choice for
    /// the equivalent step against its install directory.
    /// </para>
    /// <para>
    /// <c>rd /s /q</c> rather than <see cref="DeleteWhatCanBe"/>'s walk, because a detached command
    /// cannot report what it stranded and there would be nobody left to tell. The property that
    /// mattered survives: what will not delete is left behind and the rest still goes.
    /// </para>
    /// <para>
    /// The path is quoted and is this application's own — <c>LocalApplicationData</c> plus a
    /// constant — and Windows forbids a quote inside a path, so there is nothing here for one to
    /// close early.
    /// </para>
    /// </remarks>
    internal static string DeleteCommandFor(string root) =>
        $"/C choice /C Y /N /D Y /T 3 >nul & rd /s /q \"{root}\"";

    private static void ScheduleDelete(string root)
    {
        using var scheduled = Process.Start(new ProcessStartInfo("cmd.exe", DeleteCommandFor(root))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }

    /// <summary>
    /// Deletes <paramref name="userDataRoot"/> recursively and in this process, unless doing so is
    /// obviously wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the synchronous form, and the uninstall hook does not use it.</b> <see cref="Run"/>
    /// schedules the same removal instead, for the budget reason recorded there. What this keeps is
    /// the definition: the same guards through <see cref="ResolvedTarget"/>, a junction unlinked
    /// rather than followed, and an entry-by-entry walk in which one locked file strands only
    /// itself. It is what the tests hold down, and it is the behaviour the detached command has to
    /// match, so a change to one is a question about the other.
    /// </para>
    /// <para>
    /// A root that is itself a reparse point — someone short of disk junctioning the whole folder
    /// onto another drive — is unlinked, not followed: the link is this product's; whatever it
    /// points at is not.
    /// </para>
    /// </remarks>
    public static void DeleteUserData(string userDataRoot, string installRoot)
    {
        try
        {
            var directory = ResolvedTarget(userDataRoot, installRoot);
            if (directory is null)
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
