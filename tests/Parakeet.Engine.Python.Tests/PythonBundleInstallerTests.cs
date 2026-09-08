using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// Unpacking the shipped bundle archive: where it lands, when it is skipped, and what it refuses.
/// </summary>
/// <remarks>
/// <para>
/// The archive exists because shipping the bundle as 55,256 loose files made every update pay for
/// all of them — a measured 10m 05s for an rc.14 to rc.15 update whose download was three seconds.
/// The tests that matter here are therefore the ones about <i>not</i> working: an unpack that
/// happens twice, or that happens on an update which did not change the bundle, gives the cost
/// straight back.
/// </para>
/// <para>
/// Every test hands over both directories, for the reason <see cref="PythonRuntimeTests"/> gives:
/// a resolver allowed to read the real <c>%LOCALAPPDATA%</c> passes or fails depending on whether
/// whoever ran the suite has a bundle of their own.
/// </para>
/// </remarks>
[Collection("environment")]
public sealed class PythonBundleInstallerTests : IDisposable
{
    private readonly string? _interpreter = Environment.GetEnvironmentVariable(PythonRuntime.InterpreterVariable);
    private readonly string? _packages = Environment.GetEnvironmentVariable(PythonRuntime.PackagesVariable);
    private readonly string? _cudaPack = Environment.GetEnvironmentVariable(PythonRuntime.CudaPackVariable);

