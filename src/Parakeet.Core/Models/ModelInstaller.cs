using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Parakeet.Core.Models;

public enum ModelInstallPhase
{
    Connecting,
    Downloading,
    Verifying,
    Installing,
    Completed,
}

public sealed record ModelInstallProgress
{
    public required ModelInstallPhase Phase { get; init; }

    /// <summary>Bytes done across the whole entry, not the file currently in flight.</summary>
    public long BytesCompleted { get; init; }

    public long? TotalBytes { get; init; }

    public double? BytesPerSecond { get; init; }

    public bool Resumed { get; init; }

    /// <summary>The file being fetched, for an entry that is more than one. Null otherwise.</summary>
    public string? CurrentFile { get; init; }

    /// <summary>How many of the entry's files are done, and how many there are.</summary>
    public int FilesCompleted { get; init; }

    public int FileCount { get; init; } = 1;

    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp(BytesCompleted / (double)TotalBytes.Value, 0d, 1d) : null;

    public TimeSpan? Remaining =>
        TotalBytes is > 0 && BytesPerSecond is > 0
            ? TimeSpan.FromSeconds(Math.Max(0, TotalBytes.Value - BytesCompleted) / BytesPerSecond.Value)
            : null;
}

public sealed record ModelInstallOptions
{
    public static ModelInstallOptions Default { get; } = new();

    /// <summary>
    /// Install a model whose manifest entry does not pin a SHA-256 for every file. Off by default:
    /// a 670 MB blob pulled over the network and loaded into the process with no integrity check is
    /// not something to do quietly, and one unpinned file among nine is enough to mean it.
    /// </summary>
    public bool AllowUnverified { get; init; }

    /// <summary>Re-download even when the files are already present.</summary>
    public bool Force { get; init; }
}

/// <summary>What one file actually weighed and hashed once it was on disk.</summary>
public sealed record InstalledFileDigest
{
    public required string FileName { get; init; }

    public required string Sha256 { get; init; }

    public required long SizeBytes { get; init; }
}

public sealed record ModelInstallResult
{
    public required InstalledModel Model { get; init; }

    /// <summary>
    /// The digest computed over each file, in manifest order.
    /// </summary>
    /// <remarks>
    /// A list rather than one string, and there is no single-file shortcut on purpose. The two
    /// callers of this are both "here is what to pin in the catalogue" messages, and for an entry
    /// of nine files the answer is nine lines. A convenience property returning the first would
    /// have let both keep compiling while printing an eighth of the truth.
    /// </remarks>
    public required IReadOnlyList<InstalledFileDigest> Files { get; init; }

    public bool Resumed { get; init; }

    public bool AlreadyPresent { get; init; }
}

public class ModelInstallException : Exception
{
    public ModelInstallException(string message)
        : base(message)
    {
    }

    public ModelInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ModelInstallException()
    {
    }
}

/// <summary>
/// Downloads weights into the model store: resumable, integrity-checked, and atomic at the
/// end so a half-written 670 MB file is never mistaken for a model.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no such thing as a partially installed model, and that is the design rather than a
/// happy accident.</b> A multi-file entry is assembled in a sibling staging directory —
/// <c>&lt;name&gt;.part</c> — and becomes the installed directory in a single
/// <see cref="Directory.Move(string, string)"/> once every file has been fetched, sized and hashed.
/// Interrupt it anywhere and what is on disk is a staging directory, which
/// <see cref="IModelStore.IsInstalled"/> does not look at and <see cref="LocalModelStore.ListInstalled()"/>
/// does not report. The user has an incomplete <i>download</i>, which is resumable; they never have
/// an incomplete <i>model</i>, which would be a thing an engine tries to load.
/// </para>
/// <para>
/// Resume is per file, because the alternative is worse than it sounds: the ONNX translation route
/// is nine files and over a gigabyte at fp32, and an all-or-nothing resume would throw away eight
/// good files because the ninth was interrupted. A file already sitting in the staging directory
/// with the right digest is skipped, and a file half-fetched resumes by byte range exactly as a
/// single-file entry does.
/// </para>
/// <para>
/// The one window this leaves is between deleting an old installed directory and moving the new one
/// into place. Crash there and the model is not installed and the staging directory is complete;
/// the next run re-verifies it, finds every file good, downloads nothing and moves it. That costs a
/// re-hash and no bytes, which is the right price for not carrying a rollback journal.
/// </para>
/// </remarks>
public sealed class ModelInstaller : IDisposable
{
    private const int BufferSize = 1 << 20;

