using Parakeet.App.Services;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

/// <summary>
/// The uninstall hook's delete, held to its guards.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UninstallCleanup"/> is the one piece of this product that deletes a directory it
/// did not create this run, on a machine it will never see again, with nobody watching. Every
/// guard it claims is exercised here against a rebuilt tree rather than trusted from its
/// comments, for the same reason <see cref="PackagingIdentityTests"/> runs the installer's real
/// recursive delete instead of comparing strings.
/// </para>
/// <para>
/// Two of these tests are halves of a platform pair, like the Media Foundation pair in
/// <c>Parakeet.Audio.Tests</c>: the locked-file test needs Windows sharing semantics, and the
/// link test needs a platform where creating one requires no privilege. Each skips where the
/// other runs, so the suite's skip count stays the same number on every machine — which is what
/// lets a document CI checks quote it.
/// </para>
/// </remarks>
public class UninstallCleanupTests
{
    [Fact]
    public void TheDataDirectoryGoesWholeAndItsNeighbourStays()
    {
        var root = NewScratchRoot();
        try
        {
            // The tree the application actually leaves behind: weights, settings, and an unpacked
            // bundle — including a read-only file, because archives ship those and a plain delete
            // refuses them on Windows.
            var data = Path.Combine(root, UserDataPaths.DirectoryName);
            var weights = Path.Combine(data, "models", "parakeet-tdt-0.6b-v3-f16.gguf");
            var settings = Path.Combine(data, "settings.json");
            var readOnly = Path.Combine(data, "python", "lib", "frozen.pyd");

            Directory.CreateDirectory(Path.GetDirectoryName(weights)!);
            Directory.CreateDirectory(Path.GetDirectoryName(readOnly)!);
            File.WriteAllText(weights, "weights");
            File.WriteAllText(settings, "{}");
            File.WriteAllText(readOnly, "frozen");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);

            // The install root beside it, as on a real machine — the delete must not wander.
            var install = Path.Combine(root, "UindosillDesktop");
            Directory.CreateDirectory(install);
            var updater = Path.Combine(install, "Update.exe");
            File.WriteAllText(updater, "stub");

            UninstallCleanup.DeleteUserData(data, install);

            Assert.False(Directory.Exists(data), "The data directory survived its own uninstall.");
            Assert.True(File.Exists(updater), "The delete crossed into the install root.");
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    [Fact]
    public void ADirectoryNotNamedForTheProductIsRefused()
    {
        // The path arrives through UserDataPaths today and through whatever a refactor makes of it
        // tomorrow. A last segment that is not the product's directory name means the delete is
        // pointed somewhere it was never meant to go, and the answer is to delete nothing at all.
        var root = NewScratchRoot();
        try
        {
            var data = Path.Combine(root, "Documents");
            var hostage = Path.Combine(data, "thesis.docx");
            Directory.CreateDirectory(data);
            File.WriteAllText(hostage, "irreplaceable");

            UninstallCleanup.DeleteUserData(data, Path.Combine(root, "UindosillDesktop"));

            Assert.True(File.Exists(hostage), "A directory that is not this product's was deleted.");
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    [Fact]
    public void AnInstallRootInsideTheDataDirectoryStopsTheDelete()
    {
        // The disjointness of the two roots is held by PackagingIdentityTests at build time, but
        // this code runs on machines where the environment decides the paths. If they ever nest,
        // deleting the data root would take the running uninstaller with it — so nothing happens.
        var root = NewScratchRoot();
        try
        {
            var data = Path.Combine(root, UserDataPaths.DirectoryName);
            var install = Path.Combine(data, "app");
            Directory.CreateDirectory(install);
            var weights = Path.Combine(data, "models", "weights.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(weights)!);
            File.WriteAllText(weights, "weights");

            UninstallCleanup.DeleteUserData(data, install);

            Assert.True(File.Exists(weights), "The delete proceeded around an install root nested inside it.");
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    [Fact]
    public void AMissingDirectoryIsNothingToDoRatherThanSomethingToCreate()
    {
        var root = NewScratchRoot();
        try
        {
            var data = Path.Combine(root, UserDataPaths.DirectoryName);

            UninstallCleanup.DeleteUserData(data, Path.Combine(root, "UindosillDesktop"));

            // UserDataPaths.RootDirectory resolves its special folder with Create, so an uninstall
            // on a machine that never downloaded anything must not end by planting the directory.
            Assert.False(Directory.Exists(data), "Cleaning up a directory that was not there created it.");
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    [Fact]
    public void ALockedFileStrandsOnlyItself()
    {
        // Windows half of the platform pair: POSIX deletes open files without complaint, so only
        // Windows can show a lock stranding — or failing to strand — its neighbours.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete an open file.");

        var root = NewScratchRoot();
        try
        {
            var data = Path.Combine(root, UserDataPaths.DirectoryName);
            var weights = Path.Combine(data, "models", "weights.gguf");
            var settings = Path.Combine(data, "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(weights)!);
            File.WriteAllText(weights, "weights");
            File.WriteAllText(settings, "{}");

            using (new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                UninstallCleanup.DeleteUserData(data, Path.Combine(root, "UindosillDesktop"));
            }

            // The open file stays, and so must its chain of parents — but nothing else. The whole
            // point of deleting entry by entry is that a held settings.json cannot strand four
            // gigabytes of weights beside it.
            Assert.True(File.Exists(settings), "An open file was deleted out from under its handle.");
            Assert.False(File.Exists(weights), "One locked file stopped the rest of the cleanup.");
            Assert.False(Directory.Exists(Path.Combine(data, "models")), "An emptied subdirectory was left standing.");
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    [Fact]
    public void ALinkInsideIsUnlinkedWithoutEmptyingItsTarget()
    {
        // Non-Windows half of the platform pair: creating a symbolic link needs no privilege here,
        // where on Windows it takes developer mode — a machine-dependent skip, which is the thing
        // a checked count cannot contain. The code under test is the same attribute check and the
        // same Delete() on every platform.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a link on Windows requires developer mode.");

        var root = NewScratchRoot();
        try
        {
            // Someone short of disk points models at a bigger drive. Uninstall must remove the
            // link — it is this product's — and not one byte of what it points at.
            var elsewhere = Path.Combine(root, "big-drive", "models");
            var weights = Path.Combine(elsewhere, "weights.gguf");
            Directory.CreateDirectory(elsewhere);
            File.WriteAllText(weights, "weights");

            var data = Path.Combine(root, UserDataPaths.DirectoryName);
            Directory.CreateDirectory(data);
            Directory.CreateSymbolicLink(Path.Combine(data, "models"), elsewhere);

            UninstallCleanup.DeleteUserData(data, Path.Combine(root, "UindosillDesktop"));

            Assert.False(Directory.Exists(data), "The data directory survived because of the link inside it.");
            Assert.True(File.Exists(weights), "The delete followed a link and emptied its target.");
        }
        finally
        {
            DeleteScratchRoot(root);
        }
    }

    private static string NewScratchRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "uindosill-cleanup-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteScratchRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        // The read-only file from the first test refuses a recursive delete if it survived.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    // ---- The walk that deliberately does not happen in this process ---------------------------

    [Fact]
    public void TheScheduledCommandRemovesExactlyTheDirectoryTheGuardsAllowed()
    {
        // Run no longer walks the tree itself: it resolves a target and hands it to a detached
        // command. So what has to be held down is that the command names the directory the guards
        // approved, quoted, and nothing above it.
        var root = TestTemp.NewDirectory("uindosill-scheduled");
        var data = Path.Combine(root, UserDataPaths.DirectoryName);
        Directory.CreateDirectory(data);

        var target = UninstallCleanup.ResolvedTarget(data, Path.Combine(root, "UindosillDesktop"));

        Assert.NotNull(target);
        var command = UninstallCleanup.DeleteCommandFor(target!.FullName);

        Assert.Contains("\"" + target.FullName + "\"", command, StringComparison.Ordinal);
        Assert.Contains("rd /s /q", command, StringComparison.Ordinal);
        Assert.DoesNotContain("\"" + root + "\"", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScheduledCommandWaitsForTheUninstallerToBeGone()
    {
        // Three seconds, which is Velopack's own choice for the delayed removal of its install
        // directory. The uninstaller is still working when the hook returns, and starting a walk
        // at that moment buys a race for nothing.
        var command = UninstallCleanup.DeleteCommandFor(Path.Combine("C:", "x", "Uindosill"));

        Assert.Contains("/T 3", command, StringComparison.Ordinal);
    }

    [Fact]
    public void AGuardThatRefusesLeavesNothingToSchedule()
    {
        // The guards run before anything is scheduled rather than inside the command, because a
        // detached shell cannot be told to reconsider. A refused target is a null, and that null is
        // what stops Run scheduling anything at all.
        var root = TestTemp.NewDirectory("uindosill-refused");
        var wrongName = Path.Combine(root, "NotTheProduct");
        Directory.CreateDirectory(wrongName);

        Assert.Null(UninstallCleanup.ResolvedTarget(wrongName, Path.Combine(root, "UindosillDesktop")));
        Assert.Null(UninstallCleanup.ResolvedTarget(Path.Combine(root, "Uindosill"), root));
    }
}
