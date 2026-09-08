using System.IO.Compression;
using Parakeet.Core.Models;

namespace Parakeet.Engine.Python;

/// <summary>Which stage of the unpack a progress report is about.</summary>
public enum PythonBundlePhase
{
    /// <summary>Checking the archive against its manifest before opening it.</summary>
    Verifying,

    /// <summary>Writing entries into a staging directory.</summary>
    Unpacking,

    /// <summary>Moving the staging directory into place and removing superseded ones.</summary>
    Finishing,
}

/// <summary>Where the unpack has got to.</summary>
public sealed record PythonBundleProgress
{
    public required PythonBundlePhase Phase { get; init; }

    public long BytesCompleted { get; init; }

    public long? TotalBytes { get; init; }

    /// <summary>Entries written so far, which is the number this cost actually scales with.</summary>
    public int EntriesCompleted { get; init; }

    public int? TotalEntries { get; init; }

    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp((double)BytesCompleted / TotalBytes.Value, 0, 1) : null;
}

/// <summary>
/// Unpacks the Python bundle this build ships as an archive into the user data directory, once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the bundle is an archive at all is measured, and the measurement is in
/// <see cref="PythonRuntime.ArchiveFileName"/>.</b> The short version: shipping it as 55,256 loose
/// files made every update pay for all of them four times over, and an rc.14 to rc.15 update that
/// downloaded 31.8 MB in three seconds took 10m 05s.
/// </para>
/// <para>
/// <b>The unpack is lazy, and that is the second half of the decision.</b> Doing it during the
/// update would put the cost straight back into the update window; doing it at launch would charge
/// every user for two opt-ins most never use. Core transcription is parakeet.cpp and never touches
/// this. So the first speaker-labelling or translation request pays, once, and only on a build
/// whose bundle actually changed — the directory is named for the archive's digest, so an update
/// that leaves the bundle alone finds it already unpacked and does nothing.
/// </para>
/// <para>
/// <b>It stages and then moves, for the reason <see cref="CudaPackInstaller"/> stages and then
/// moves.</b> A half-written bundle directory holding an interpreter but no engines is exactly
/// what <see cref="PythonRuntime"/> calls half a bundle, and it would be found, reported as
/// damaged, and never repaired — because the destination existing is what stops the next attempt.
/// The destination only ever appears complete.
/// </para>
/// <para>
/// <b>The archive carries <c>python/</c> at its root</b>, which is the same archive the release
/// publishes as the separate command-line download: one artefact, built once and verified once,
/// serving both the installer and the person unpacking it by hand. That root is why the staged
/// tree is moved from its <c>python</c> subdirectory rather than wholesale.
/// </para>
/// </remarks>
public static class PythonBundleInstaller
{
    /// <summary>
    /// A digest names an unpacked bundle: 64 lowercase hex characters, and nothing else is touched.
    /// </summary>
    /// <remarks>
    /// The prune below deletes superseded unpacks, and it runs inside a directory a user is invited
    /// to put their own hand-unpacked bundle in. Matching the shape of a SHA-256 rather than
    /// "a directory that looks like a bundle" is what makes it impossible for this to delete
    /// somebody's own copy: a person does not name a folder after a digest by accident.
    /// </remarks>
    private static bool IsUnpackedBundleName(string name) =>
        name.Length == 64 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Resolves the sidecar, unpacking this build's archive first if that is what is missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the resolution rather than void so a caller does not resolve twice, and so the
    /// provenance a run reports is the one this call produced.
    /// </para>
    /// <para>
    /// A bundle that resolves already — beside the application, unpacked by a previous run, named
    /// by <c>UINDOSILL_PYTHON</c>, or downloaded by hand — returns immediately and reports no
    /// progress at all. That is the overwhelmingly common path: the unpack happens once per bundle
    /// per machine, and every launch after it takes the early return.
    /// </para>
    /// </remarks>
    public static Task<PythonRuntime.Resolution> EnsureUnpackedAsync(
        IProgress<PythonBundleProgress>? progress = null,
        CancellationToken ct = default,
        string? baseDirectory = null,
        string? userDataDirectory = null) =>
        Task.Run(() => EnsureUnpacked(progress, ct, baseDirectory, userDataDirectory), ct);