    /// <summary>Suffix for both a half-written file and a half-assembled directory.</summary>
    private const string PartSuffix = ".part";

    /// <summary>
    /// The one host a Hugging Face access token is ever sent to.
    /// </summary>
    /// <remarks>
    /// <b>A token is a credential and a catalogue is data</b>, so the two must not be allowed to
    /// meet on the say-so of a URL in a manifest. Attaching the header to whatever host an entry
    /// happens to name would mean that anyone who could get a URL into <c>models.json</c> — or into
    /// a redirect from one — could collect the user's token. So the check is against this constant
    /// and its subdomains rather than against the entry.
    /// </remarks>
    private const string HuggingFaceHost = "huggingface.co";

    private readonly IModelStore _store;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<string?>? _huggingFaceToken;

    public ModelInstaller(
        IModelStore store,
        HttpClient? http = null,
        Func<string?>? huggingFaceToken = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        // <b>A callback rather than a string</b>, so that a token entered or cleared after this
        // installer was constructed is the one used, and so that nothing here holds a credential
        // alive for longer than the request that needs it.
        _huggingFaceToken = huggingFaceToken;

        if (http is null)
        {
            // Large files over slow links: the per-request timeout has to be off, because it
            // covers the whole body, not the idle gap. Progress reporting is what tells a user
            // the download is alive.
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
            _ownsHttp = false;
        }
    }

