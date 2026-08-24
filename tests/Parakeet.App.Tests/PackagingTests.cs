using Parakeet.App.Services;
using Parakeet.Core.Models;

namespace Parakeet.App.Tests;

/// <summary>
/// The one thing about packaging that can destroy a user's data, held down by a test rather than
/// by a comment.
/// </summary>
/// <remarks>
/// Velopack installs a Windows application under <c>%LOCALAPPDATA%\{package id}</c> and its
/// uninstall deletes that directory recursively, with no exclusion for anything an application put
/// inside it. This product downloads 700 MB to 1.3 GiB of weights into
/// <c>%LOCALAPPDATA%\Uindosill\models</c>, and a ~474 MB diariser is joining them. If the package
/// id were "Uindosill", uninstalling would delete all of it, and nothing in the build would say so.
/// Nothing removes that directory now: the uninstall hook that did was withdrawn on 2026-08-23
/// (<c>docs/PHASES.md</c>), so this separation is once again the only thing standing between an
/// uninstall and a user's weights.
/// </remarks>
public class PackagingIdentityTests
{
    [Fact]
    public void ThePackageIdIsNotTheNameOfTheDataDirectory()
    {
        var dataDirectoryName = Path.GetFileName(UserDataPaths.RootDirectory());

        Assert.False(
            string.Equals(PackagingIdentity.PackageId, dataDirectoryName, StringComparison.OrdinalIgnoreCase),
            $"The Velopack package id is '{PackagingIdentity.PackageId}' and the data directory is "
            + $"'{dataDirectoryName}'. Equal means uninstall deletes every downloaded model.");
    }

    [Fact]
    public void ThePackageIdIsASingleDirectoryName()
    {
        // A separator, a drive or a traversal in the id would put the install root somewhere other
        // than the directory this file reasons about, and every claim below with it.
        var id = PackagingIdentity.PackageId;

        Assert.NotEmpty(id);
        Assert.Equal(id, Path.GetFileName(id));
        Assert.DoesNotContain("..", id, StringComparison.Ordinal);
        Assert.False(Path.IsPathRooted(id));
    }

    [Fact]
    public void TheInstallRootAndTheDataRootAreDisjointDirectories()
    {
        var install = Normalise(PackagingIdentity.InstallRootDirectory());
        var data = Normalise(UserDataPaths.RootDirectory());
        var models = Normalise(LocalModelStore.DefaultRootDirectory());

        Assert.False(IsUnderOrEqual(data, install), $"'{data}' is inside the install root '{install}'.");
        Assert.False(IsUnderOrEqual(models, install), $"'{models}' is inside the install root '{install}'.");

        // The other direction matters too: an install root inside the data directory would be
        // wiped by anything this product ever does to its own data folder.
        Assert.False(IsUnderOrEqual(install, data), $"The install root '{install}' is inside '{data}'.");
    }

    [Fact]
    public void ARecursiveDeleteOfTheInstallRootLeavesTheModelsWhereTheyAre()
    {
        // Not an assertion about strings: the two directories are rebuilt under a temporary root
        // using the real path arithmetic, a model file is written, and the delete Velopack's
        // uninstall performs is actually run against the install root.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.False(
            string.IsNullOrEmpty(localAppData),
            "This profile has no local application data directory, so neither path under test is the "
            + "one that ships. Nothing here is proven on such a machine.");

        var installRelative = Path.GetRelativePath(localAppData, PackagingIdentity.InstallRootDirectory());
        var modelsRelative = Path.GetRelativePath(localAppData, LocalModelStore.DefaultRootDirectory());

        var root = Path.Combine(Path.GetTempPath(), "uindosill-uninstall-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var install = Path.Combine(root, installRelative);
            var models = Path.Combine(root, modelsRelative);

            // A plausible install: Velopack's own layout, plus files nothing knows about.
            Directory.CreateDirectory(Path.Combine(install, "current", "native", "win-x64", "cpu"));
            Directory.CreateDirectory(Path.Combine(install, "packages"));
            File.WriteAllText(Path.Combine(install, "Update.exe"), "stub");
            File.WriteAllText(Path.Combine(install, "current", "Uindosill.exe"), "stub");

            Directory.CreateDirectory(models);
            var weights = Path.Combine(models, "parakeet-tdt-0.6b-v3-f16.gguf");
            File.WriteAllText(weights, "1.34 GiB of somebody's afternoon");

            Directory.Delete(install, recursive: true);

            Assert.False(Directory.Exists(install));
            Assert.True(File.Exists(weights), "The installer's own recursive delete took the weights with it.");
            Assert.Equal("1.34 GiB of somebody's afternoon", File.ReadAllText(weights));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ThePackageIdSurvivesTheNormalisationsAnInstallerMightApply()
    {
        // The id reaches a directory name through a chain nobody here controls. Case folding and
        // stripping punctuation are the two plausible transforms; either one landing on the data
        // directory's name would be the same disaster arriving quietly.
        var dataDirectoryName = Path.GetFileName(UserDataPaths.RootDirectory());

        foreach (var candidate in new[]
        {
            PackagingIdentity.PackageId,
            PackagingIdentity.PackageId.ToLowerInvariant(),
            new string(PackagingIdentity.PackageId.Where(char.IsLetterOrDigit).ToArray()),
        })
        {
            Assert.False(
                string.Equals(candidate, dataDirectoryName, StringComparison.OrdinalIgnoreCase),
                $"'{PackagingIdentity.PackageId}' normalises to '{candidate}', which is the data directory.");
        }
    }

    private static string Normalise(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsUnderOrEqual(string candidate, string ancestor)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(candidate, ancestor, comparison)
            || candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, comparison);
    }
}
