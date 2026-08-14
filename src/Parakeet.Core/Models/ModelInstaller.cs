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

    public long BytesCompleted { get; init; }

    public long? TotalBytes { get; init; }

    public double? BytesPerSecond { get; init; }

    public bool Resumed { get; init; }

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
    /// Install a model whose manifest entry carries no SHA-256. Off by default: a 670 MB blob
    /// pulled over the network and loaded into the process with no integrity check is not
    /// something to do quietly.
    /// </summary>
    public bool AllowUnverified { get; init; }

    /// <summary>Re-download even when the file is already present.</summary>
    public bool Force { get; init; }
}

public sealed record ModelInstallResult
{
    public required InstalledModel Model { get; init; }

    /// <summary>Digest actually computed over the downloaded bytes.</summary>
    public required string Sha256 { get; init; }

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
public sealed class ModelInstaller : IDisposable
{
    private const int BufferSize = 1 << 20;

    private readonly IModelStore _store;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ModelInstaller(IModelStore store, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

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

        if (model.Sha256 is null && !options.AllowUnverified)
        {
            throw new ModelInstallException(
                $"Model '{model.Id}' has no pinned SHA-256 in the catalogue, so a download cannot be verified. " +
                "Pin the digest in models.json (see docs/MODELS.md) or install with the explicit unverified opt-in.");
        }

        var finalPath = _store.PathFor(model);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        if (File.Exists(finalPath) && !options.Force)
        {
            var digest = await ComputeSha256Async(finalPath, ct).ConfigureAwait(false);
            if (model.Sha256 is null || string.Equals(digest, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new ModelInstallProgress
                {
                    Phase = ModelInstallPhase.Completed,
                    BytesCompleted = new FileInfo(finalPath).Length,
                    TotalBytes = new FileInfo(finalPath).Length,
                });

                return new ModelInstallResult
                {
                    Model = Describe(model, finalPath),
                    Sha256 = digest,
                    AlreadyPresent = true,
                };
            }

            // A file on disk that does not match the pinned digest is a corrupt or tampered
            // install. Replacing it is the only safe outcome, and saying so is mandatory.
            File.Delete(finalPath);
        }

        var partPath = finalPath + ".part";
        var metaPath = finalPath + ".part.json";
        var resumeOffset = DetermineResumeOffset(partPath, metaPath, model.Url);
        var resumed = resumeOffset > 0;

        progress?.Report(new ModelInstallProgress
        {
            Phase = ModelInstallPhase.Connecting,
            BytesCompleted = resumeOffset,
            TotalBytes = model.SizeBytes,
            Resumed = resumed,
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
        }

        using var response = await SendAsync(request, model, ct).ConfigureAwait(false);

        long total;
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

        if (total == 0 && model.SizeBytes is { } expectedSize)
        {
            total = expectedSize;
        }

        WriteResumeMetadata(metaPath, model.Url, response.Headers.ETag?.ToString(), total);

        await DownloadAsync(response, partPath, resumeOffset, total, resumed, progress, ct).ConfigureAwait(false);

        progress?.Report(new ModelInstallProgress
        {
            Phase = ModelInstallPhase.Verifying,
            BytesCompleted = new FileInfo(partPath).Length,
            TotalBytes = total > 0 ? total : null,
        });

        var actualSha = await ComputeSha256Async(partPath, ct).ConfigureAwait(false);
        var actualSize = new FileInfo(partPath).Length;

        if (model.SizeBytes is { } pinnedSize && pinnedSize != actualSize)
        {
            throw new ModelInstallException(
                $"Model '{model.Id}' downloaded {actualSize} bytes but the catalogue pins {pinnedSize}. " +
                "The manifest and the remote file disagree; nothing was installed.");
        }

        if (model.Sha256 is { } expected && !string.Equals(actualSha, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            DeleteIfExists(metaPath);
            throw new ModelInstallException(
                $"Model '{model.Id}' failed verification: expected SHA-256 {expected}, got {actualSha}. " +
                "The partial download was discarded.");
        }

        progress?.Report(new ModelInstallProgress
        {
            Phase = ModelInstallPhase.Installing,
            BytesCompleted = actualSize,
            TotalBytes = actualSize,
        });

        File.Move(partPath, finalPath, overwrite: true);
        DeleteIfExists(metaPath);

        progress?.Report(new ModelInstallProgress
        {
            Phase = ModelInstallPhase.Completed,
            BytesCompleted = actualSize,
            TotalBytes = actualSize,
            Resumed = resumed,
        });

        return new ModelInstallResult
        {
            Model = Describe(model, finalPath),
            Sha256 = actualSha,
            Resumed = resumed,
        };
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, ModelDescriptor model, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelInstallException($"Could not reach {model.Url} to download '{model.Id}': {ex.Message}", ex);
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
                $"{model.Url} returned 404 for '{model.Id}'. This catalogue entry is marked unverified: its file name " +
                "and URL were never checked against the live repository. Fix the entry in models.json (docs/MODELS.md " +
                "explains how) rather than retrying.");
        }

        throw new ModelInstallException($"{model.Url} returned {(int)status} {status} for '{model.Id}'.");
    }

    private static async Task DownloadAsync(
        HttpResponseMessage response,
        string partPath,
        long resumeOffset,
        long total,
        bool resumed,
        IProgress<ModelInstallProgress>? progress,
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
                progress.Report(new ModelInstallProgress
                {
                    Phase = ModelInstallPhase.Downloading,
                    BytesCompleted = completed,
                    TotalBytes = total > 0 ? total : null,
                    BytesPerSecond = elapsed.TotalSeconds > 0 ? moved / elapsed.TotalSeconds : null,
                    Resumed = resumed,
                });
            }
        }

        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

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

    private static InstalledModel Describe(ModelDescriptor model, string path) => new()
    {
        Id = model.Id,
        Path = path,
        SizeBytes = new FileInfo(path).Length,
        Descriptor = model,
    };

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
