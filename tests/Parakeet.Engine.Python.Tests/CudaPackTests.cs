using Parakeet.Core.Models;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// The pack's manifest, and the two installs that must be refused before a byte is fetched.
/// </summary>
/// <remarks>
/// <b>What is not here is the download.</b> Fetching four parts, assembling and unpacking them was
/// driven end to end by hand on 2026-08-29 against a local HTTP server — 1,961,716,087 bytes in
/// four parts, then <c>diarise</c> off the installed result at 7.9 s with an RTTM identical to the
/// CPU reference — and it is not in this suite, which has no network and would not carry 1.8 GB of
/// fixture if it had. What is here is everything that decides whether that download should start,
/// because those are the decisions a user cannot see being made.
/// </remarks>
public class CudaPackTests
{
    private const string Minimal = """
        {
          "archiveName": "pack.zip",
          "archiveBytes": 300,
          "archiveSha256": "abc",
          "unpackedBytes": 900,
          "verified": true,
          "torchVersion": "2.13.0+cu130",
          "baseUrl": "https://example.invalid/v1",
          "parts": [
            { "fileName": "pack.zip.001", "sizeBytes": 200, "sha256": "aa" },
            { "fileName": "pack.zip.002", "sizeBytes": 100, "sha256": "bb" }
          ]
        }
        """;

    // ---- The manifest that actually ships ----------------------------------------------------

    [Fact]
    public void TheShippedManifestLoadsAndIsSelfConsistent()
    {
        // This is the guard on `cuda-pack.json` itself. Its digests are edited by hand from what
        // the packaging script printed, and a typo in a size is not visible by reading.
        var manifest = CudaPackManifest.Shipped;

        Assert.NotEmpty(manifest.Parts);
        Assert.Equal(manifest.ArchiveBytes, manifest.Parts.Sum(p => p.SizeBytes));
        Assert.All(manifest.Parts, p => Assert.Equal(64, p.Sha256.Length));
        Assert.Equal(64, manifest.ArchiveSha256.Length);
    }