    /// <summary>
    /// <see cref="EnsureUnpackedAsync"/> without the thread hop, for callers already off the UI
    /// thread.
    /// </summary>
    /// <remarks>
    /// This is the one the sidecar's own default factories call, which is what makes the unpack
    /// lazy in the exact sense the decision meant: it happens when something first asks for a
    /// sidecar, in whatever thread asked, and a transcription job asks from its own worker. A
    /// caller on the UI thread would block it — so the window's engine provider must not become
    /// one, and today is not: both sidecar-backed engines are built inside a job.
    /// </remarks>
    public static PythonRuntime.Resolution EnsureUnpacked(
        IProgress<PythonBundleProgress>? progress = null,
        CancellationToken ct = default,
        string? baseDirectory = null,
        string? userDataDirectory = null)
    {
        if (PythonRuntime.TryResolve(out var resolved, out _, baseDirectory, userDataDirectory)
            && resolved is not null)
        {
            return resolved;
        }

        // No archive to unpack means nothing here can help, and the resolver's own message is the
        // right one: it names every place it looked, which is what a user needs.
        if (PythonRuntime.FindArchive(baseDirectory) is not { } archive)
        {
            return PythonRuntime.Resolve(baseDirectory, userDataDirectory);
        }

        // The throwing reader, not the silent one: at this point the manifest is the only thing
        // that says what is being unpacked, and "missing" and "malformed" send a packager in
        // different directions. See PythonRuntime.ReadArchiveManifest for the fault that taught it.
        var manifest = PythonRuntime.ReadArchiveManifest(baseDirectory);

        var userDataRoot = userDataDirectory ?? UserDataPaths.RootDirectory();
        var destination = Path.Combine(
            userDataRoot, PythonRuntime.BundleDirectoryName, manifest.Id);

        Unpack(archive, manifest, destination, progress, ct);

        // Resolved again rather than constructed here, so that what a run reports having used is
        // what the resolver actually found on disk — including the CUDA pack, which this knows
        // nothing about.
        return PythonRuntime.Resolve(baseDirectory, userDataDirectory);
    }

    private static void Unpack(
        string archive,
        PythonRuntime.ArchiveManifest manifest,
        string destination,
        IProgress<PythonBundleProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new PythonBundleProgress
        {
            Phase = PythonBundlePhase.Verifying,
            TotalBytes = manifest.UnpackedBytes,
            TotalEntries = manifest.Entries,
        });

        // Size rather than digest, deliberately. The archive was written by the installer into a
        // directory the installer owns, so the threat is a truncated or half-copied file rather
        // than a substituted one — and hashing 1.5 GB before every unpack would put back a chunk
        // of the cost this whole change exists to remove.
        var length = new FileInfo(archive).Length;
        if (length != manifest.ArchiveBytes)
        {
            throw new PythonSidecarException(
                $"{PythonRuntime.ArchiveFileName} is {length:N0} bytes and " +
                $"{PythonRuntime.ArchiveManifestFileName} says {manifest.ArchiveBytes:N0}. The " +
                "archive in this install is not the one it shipped with; reinstalling replaces it.");
        }

        // <user data>/python, the directory the digest-named unpack sits inside — and the one a
        // hand-unpacked download occupies directly, which is why the prune below is so narrow.
        var bundleRoot = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(bundleRoot);

        var staging = Path.Combine(bundleRoot, ".staging-" + manifest.Id);
        TryDeleteDirectory(staging);
        Directory.CreateDirectory(staging);