    public PythonBundleInstallerTests()
    {
        Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, null);
        Environment.SetEnvironmentVariable(PythonRuntime.PackagesVariable, null);
        Environment.SetEnvironmentVariable(PythonRuntime.CudaPackVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, _interpreter);
        Environment.SetEnvironmentVariable(PythonRuntime.PackagesVariable, _packages);
        Environment.SetEnvironmentVariable(PythonRuntime.CudaPackVariable, _cudaPack);
    }

    /// <summary>
    /// An application directory carrying the archive and its manifest, the way a publish does.
    /// </summary>
    /// <remarks>
    /// Built by actually zipping a directory named <c>python</c> rather than by writing entries by
    /// hand, because the archive's root is load-bearing — it is what lets one artefact serve both
    /// the installer and the hand-unpacked download, and a test that fabricated a rootless zip
    /// would pass while the shipping one failed.
    /// </remarks>
    private static (string AppRoot, string Id) StageArchive(string? extraEntry = null)
    {
        var appRoot = TestTemp.NewDirectory("uindosill-app");
        var source = Path.Combine(TestTemp.NewDirectory("uindosill-src"), "python");

        var interpreter = Path.Combine(source, PythonRuntime.ExecutableName);
        Directory.CreateDirectory(Path.GetDirectoryName(interpreter)!);
        File.WriteAllText(interpreter, "not really an interpreter");
        Directory.CreateDirectory(Path.Combine(source, "uindosill_engines"));
        File.WriteAllText(Path.Combine(source, "uindosill_engines", "serve.py"), "# not really serve");

        var archive = Path.Combine(appRoot, PythonRuntime.ArchiveFileName);
        ZipFile.CreateFromDirectory(source, archive, CompressionLevel.NoCompression, true);

        if (extraEntry is not null)
        {
            using var writable = ZipFile.Open(archive, ZipArchiveMode.Update);
            writable.CreateEntry(extraEntry);
        }

        var id = WriteManifest(appRoot, archive);
        return (appRoot, id);
    }

    /// <summary>The manifest the packaging script writes, computed the same way it computes it.</summary>
    private static string WriteManifest(string appRoot, string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();

        string id;
        using (var stream = File.OpenRead(archive))
        {
            id = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        File.WriteAllText(
            Path.Combine(appRoot, PythonRuntime.ArchiveManifestFileName),
            JsonSerializer.Serialize(new
            {
                id,
                archiveBytes = new FileInfo(archive).Length,
                unpackedBytes = files.Sum(e => e.Length),
                entries = files.Count,
            }));

        return id;
    }

    private static string Interpreter(string bundle) =>
        Path.Combine(bundle, PythonRuntime.ExecutableName);

    [Fact]
    public void TheShippedArchiveIsUnpackedUnderTheUserDataDirectoryAndNamedForItsDigest()
    {
        var (appRoot, id) = StageArchive();
        var userData = TestTemp.NewDirectory("uindosill-data");

        var resolved = PythonBundleInstaller.EnsureUnpacked(
            baseDirectory: appRoot, userDataDirectory: userData);

        var expected = Path.Combine(userData, PythonRuntime.BundleDirectoryName, id);
        Assert.Equal(expected, resolved.PackageRoot);
        Assert.Equal(Interpreter(expected), resolved.Interpreter);
        Assert.Equal(PythonRuntime.BundleSource.Unpacked, resolved.Source);
        Assert.True(Directory.Exists(Path.Combine(expected, "uindosill_engines")));
    }

    /// <summary>
    /// The test the whole change exists for: the second call must not unpack anything again.
    /// </summary>
    /// <remarks>
    /// An update that leaves the bundle alone — which is most of them, rc.15 included — ships an
    /// archive with the same digest, so the directory is already there. Asserted by write time
    /// rather than by counting calls, because what a user would feel is the tree being rewritten.
    /// </remarks>
    [Fact]
    public void AnAlreadyUnpackedBundleIsNotUnpackedAgain()
    {
        var (appRoot, id) = StageArchive();
        var userData = TestTemp.NewDirectory("uindosill-data");

        PythonBundleInstaller.EnsureUnpacked(baseDirectory: appRoot, userDataDirectory: userData);

        var unpacked = Path.Combine(userData, PythonRuntime.BundleDirectoryName, id);
        var marker = Path.Combine(unpacked, "written-by-the-first-unpack");
        File.WriteAllText(marker, "still here");

        var resolved = PythonBundleInstaller.EnsureUnpacked(
            baseDirectory: appRoot, userDataDirectory: userData);

        Assert.Equal(unpacked, resolved.PackageRoot);
        Assert.True(File.Exists(marker), "the directory was rebuilt, so the unpack ran a second time");
    }

    [Fact]
    public void ABundleBesideTheApplicationWinsAndNothingIsUnpacked()
    {
        var (appRoot, _) = StageArchive();
        var userData = TestTemp.NewDirectory("uindosill-data");

        // The loose layout a build from source or a pre-rc.16 publish has.
        var beside = Path.Combine(appRoot, PythonRuntime.BundleDirectoryName);
        Directory.CreateDirectory(Path.GetDirectoryName(Interpreter(beside))!);
        File.WriteAllText(Interpreter(beside), "not really an interpreter");
        Directory.CreateDirectory(Path.Combine(beside, "uindosill_engines"));

        var resolved = PythonBundleInstaller.EnsureUnpacked(
            baseDirectory: appRoot, userDataDirectory: userData);

        Assert.Equal(PythonRuntime.BundleSource.Application, resolved.Source);
        Assert.False(
            Directory.Exists(Path.Combine(userData, PythonRuntime.BundleDirectoryName)),
            "the archive was unpacked even though a bundle was already beside the application");
    }

    [Fact]
    public void ASupersededUnpackIsRemoved()
    {
        var (appRoot, id) = StageArchive();
        var userData = TestTemp.NewDirectory("uindosill-data");

        // What the previous bundle left behind: a digest-named directory that is not this one.
        var stale = Path.Combine(
            userData,
            PythonRuntime.BundleDirectoryName,
            new string('a', 64));
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "big.bin"), "1.5 GB, in spirit");

        PythonBundleInstaller.EnsureUnpacked(baseDirectory: appRoot, userDataDirectory: userData);

        Assert.False(Directory.Exists(stale), "the superseded bundle was left behind");
        Assert.True(Directory.Exists(Path.Combine(userData, PythonRuntime.BundleDirectoryName, id)));
    }

    /// <summary>
    /// The prune runs inside a directory a user is invited to unpack their own bundle into, so it
    /// has to be incapable of touching it.
    /// </summary>
    [Fact]
    public void AHandUnpackedBundleBesideTheDigestNamedOnesIsNotPruned()
    {
        var (appRoot, _) = StageArchive();
        var userData = TestTemp.NewDirectory("uindosill-data");

        // The separate download, unpacked where its instruction says: <user data>/python.
        var byHand = Path.Combine(userData, PythonRuntime.BundleDirectoryName);
        Directory.CreateDirectory(Path.GetDirectoryName(Interpreter(byHand))!);
        File.WriteAllText(Interpreter(byHand), "not really an interpreter");
        var theirs = Path.Combine(byHand, "Lib");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "theirs.py"), "# a user's own file");

        PythonBundleInstaller.EnsureUnpacked(baseDirectory: appRoot, userDataDirectory: userData);

        Assert.True(File.Exists(Path.Combine(theirs, "theirs.py")), "a user's own bundle was deleted");
        Assert.True(File.Exists(Interpreter(byHand)));
    }

    [Fact]
    public void AnArchiveThatDisagreesWithItsManifestIsRefused()
    {
        var (appRoot, _) = StageArchive();
        var userData = TestTemp.NewDirectory("uindosill-data");

        // A truncated copy: the size the manifest records is no longer the size on disk.
        var archive = Path.Combine(appRoot, PythonRuntime.ArchiveFileName);
        File.WriteAllBytes(archive, File.ReadAllBytes(archive)[..^16]);

        var thrown = Assert.Throws<PythonSidecarException>(
            () => PythonBundleInstaller.EnsureUnpacked(
                baseDirectory: appRoot, userDataDirectory: userData));

        Assert.Contains("is not the one it shipped with", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry naming its way out of the destination is refused, and nothing is left behind.
    /// </summary>
    [Fact]
    public void AnEntryThatEscapesTheDestinationIsRefused()
    {
        var (appRoot, _) = StageArchive(extraEntry: "../escaped.txt");
        var userData = TestTemp.NewDirectory("uindosill-data");

        var thrown = Assert.Throws<PythonSidecarException>(
            () => PythonBundleInstaller.EnsureUnpacked(
                baseDirectory: appRoot, userDataDirectory: userData));

        Assert.Contains("outside the directory", thrown.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(userData, "escaped.txt")));
    }

    /// <summary>
    /// Resolution on a build that ships the archive but has not unpacked it says so, rather than
    /// telling a user to fetch a download they already have.
    /// </summary>
    [Fact]
    public void ResolvingBeforeTheUnpackDoesNotSendAUserAfterADownloadTheyHave()
    {
        var (appRoot, _) = StageArchive();
        var userData = TestTemp.NewDirectory("uindosill-data");

        var thrown = Assert.Throws<PythonSidecarException>(
            () => PythonRuntime.Resolve(appRoot, userData));

        Assert.Contains("has not been unpacked yet", thrown.Message, StringComparison.Ordinal);

        // The message a build with no bundle at all gets, which would be wrong here: this build
        // ships one. Until 2026-09-07 this test also asserted the archive's file name was in the
        // sentence; the name went when the sentence became something a user reads rather than
        // something a developer greps, and TheNotYetUnpackedMessageIsWrittenForAReader holds that.
        Assert.DoesNotContain("unpack the separate bundle download", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(InterpreterVariableName, thrown.Message, StringComparison.Ordinal);
    }

    private static string InterpreterVariableName => PythonRuntime.InterpreterVariable;

    /// <summary>
    /// The manifest the packaging script actually wrote on 2026-09-07, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a regression test for a fault that reached a packed installer.</b> PowerShell's
    /// <c>Measure-Object -Sum</c> returns a Double and <c>ConvertTo-Json</c> renders it
    /// <c>1397504487.0</c>; <c>System.Text.Json</c> refuses that into a <see cref="long"/>, and the
    /// silent reader turned the refusal into "no manifest" — which on an installed copy is
    /// indistinguishable from a build carrying no archive, and would have left speaker labelling
    /// and translation dead with a message blaming a missing download.
    /// </para>
    /// <para>
    /// The literal is the real one rather than a fabricated equivalent, because the point is the
    /// exact rendering, and a hand-written approximation is how a test stops covering the thing it
    /// was written for.
    /// </para>
    /// </remarks>
    [Fact]
    public void AManifestWithAFloatingPointByteCountIsRefusedByName()
    {
        var appRoot = TestTemp.NewDirectory("uindosill-app");
        File.WriteAllText(Path.Combine(appRoot, PythonRuntime.ArchiveFileName), "not really a zip");
        File.WriteAllText(
            Path.Combine(appRoot, PythonRuntime.ArchiveManifestFileName),
            """
            {
              "id": "60a785e60929a5b96538205a912675812ddf6472898668a763e1b947c5ff7259",
              "archiveBytes": 459567606,
              "unpackedBytes": 1397504487.0,
              "entries": 55256
            }
            """);

        // The silent reader still says "no manifest", which is what the resolution path needs.
        Assert.Null(PythonRuntime.TryReadArchiveManifest(appRoot));

        // The unpack path must say what is actually wrong with it.
        var thrown = Assert.Throws<PythonSidecarException>(
            () => PythonRuntime.ReadArchiveManifest(appRoot));

        Assert.Contains("could not be read", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("decimal point", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The same manifest with the byte count written as the whole number it is.</summary>
    [Fact]
    public void AManifestWithWholeNumberByteCountsIsRead()
    {
        var appRoot = TestTemp.NewDirectory("uindosill-app");
        File.WriteAllText(
            Path.Combine(appRoot, PythonRuntime.ArchiveManifestFileName),
            """
            {
              "id": "60a785e60929a5b96538205a912675812ddf6472898668a763e1b947c5ff7259",
              "archiveBytes": 459567606,
              "unpackedBytes": 1397504487,
              "entries": 55256
            }
            """);

        var manifest = PythonRuntime.ReadArchiveManifest(appRoot);

        Assert.Equal(459567606, manifest.ArchiveBytes);
        Assert.Equal(1397504487, manifest.UnpackedBytes);
        Assert.Equal(55256, manifest.Entries);
    }

    /// <summary>
    /// The question the window asks, on the state a fresh install is actually in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the test rc.16 shipped without, and the defect it would have caught was total.</b>
    /// The window disabled speaker labelling and translation whenever the resolver found no bundle,
    /// which on a fresh install of an archive-carrying build is the normal state — so both opt-ins
    /// were greyed out, and the unpack that would have satisfied them only runs when one of them is
    /// used. Neither could ever happen, and the reason text beside the checkboxes named a class and
    /// two absolute paths at a user who could do nothing with either.
    /// </para>
    /// <para>
    /// Every other test here drives the unpack directly, which is exactly why none of them saw it:
    /// they all begin after the point the window never got past.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFreshInstallCarryingOnlyTheArchiveCanStillRun()
    {
        var (appRoot, _) = StageArchive();
        var userData = TestTemp.NewDirectory("uindosill-data");

        // The resolver's answer, which is what the window used to ask: nothing is unpacked.
        Assert.False(PythonRuntime.TryResolve(out _, out _, appRoot, userData));

        // The question it should ask: can this run at all? It can — after an unpack it will do.
        Assert.True(PythonRuntime.CanRun(out var reason, appRoot, userData));
        Assert.Null(reason);
    }

    /// <summary>A build with no bundle and no archive is genuinely unavailable, and says why.</summary>
    [Fact]
    public void ABuildWithNeitherBundleNorArchiveCannotRunAndGivesAReason()
    {
        var appRoot = TestTemp.NewDirectory("uindosill-app");
        var userData = TestTemp.NewDirectory("uindosill-data");

        Assert.False(PythonRuntime.CanRun(out var reason, appRoot, userData));
        Assert.NotNull(reason);
        Assert.Contains("The bundled Python is not at", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence a user sees while the bundle is still an archive carries no class name and no
    /// path, because it is read by somebody who can act on neither.
    /// </summary>
    [Fact]
    public void TheNotYetUnpackedMessageIsWrittenForAReader()
    {
        var (appRoot, _) = StageArchive();
        var userData = TestTemp.NewDirectory("uindosill-data");

        var thrown = Assert.Throws<PythonSidecarException>(
            () => PythonRuntime.Resolve(appRoot, userData));

        Assert.Contains("has not been unpacked yet", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PythonBundleInstaller), thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PythonRuntime.ArchiveFileName, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildWithNoArchiveResolvesAndFailsExactlyAsItDidBefore()
    {
        var appRoot = TestTemp.NewDirectory("uindosill-app");
        var userData = TestTemp.NewDirectory("uindosill-data");

        Assert.Null(PythonRuntime.FindArchive(appRoot));
        Assert.Null(PythonRuntime.UnpackedDirectory(appRoot, userData));

        var thrown = Assert.Throws<PythonSidecarException>(
            () => PythonBundleInstaller.EnsureUnpacked(
                baseDirectory: appRoot, userDataDirectory: userData));

        Assert.Contains("The bundled Python is not at", thrown.Message, StringComparison.Ordinal);
    }
}