    public async Task<ModelInstallResult> InstallAsync(
        ModelDescriptor model,
        ModelInstallOptions? options = null,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= ModelInstallOptions.Default;

        if (!model.IsFullyPinned && !options.AllowUnverified)
        {
            var unpinned = model.Files.Where(f => f.Sha256 is null).Select(f => f.FileName).ToList();
            throw new ModelInstallException(
                $"Model '{model.Id}' has no pinned SHA-256 in the catalogue for " +
                (model.Files.Count == 1 ? "its file" : $"{unpinned.Count} of its {model.Files.Count} files ({string.Join(", ", unpinned)})") +
                ", so a download cannot be verified. Pin the digest in models.json (see docs/MODELS.md) " +
                "or install with the explicit unverified opt-in.");
        }

        if (!options.Force && _store.IsInstalled(model))
        {
            if (await VerifyInstalledAsync(model, ct).ConfigureAwait(false) is { } existing)
            {
                var bytes = existing.Sum(f => f.SizeBytes);
                progress?.Report(new ModelInstallProgress
                {
                    Phase = ModelInstallPhase.Completed,
                    BytesCompleted = bytes,
                    TotalBytes = bytes,
                    FilesCompleted = existing.Count,
                    FileCount = model.Files.Count,
                });

                return new ModelInstallResult
                {
                    Model = Describe(model),
                    Files = existing,
                    AlreadyPresent = true,
                };
            }

            // Anything on disk that does not match the pins is a corrupt or tampered install.
            // Replacing it is the only safe outcome, and for a directory that means the directory:
            // keeping the files that happened to verify would leave a set nobody has ever tested.
            _store.Remove(model);
        }

        return model.IsMultiFile
            ? await InstallDirectoryAsync(model, progress, ct).ConfigureAwait(false)
            : await InstallSingleFileAsync(model, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Digests for every installed file when all of them match their pins, null when any does not.
    /// </summary>
    /// <remarks>
    /// A file whose entry pins no digest passes: there is nothing to disagree with. That is the
    /// same latitude the unverified opt-in buys at download time, applied to the copy already here.
    /// </remarks>
    private async Task<IReadOnlyList<InstalledFileDigest>?> VerifyInstalledAsync(
        ModelDescriptor model, CancellationToken ct)
    {
        var digests = new List<InstalledFileDigest>(model.Files.Count);

        foreach (var file in model.Files)
        {
            var path = _store.PathFor(model, file);
            var digest = await ComputeSha256Async(path, ct).ConfigureAwait(false);

            if (file.Sha256 is { } expected && !string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            digests.Add(new InstalledFileDigest
            {
                FileName = file.FileName,
                Sha256 = digest,
                SizeBytes = new FileInfo(path).Length,
            });
        }

        return digests;
    }

    private async Task<ModelInstallResult> InstallSingleFileAsync(
        ModelDescriptor model, IProgress<ModelInstallProgress>? progress, CancellationToken ct)
    {
        var file = model.Files[0];
        var finalPath = _store.PathFor(model, file);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        var fetched = await FetchAsync(model, file, finalPath, model.TotalSizeBytes, 0, 0, progress, ct)
            .ConfigureAwait(false);

        progress?.Report(new ModelInstallProgress
        {
            Phase = ModelInstallPhase.Completed,
            BytesCompleted = fetched.Digest.SizeBytes,
            TotalBytes = fetched.Digest.SizeBytes,
            Resumed = fetched.Resumed,
            FilesCompleted = 1,
            FileCount = 1,
        });

        return new ModelInstallResult
        {
            Model = Describe(model),
            Files = [fetched.Digest],
            Resumed = fetched.Resumed,
        };
    }

    private async Task<ModelInstallResult> InstallDirectoryAsync(
        ModelDescriptor model, IProgress<ModelInstallProgress>? progress, CancellationToken ct)
    {
        var finalDirectory = _store.PathFor(model);
        var stagingDirectory = finalDirectory + PartSuffix;
        Directory.CreateDirectory(stagingDirectory);

        var total = model.TotalSizeBytes;
        var digests = new List<InstalledFileDigest>(model.Files.Count);
        var completedBytes = 0L;
        var resumedAny = false;

        for (var index = 0; index < model.Files.Count; index++)
        {
            var file = model.Files[index];

            // <b>A file name may be a relative path since 2026-08-27</b>, so the directory it lands
            // in is not necessarily the staging root. Created here rather than assumed: the
            // catalogue guarantees the path cannot climb out of the staging directory
            // (<c>ModelCatalog.IsSafeRelativeFileName</c>), and the <see cref="Directory.Move"/>
            // that promotes the staging tree at the end carries subdirectories with it unchanged.
            var stagedPath = Path.Combine(
                stagingDirectory,
                file.FileName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);

            var fetched = await FetchAsync(model, file, stagedPath, total, completedBytes, index, progress, ct)
                .ConfigureAwait(false);

            digests.Add(fetched.Digest);
            completedBytes += fetched.Digest.SizeBytes;
            resumedAny |= fetched.Resumed;
        }

        progress?.Report(new ModelInstallProgress
        {
            Phase = ModelInstallPhase.Installing,
            BytesCompleted = completedBytes,
            TotalBytes = total ?? completedBytes,
            FilesCompleted = model.Files.Count,
            FileCount = model.Files.Count,
        });

        // The whole point of the staging directory, in two statements. Everything above this line
        // can be interrupted and leaves nothing that looks installed; everything below is done.
        if (Directory.Exists(finalDirectory))
        {
            Directory.Delete(finalDirectory, recursive: true);
        }

        Directory.Move(stagingDirectory, finalDirectory);

        progress?.Report(new ModelInstallProgress
        {
            Phase = ModelInstallPhase.Completed,
            BytesCompleted = completedBytes,
            TotalBytes = completedBytes,
            Resumed = resumedAny,
            FilesCompleted = model.Files.Count,
            FileCount = model.Files.Count,
        });

        return new ModelInstallResult
        {
            Model = Describe(model),
            Files = digests,
            Resumed = resumedAny,
        };
    }

    /// <summary>
    /// Gets one file to <paramref name="finalPath"/>, verified, resuming whatever can be resumed.
    /// </summary>
    /// <param name="entryBytesBefore">
    /// Bytes already done for the whole entry, so progress across nine files climbs once rather
    /// than nine times from zero.
    /// </param>
    private async Task<(InstalledFileDigest Digest, bool Resumed)> FetchAsync(
        ModelDescriptor model,
        ModelFile file,
        string finalPath,
        long? entryTotalBytes,
        long entryBytesBefore,
        int fileIndex,
        IProgress<ModelInstallProgress>? progress,
        CancellationToken ct)
    {
        var multi = model.Files.Count > 1;

        ModelInstallProgress Shape(ModelInstallPhase phase, long fileBytes, long? fileTotal, double? rate, bool resumed) => new()
        {
            Phase = phase,
            BytesCompleted = entryBytesBefore + fileBytes,
            TotalBytes = entryTotalBytes ?? (multi ? null : fileTotal),
            BytesPerSecond = rate,
            Resumed = resumed,
            CurrentFile = multi ? file.FileName : null,
            FilesCompleted = fileIndex,
            FileCount = model.Files.Count,
        };

        // Already staged and correct from an earlier interrupted run: eight good files are not
        // re-fetched because the ninth failed.
        if (File.Exists(finalPath) && file.Sha256 is { } pinned)
        {
            var existing = await ComputeSha256Async(finalPath, ct).ConfigureAwait(false);
            if (string.Equals(existing, pinned, StringComparison.OrdinalIgnoreCase))
            {
                var length = new FileInfo(finalPath).Length;
                progress?.Report(Shape(ModelInstallPhase.Verifying, length, length, null, resumed: true));
                return (new InstalledFileDigest { FileName = file.FileName, Sha256 = existing, SizeBytes = length }, true);
            }

            File.Delete(finalPath);
        }

        var partPath = finalPath + PartSuffix;
        var metaPath = finalPath + PartSuffix + ".json";

        // **A dropped connection is retried here, and until 2026-08-29 it took the process with
        // it.** Hugging Face ended a response after 149 KB of a 6.3 GB file; `HttpIOException`
        // came out of the read loop, matched neither of the window's two catch clauses, and
        // terminated the application from inside an async command. Two things were wrong and this
        // is the first: a transport failure is the *expected* case over hours of downloading, and
        // everything needed to survive it — the `.part` file, the resume metadata, the range
        // request — was already built and simply never used for this.
        //
        // **Retrying costs nothing because it resumes.** Each attempt re-reads the offset off
        // disk, so a connection that died at 149 KB asks for `Range: bytes=149148-` rather than
        // starting again. An attempt that makes progress resets the budget, which is what stops a
        // slow, flaky link from exhausting the count while it is still moving forward.
        // Carried out of the attempt so the verification below reads what the *successful*
        // attempt saw: `resumed` is reported to the caller, `total` is what the length and digest
        // checks are made against.
        long total = 0;
        var resumed = false;

        const int MaxAttempts = 5;
        var attempt = 0;
        while (true)
        {
            var resumeOffsetBefore = DetermineResumeOffset(partPath, metaPath, file.Url);
            try
            {
                await AttemptAsync(resumeOffsetBefore).ConfigureAwait(false);
                break;
            }
            catch (Exception exception) when (IsTransient(exception) && !ct.IsCancellationRequested)
            {
                // Progress since the last attempt buys a fresh budget rather than counting against
                // it: the failure being retried is a connection that dies periodically, not a
                // request that cannot be served.
                var after = DetermineResumeOffset(partPath, metaPath, file.Url);
                if (after > resumeOffsetBefore)
                {
                    attempt = 0;
                }

                if (++attempt >= MaxAttempts)
                {
                    throw new ModelInstallException(
                        $"The download of {file.FileName} kept being cut off — {MaxAttempts} attempts, " +
                        $"the last after {after:N0} of {file.SizeBytes ?? 0:N0} bytes. What arrived is " +
                        "kept, so starting again resumes rather than restarting. " +
                        exception.Message,
                        exception);
                }

                // Short, growing, and capped. **Starting at 500 ms rather than 2 s** because the
                // failure being waited out is usually a single cut response rather than a service
                // that is down: the first retry succeeding half a second later is invisible to
                // somebody watching a progress bar, where two seconds reads as a stall. The cap is
                // what covers the other case, and the attempt budget is what ends it.
                var backoff = TimeSpan.FromMilliseconds(Math.Min(8000, 250 * (1 << attempt)));
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
        }

        async Task AttemptAsync(long resumeOffset)
        {
            resumed = resumeOffset > 0;

        progress?.Report(Shape(ModelInstallPhase.Connecting, resumeOffset, file.SizeBytes, null, resumed));

        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
        }

        // <b>Gated entries need the user's own token, and only Hugging Face ever sees it.</b>
        // Everything in the catalogue downloads anonymously except the pyannote pipeline, whose
        // repository requires an accepted user agreement — an unauthenticated fetch of it returns
        // 401 rather than the file. See <see cref="HuggingFaceHost"/> for why the host is checked
        // here rather than trusted from the entry.
        if (IsHuggingFace(file.Url) && _huggingFaceToken?.Invoke() is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await SendAsync(request, model, file, ct).ConfigureAwait(false);

        if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent)
        {
            total = response.Content.Headers.ContentRange?.Length
                ?? (response.Content.Headers.ContentLength is { } length ? resumeOffset + length : 0);
        }
        else
        {
            // The server ignored the range (or there was nothing to resume): start over rather
            // than append to a prefix that may not match.
            resumeOffset = 0;
            resumed = false;
            total = response.Content.Headers.ContentLength ?? 0;
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }
        }

        if (total == 0 && file.SizeBytes is { } expectedSize)
        {
            total = expectedSize;
        }

        WriteResumeMetadata(metaPath, file.Url, response.Headers.ETag?.ToString(), total);

        await DownloadAsync(response, partPath, resumeOffset, resumed, Shape, progress, total, ct).ConfigureAwait(false);
        }

        var actualSize = new FileInfo(partPath).Length;
        progress?.Report(Shape(ModelInstallPhase.Verifying, actualSize, total > 0 ? total : null, null, resumed));

        var actualSha = await ComputeSha256Async(partPath, ct).ConfigureAwait(false);

        if (file.SizeBytes is { } pinnedSize && pinnedSize != actualSize)
        {
            // Discarded, exactly as a digest mismatch below is, and for a reason the digest branch
            // does not have to spell out. A complete `.part` whose length disagrees with the pin is
            // a file the resume path will treat as finished: the next attempt reads its length from
            // the metadata, asks for `Range: bytes=<length>-`, and any server honouring Range
            // answers 416. The user then gets "returned 416 RequestedRangeNotSatisfiable" — which
            // names nothing about the real cause — on this attempt and on every attempt after it,
            // including from the application's Download button, with no way out but deleting a file
            // in a directory they were never told about.
            File.Delete(partPath);
            DeleteIfExists(metaPath);
            throw new ModelInstallException(
                $"Model '{model.Id}' downloaded {actualSize} bytes{Where(model, file)} but the catalogue " +
                $"pins {pinnedSize}. The manifest and the remote file disagree; the partial download was " +
                "discarded and nothing was installed.");
        }

        if (file.Sha256 is { } expected && !string.Equals(actualSha, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            DeleteIfExists(metaPath);
            throw new ModelInstallException(
                $"Model '{model.Id}' failed verification{Where(model, file)}: expected SHA-256 {expected}, " +
                $"got {actualSha}. The partial download was discarded.");
        }

        progress?.Report(Shape(ModelInstallPhase.Installing, actualSize, actualSize, null, resumed));

        File.Move(partPath, finalPath, overwrite: true);
        DeleteIfExists(metaPath);

        return (new InstalledFileDigest { FileName = file.FileName, Sha256 = actualSha, SizeBytes = actualSize }, resumed);
    }

    /// <summary>
    /// The clause that says which file an error is about, and nothing at all when there is only
    /// one. A single-file entry's messages read exactly as they always did — naming "its file" when
    /// the entry has exactly one would be words that carry no information — while a multi-file
    /// entry never reports a failure that leaves the reader guessing which of nine it was.
    /// </summary>
    private static string Where(ModelDescriptor model, ModelFile file) =>
        model.Files.Count == 1 ? string.Empty : $" of '{file.FileName}'";

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Whether a URL is Hugging Face itself, and so may carry the user's token.</summary>
    /// <remarks>
    /// <para>
    /// Matched on the host rather than with <c>StartsWith</c> on the string, because
    /// <c>https://huggingface.co.example.com/</c> starts with the same characters and is somebody
    /// else's server. The subdomain arm is a suffix test against <c>.huggingface.co</c>, which
    /// cannot be satisfied by a host that merely contains it.
    /// </para>
    /// <para>
    /// <b>The redirect to the CDN is not this method's problem, and that is worth knowing rather
    /// than rediscovering.</b> Hugging Face answers a file request with a redirect to a signed URL
    /// on another host. <b>.NET's redirect handler clears <c>Authorization</c> on every automatic
    /// redirect it follows, not only cross-origin ones</b> — stated precisely because the weaker
    /// "cross-origin only" version was written here first and would matter if it were relied on:
    /// the token reaches the first request and nothing after it. That is the safe direction, and it
    /// is why the CDN leg works anyway — the signature in the redirect target authorises it.
    /// </para>
    /// </remarks>
    internal static bool IsHuggingFace(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps
        && (string.Equals(url.Host, HuggingFaceHost, StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith("." + HuggingFaceHost, StringComparison.OrdinalIgnoreCase));

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, ModelDescriptor model, ModelFile file, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelInstallException($"Could not reach {file.Url} to download '{model.Id}': {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();

        if (status == HttpStatusCode.NotFound && !model.Verified)
        {
            throw new ModelInstallException(
                $"{file.Url} returned 404 for '{model.Id}'. This catalogue entry is marked unverified: its file name " +
                "and URL were never checked against the live repository. Fix the entry in models.json (docs/MODELS.md " +
                "explains how) rather than retrying.");
        }

        // <b>A gated repository answers 401 or 403, and a bare status code sends the reader looking
        // in the wrong place.</b> Neither number means "this download is broken": they mean the user
        // has not accepted the model's terms, or has no token, or has one without access. None of
        // that is fixable by retrying, and the remedy is three steps on a website rather than
        // anything in this application — so the message names them.
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && IsHuggingFace(file.Url))
        {
            var hasToken = _huggingFaceToken?.Invoke() is { Length: > 0 };
            throw new ModelInstallException(
                $"{file.Url} returned {(int)status} {status} for '{model.Id}'. This model is only handed out to " +
                "people who have accepted its terms. Open the model's page, accept them, then " +
                (hasToken
                    ? "check that the access token in Settings belongs to that account and can read gated repositories."
                    : "create a read-only access token on your Hugging Face account and paste it into Settings " +
                      $"(or set {HuggingFaceToken.PrimaryVariable} in the environment).") +
                " Retrying without doing that will fail the same way.");
        }

        throw new ModelInstallException($"{file.Url} returned {(int)status} {status} for '{model.Id}'.");
    }

    private static async Task DownloadAsync(
        HttpResponseMessage response,
        string partPath,
        long resumeOffset,
        bool resumed,
        Func<ModelInstallPhase, long, long?, double?, bool, ModelInstallProgress> shape,
        IProgress<ModelInstallProgress>? progress,
        long total,
        CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = new FileStream(
            partPath,
            resumeOffset > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous);

        var buffer = new byte[BufferSize];
        var completed = resumeOffset;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            completed += read;

            var elapsed = stopwatch.Elapsed;
            if (progress is not null && elapsed - lastReport > TimeSpan.FromMilliseconds(200))
            {
                lastReport = elapsed;
                var moved = completed - resumeOffset;
                progress.Report(shape(
                    ModelInstallPhase.Downloading,
                    completed,
                    total > 0 ? total : null,
                    elapsed.TotalSeconds > 0 ? moved / elapsed.TotalSeconds : null,
                    resumed));
            }
        }

        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a failure is the connection rather than the request, and so worth another attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately narrow.</b> A 404, a 401 on a gated repository, a digest that disagrees and
    /// a disk with no room left are all failures that will fail again in four seconds, and retrying
    /// them turns a clear message into the same message five times slower.
    /// <see cref="ModelInstallException"/> is excluded for exactly that reason: it is this class's
    /// own word for "this is settled".
    /// </para>
    /// <para>
    /// <b><see cref="HttpIOException"/> is the case that prompted this</b> and it is not obvious
    /// from its name: it derives from <see cref="IOException"/>, so it is caught by the clause
    /// below, and a response that ends early — which is what Hugging Face did after 149 KB of a
    /// 6.3 GB file on 2026-08-29 — arrives as one. <see cref="TaskCanceledException"/> is included
    /// only when the caller's own token is not the reason: an <see cref="HttpClient"/> timeout
    /// presents as a cancellation with nobody having cancelled anything, and the caller's real
    /// cancellation is filtered out at the catch site rather than here.
    /// </para>
    /// </remarks>
    private static bool IsTransient(Exception exception) => exception switch
    {
        ModelInstallException => false,
        OperationCanceledException => true,
        HttpRequestException => true,
        IOException => true,
        _ => false,
    };

    private static long DetermineResumeOffset(string partPath, string metaPath, Uri url)
    {
        if (!File.Exists(partPath))
        {
            DeleteIfExists(metaPath);
            return 0;
        }

        // Resuming against a different URL would splice two files together and produce a blob
        // that hashes to nothing anyone expects.
        if (!File.Exists(metaPath))
        {
            File.Delete(partPath);
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metaPath));
            var storedUrl = document.RootElement.TryGetProperty("url", out var value) ? value.GetString() : null;
            if (!string.Equals(storedUrl, url.ToString(), StringComparison.Ordinal))
            {
                File.Delete(partPath);
                File.Delete(metaPath);
                return 0;
            }
        }
        catch (JsonException)
        {
            File.Delete(partPath);
            File.Delete(metaPath);
            return 0;
        }

        return new FileInfo(partPath).Length;
    }

    private static void WriteResumeMetadata(string metaPath, Uri url, string? etag, long total)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["url"] = url.ToString(),
            ["etag"] = etag,
            ["totalBytes"] = total > 0 ? total.ToString(CultureInfo.InvariantCulture) : null,
        });

        File.WriteAllText(metaPath, json);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private InstalledModel Describe(ModelDescriptor model)
    {
        var path = _store.PathFor(model);
        return new InstalledModel
        {
            Id = model.Id,
            Path = path,
            SizeBytes = model.Files.Sum(file => new FileInfo(_store.PathFor(model, file)).Length),
            Descriptor = model,
        };
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