        try
        {
            Extract(archive, staging, manifest, progress, ct);

            // The archive's root, which is what makes one artefact serve both this and the
            // hand-unpacked download.
            var staged = Path.Combine(staging, PythonRuntime.BundleDirectoryName);
            if (!File.Exists(Path.Combine(staged, PythonRuntime.ExecutableName)))
            {
                throw new PythonSidecarException(
                    $"{PythonRuntime.ArchiveFileName} unpacked without " +
                    $"{PythonRuntime.BundleDirectoryName}/{PythonRuntime.ExecutableName} at its " +
                    "root, so it is not a bundle. Nothing was installed.");
            }

            if (!Directory.Exists(Path.Combine(staged, "uindosill_engines")))
            {
                throw new PythonSidecarException(
                    $"{PythonRuntime.ArchiveFileName} unpacked without a uindosill_engines package " +
                    "beside its interpreter — half a bundle. Nothing was installed.");
            }

            progress?.Report(new PythonBundleProgress
            {
                Phase = PythonBundlePhase.Finishing,
                TotalBytes = manifest.UnpackedBytes,
                BytesCompleted = manifest.UnpackedBytes,
                TotalEntries = manifest.Entries,
                EntriesCompleted = manifest.Entries,
            });

            ct.ThrowIfCancellationRequested();

            TryDeleteDirectory(destination);
            if (Directory.Exists(destination))
            {
                throw new PythonSidecarException(
                    $"The bundle at {destination} could not be removed to make way for the freshly " +
                    "unpacked one — something is holding it open. Close anything using it, or " +
                    "delete the directory by hand, and try again.");
            }

            Directory.Move(staged, destination);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }

        TryDeleteDirectory(staging);
        PruneSuperseded(bundleRoot, manifest.Id);
    }

    /// <summary>
    /// Entry by entry rather than <see cref="ZipFile.ExtractToDirectory(string, string)"/>, because
    /// this is the one operation in the product that takes minutes and a user is watching it.
    /// </summary>
    /// <remarks>
    /// The traversal guard is not theatre even though the archive is one this project builds: an
    /// entry whose name escapes the destination is the standard zip failure, the check is two
    /// lines, and an archive that has been tampered with on disk is exactly the case where the
    /// destination is a user's profile.
    /// </remarks>
    private static void Extract(
        string archive,
        string staging,
        PythonRuntime.ArchiveManifest manifest,
        IProgress<PythonBundleProgress>? progress,
        CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archive);

        var root = Path.GetFullPath(staging);
        long bytes = 0;
        var entries = 0;

        // Reported at a granularity a person can see rather than per entry: 55,000 progress
        // callbacks marshalled onto a UI thread is its own slowdown, and this is a bar, not a log.
        var nextReport = 0L;
        var step = Math.Max(manifest.UnpackedBytes / 200, 1);

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new PythonSidecarException(
                    $"{PythonRuntime.ArchiveFileName} holds an entry that would be written outside " +
                    $"the directory being unpacked into ({entry.FullName}). Nothing was installed.");
            }

            // A directory entry, which a zip records with a trailing slash and no content.
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);

            bytes += entry.Length;
            entries++;

            if (bytes >= nextReport)
            {
                nextReport = bytes + step;
                progress?.Report(new PythonBundleProgress
                {
                    Phase = PythonBundlePhase.Unpacking,
                    BytesCompleted = bytes,
                    TotalBytes = manifest.UnpackedBytes,
                    EntriesCompleted = entries,
                    TotalEntries = manifest.Entries,
                });
            }
        }
    }

    /// <summary>
    /// Removes unpacked bundles this build has superseded, so the data directory holds one.
    /// </summary>
    /// <remarks>
    /// Without this, every bundle change would leave 1.5 GB behind for ever, in a directory whose
    /// whole purpose is surviving uninstall — the worst place in the product to accumulate. Failure
    /// is swallowed: a stale bundle is wasted disk, and refusing to label a speaker because an old
    /// directory could not be deleted would be a far worse trade.
    /// </remarks>
    private static void PruneSuperseded(string bundleRoot, string keep)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(bundleRoot);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            if (name.Equals(keep, StringComparison.Ordinal) || !IsUnpackedBundleName(name))
            {
                continue;
            }

            TryDeleteDirectory(directory);
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
