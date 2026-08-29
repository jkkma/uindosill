using System.IO.Compression;
using System.Security.Cryptography;
using Parakeet.Core.Models;

namespace Parakeet.Engine.Python;

/// <summary>Which stage of the install a progress report is about.</summary>
public enum CudaPackPhase
{
    /// <summary>Fetching the parts. Reported straight from <see cref="ModelInstaller"/>.</summary>
    Downloading,

    /// <summary>Concatenating the parts into the archive.</summary>
    Assembling,

    /// <summary>Hashing the assembled archive against the manifest.</summary>
    Verifying,

    /// <summary>Extracting into a staging directory.</summary>
    Unpacking,

    /// <summary>Moving the staging directory into place and removing the parts.</summary>
    Finishing,
}

/// <summary>Where the install has got to.</summary>
public sealed record CudaPackProgress
{
    public required CudaPackPhase Phase { get; init; }

    public long BytesCompleted { get; init; }

    public long? TotalBytes { get; init; }

    /// <summary>The download's own report, present only during <see cref="CudaPackPhase.Downloading"/>.</summary>
    public ModelInstallProgress? Download { get; init; }

    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)BytesCompleted / TotalBytes.Value, 0, 1) : null;
}

/// <summary>The install could not be completed, with the reason a user can act on.</summary>
public sealed class CudaPackException : Exception
{
    public CudaPackException(string message) : base(message)
    {
    }

    public CudaPackException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public CudaPackException()
    {
    }
}

/// <summary>
/// Downloads the CUDA pack, assembles it, and puts it where <see cref="PythonRuntime"/> looks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The download is <see cref="ModelInstaller"/>'s and deliberately not this class's.</b> Fetching
/// several pinned files with per-file resume, per-file digests, a staging directory and an
/// all-or-nothing move is a problem this repository has already solved once, over an entry of nine
/// files; writing it again for four would be two implementations of resume to keep correct. What is
/// here is only what a pack needs and a model does not: concatenation, a whole-archive digest, an
/// unzip, and a destination outside the model store.
/// </para>
/// <para>
/// <b>Two staging directories, for two different atomicity questions.</b> The installer stages the
/// parts and moves them into <see cref="PartsDirectoryName"/>; this stages the *unpacked* tree and
/// moves that into <see cref="PythonRuntime.CudaPackDirectoryName"/>. The second matters more than
/// it looks: a half-extracted `python-cuda` holding a torch with some of its DLLs would satisfy
/// <see cref="PythonRuntime.IsCudaPack"/>, go in front of the bundle on <c>PYTHONPATH</c>, and break
/// a diariser that worked yesterday. The directory only ever appears complete.
/// </para>
/// <para>
/// <b>The version is checked against the bundle's pin.</b> The pack shadows the bundle's torch, so a
/// pack whose version has drifted would silently run a decode the translator's 8,149-sentence gate
/// does not describe. This is the only place that pairing can be checked at all — the builder can
/// refuse to produce a non-CUDA pack, but it cannot know which bundle it will land beside.
/// </para>
/// </remarks>
public sealed class CudaPackInstaller
{
    /// <summary>
    /// Where the parts land: beside the pack, and deliberately not named like one.
    /// </summary>
    /// <remarks>
    /// A dot in the name so it cannot collide with <see cref="PythonRuntime.CudaPackDirectoryName"/>
    /// and cannot be mistaken for it by a reader either. Left behind on a failure rather than
    /// deleted, which is what makes a retry resume instead of starting the 1.8 GB again.
    /// </remarks>
    public const string PartsDirectoryName = "python-cuda.parts";

    private readonly IModelStore _store;
    private readonly ModelInstaller _installer;