    [Fact]
    public void TheShippedPackCarriesTheBundlesOwnTorchPin()
    {
        // **The pairing this whole feature rests on.** The pack goes ahead of the bundle on
        // PYTHONPATH, so its torch must be the version `python/requirements-bundle.txt` pins, in a
        // CUDA build. A pack a version ahead would silently run a decode the translator's
        // 8,149-sentence gate does not describe, and nothing downstream would notice.
        var manifest = CudaPackManifest.Shipped;

        Assert.Equal("2.13.0", manifest.TorchVersionWithoutBuild);
        Assert.Contains("+cu", manifest.TorchVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerifiedManifestNamesAReleaseItsPartsCanBeFetchedFrom()
    {
        // Was TheShippedManifestIsUnverifiedUntilTheAssetsAreUploaded, an Assert.False standing as
        // a reminder to flip the flag in the same commit as the upload. The upload happened on
        // 2026-08-29 for v1.0.0-rc.7, so the reminder is spent — and this is what it was really
        // protecting. `verified` asserts the parts are fetchable; a flag set true over a baseUrl
        // still naming a tag nobody released is exactly the failure it exists to prevent, and it
        // would present to a user as four 404s after they agreed to a 1.8 GB download.
        var manifest = CudaPackManifest.Shipped;

        if (!manifest.Verified)
        {
            return;
        }

        Assert.Matches(@"^https://.+/releases/download/v\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", manifest.BaseUrl);
    }

    // ---- Parsing -----------------------------------------------------------------------------

    [Fact]
    public void PartsThatDoNotAddUpToTheArchiveAreRefusedAtParse()
    {
        // The failure this replaces is a digest mismatch after 1.8 GB has been fetched.
        var wrong = Minimal.Replace("\"archiveBytes\": 300", "\"archiveBytes\": 999");

        var exception = Assert.Throws<PythonSidecarException>(() => CudaPackManifest.Parse(wrong));

        Assert.Contains("999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestWithNoPartsIsRefused()
    {
        var json = """
            {
              "archiveName": "pack.zip",
              "archiveBytes": 0,
              "archiveSha256": "abc",
              "unpackedBytes": 0,
              "verified": true,
              "torchVersion": "2.13.0+cu130",
              "baseUrl": "https://example.invalid/v1",
              "parts": []
            }
            """;

        var exception = Assert.Throws<PythonSidecarException>(() => CudaPackManifest.Parse(json));

        Assert.Contains("no parts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDescriptorCarriesOnePinnedFilePerPart()
    {
        var manifest = CudaPackManifest.Parse(Minimal);

        var descriptor = manifest.AsDescriptor();

        Assert.Equal(2, descriptor.Files.Count);
        Assert.True(descriptor.IsMultiFile);
        Assert.Equal(CudaPackInstaller.PartsDirectoryName, descriptor.DirectoryName);
        Assert.True(descriptor.IsFullyPinned);
        Assert.Equal(300, descriptor.TotalSizeBytes);
        Assert.Equal(
            "https://example.invalid/v1/pack.zip.001",
            descriptor.Files[0].Url?.ToString());
    }

    [Fact]
    public void ThePartsDirectoryIsNotThePackDirectory()
    {
        // A half-finished download must not be mistaken for an installed pack by
        // PythonRuntime.IsCudaPack, which is why the two names cannot collide.
        Assert.NotEqual(PythonRuntime.CudaPackDirectoryName, CudaPackInstaller.PartsDirectoryName);
    }

    // ---- The two refusals, both before any network -------------------------------------------

    private static CudaPackInstaller NewInstaller(out string root)
    {
        root = TestTemp.NewDirectory("uindosill-packroot");
        var store = new LocalModelStore(Path.Combine(root, "store"));
        return new CudaPackInstaller(store, new ModelInstaller(store));
    }

    [Fact]
    public async Task APackWhoseTorchDiffersFromTheBundlesPinIsRefused()
    {
        var installer = NewInstaller(out var root);
        var manifest = CudaPackManifest.Parse(Minimal);   // torch 2.13.0+cu130

        var exception = await Assert.ThrowsAsync<CudaPackException>(
            () => installer.InstallAsync(manifest, root, expectedTorchVersion: "2.14.0"));

        Assert.Contains("2.13.0+cu130", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2.14.0", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(root, PythonRuntime.CudaPackDirectoryName)));
    }

    [Fact]
    public async Task AnUnverifiedPackIsRefusedUnlessTheCallerOptsIn()
    {
        var installer = NewInstaller(out var root);
        var manifest = CudaPackManifest.Parse(
            Minimal.Replace("\"verified\": true", "\"verified\": false"));

        var exception = await Assert.ThrowsAsync<CudaPackException>(
            () => installer.InstallAsync(manifest, root, "2.13.0"));

        Assert.Contains("verify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheVersionIsCheckedBeforeTheVerificationFlag()
    {
        // Order matters for the message a user gets: a pack that is both unverified and the wrong
        // version is *first* the wrong version, because that is the one no upload would fix.
        var installer = NewInstaller(out var root);
        var manifest = CudaPackManifest.Parse(
            Minimal.Replace("\"verified\": true", "\"verified\": false"));

        var exception = await Assert.ThrowsAsync<CudaPackException>(
            () => installer.InstallAsync(manifest, root, "9.9.9"));

        Assert.Contains("9.9.9", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlreadyInstalledPackIsReturnedWithoutDownloading()
    {
        // The manifest points at example.invalid, so anything that reached the network would fail
        // rather than pass slowly.
        var installer = NewInstaller(out var root);
        var destination = Path.Combine(root, PythonRuntime.CudaPackDirectoryName);
        Directory.CreateDirectory(Path.Combine(destination, "torch"));
        await File.WriteAllTextAsync(Path.Combine(destination, "torch", "__init__.py"), "#");

        var manifest = CudaPackManifest.Parse(Minimal);

        var result = await installer.InstallAsync(manifest, root, "2.13.0");

        Assert.Equal(destination, result);
    }

    [Fact]
    public void IsInstalledAnswersForTheDirectoryTheInstallerWritesTo()
    {
        var root = TestTemp.NewDirectory("uindosill-packroot");
        Assert.False(CudaPackInstaller.IsInstalled(root));

        var torch = Path.Combine(root, PythonRuntime.CudaPackDirectoryName, "torch");
        Directory.CreateDirectory(torch);
        File.WriteAllText(Path.Combine(torch, "__init__.py"), "#");

        Assert.True(CudaPackInstaller.IsInstalled(root));
    }
}