    public CudaPackInstaller(IModelStore store, ModelInstaller installer)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    }

    /// <summary>
    /// Whether the pack is already installed at the destination this installer would write to.
    /// </summary>
    public static bool IsInstalled(string destinationRoot) =>
        PythonRuntime.IsCudaPack(
            Path.Combine(destinationRoot, PythonRuntime.CudaPackDirectoryName));

    /// <summary>
    /// Downloads and installs the pack under <paramref name="destinationRoot"/>, which is the
    /// directory holding the bundle rather than the pack directory itself.
    /// </summary>
    /// <param name="expectedTorchVersion">
    /// The bundle's torch pin without its build local — `2.13.0`. The install is refused when the
    /// manifest disagrees, because the pack would shadow a torch it was never measured beside.
    /// </param>
    public async Task<string> InstallAsync(
        CudaPackManifest manifest,
        string destinationRoot,
        string expectedTorchVersion,
        bool allowUnverified = false,
        IProgress<CudaPackProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTorchVersion);

        if (!string.Equals(manifest.TorchVersionWithoutBuild, expectedTorchVersion, StringComparison.Ordinal))
        {
            throw new CudaPackException(
                $"This pack carries torch {manifest.TorchVersion} and the bundle pins " +
                $"{expectedTorchVersion}. The pack goes in front of the bundle on PYTHONPATH, so " +
                "installing it would run a torch this build has never been measured against. Use a " +
                "pack built from this version's requirements-bundle.txt.");
        }

        if (!manifest.Verified && !allowUnverified)
        {
            throw new CudaPackException(
                "This CUDA pack's digests have not been checked against an uploaded release asset, " +
                "so there is nothing to verify the download against. It is not installed by default.");
        }

        var destination = Path.Combine(destinationRoot, PythonRuntime.CudaPackDirectoryName);
        if (PythonRuntime.IsCudaPack(destination))
        {
            return destination;
        }

        // ---- The parts, fetched by the machinery that already knows how to resume one.
        var descriptor = manifest.AsDescriptor();
        await _installer.InstallAsync(
            descriptor,
            new ModelInstallOptions { AllowUnverified = allowUnverified },
            new Progress<ModelInstallProgress>(p => progress?.Report(new CudaPackProgress
            {
                Phase = CudaPackPhase.Downloading,
                BytesCompleted = p.BytesCompleted,
                TotalBytes = p.TotalBytes ?? manifest.TotalDownloadBytes,
                Download = p,
            })),
            ct).ConfigureAwait(false);

        var partsDirectory = _store.PathFor(descriptor);
        var archive = Path.Combine(partsDirectory, manifest.ArchiveName);

        try
        {
            await AssembleAsync(manifest, partsDirectory, archive, progress, ct).ConfigureAwait(false);
            await VerifyAsync(manifest, archive, progress, ct).ConfigureAwait(false);
            Unpack(manifest, archive, destinationRoot, destination, progress, ct);
        }
        finally
        {
            // The assembled archive is 1.8 GB of duplicate and is removed whatever happened; the
            // *parts* are kept on failure so a retry resumes, and removed below only on success.
            TryDelete(archive);
        }

        progress?.Report(new CudaPackProgress { Phase = CudaPackPhase.Finishing });
        TryDeleteDirectory(partsDirectory);

        return destination;
    }

    private static async Task AssembleAsync(
        CudaPackManifest manifest,
        string partsDirectory,
        string archive,
        IProgress<CudaPackProgress>? progress,
        CancellationToken ct)
    {
        TryDelete(archive);

        var written = 0L;
        var output = new FileStream(
            archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await using (output.ConfigureAwait(false))
        {
            // In manifest order rather than by sorting the directory: the order is the manifest's
            // fact, and a lexical sort of the file names only happens to agree with it.
            foreach (var part in manifest.Parts)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(partsDirectory, part.FileName);
                if (!File.Exists(path))
                {
                    throw new CudaPackException(
                        $"{part.FileName} is missing after a download that reported success.");
                }

                var input = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
                await using (input.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                }

                written += part.SizeBytes;
                progress?.Report(new CudaPackProgress
                {
                    Phase = CudaPackPhase.Assembling,
                    BytesCompleted = written,
                    TotalBytes = manifest.ArchiveBytes,
                });
            }
        }
    }

    private static async Task VerifyAsync(
        CudaPackManifest manifest,
        string archive,
        IProgress<CudaPackProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new CudaPackProgress
        {
            Phase = CudaPackPhase.Verifying,
            TotalBytes = manifest.ArchiveBytes,
        });

        var length = new FileInfo(archive).Length;
        if (length != manifest.ArchiveBytes)
        {
            throw new CudaPackException(
                $"The assembled archive is {length:N0} bytes and the manifest says " +
                $"{manifest.ArchiveBytes:N0}. The parts did not reassemble.");
        }

        string digest;
        var stream = new FileStream(
            archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            digest = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }

        if (!string.Equals(digest, manifest.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CudaPackException(
                $"The assembled archive hashes to {digest} and the manifest says " +
                $"{manifest.ArchiveSha256}. Every part matched its own digest, so this is the order " +
                "or completeness of the reassembly rather than a corrupted download.");
        }
    }

    private static void Unpack(
        CudaPackManifest manifest,
        string archive,
        string destinationRoot,
        string destination,
        IProgress<CudaPackProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new CudaPackProgress
        {
            Phase = CudaPackPhase.Unpacking,
            TotalBytes = manifest.UnpackedBytes,
        });

        var staging = Path.Combine(
            destinationRoot, PythonRuntime.CudaPackDirectoryName + ".staging");
        TryDeleteDirectory(staging);
        Directory.CreateDirectory(staging);

        try
        {
            ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);
            ct.ThrowIfCancellationRequested();

            if (!PythonRuntime.IsCudaPack(staging))
            {
                throw new CudaPackException(
                    "The archive unpacked without a torch package at its root, so it is not a CUDA " +
                    "pack. Nothing was installed.");
            }

            // The one moment the destination exists in an incomplete state is this rename, which is
            // why extraction happens somewhere else first.
            TryDeleteDirectory(destination);
            Directory.Move(staging, destination);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover in a directory the next install rebuilds is not worth failing over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
