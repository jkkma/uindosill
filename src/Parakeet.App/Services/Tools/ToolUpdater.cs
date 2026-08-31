using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Parakeet.Core.Models;

namespace Parakeet.App.Services.Tools;

/// <summary>Which of the two tools an operation is about.</summary>
public enum UpdatableTool
{
    YtDlp,
    Deno,
}

/// <summary>How a publisher writes the digest beside its release.</summary>
/// <remarks>
/// Two publishers, two formats, and neither is a choice this project gets to make. yt-dlp ships a
/// combined <c>SHA2-256SUMS</c> in the ordinary <c>&lt;hash&gt;  &lt;name&gt;</c> shape; Deno ships
/// a per-asset file that is PowerShell's <c>Get-FileHash | Format-List</c> output, with the digest
/// on a <c>Hash      : </c> line in upper case. Both were read off the real releases on 2026-08-29.
/// </remarks>
public enum ChecksumFormat
{
    /// <summary>Lines of <c>&lt;hash&gt;  &lt;filename&gt;</c>; the matching line is found by name.</summary>
    Sha256Sums,

    /// <summary>PowerShell <c>Format-List</c> output; the digest follows <c>Hash</c>.</summary>
    PowerShellFormatList,
}

/// <summary>What a tool is now and what is published.</summary>
public sealed record ToolStatus
{
    public required UpdatableTool Tool { get; init; }

    /// <summary>What the binary on disk says it is, or null when it could not be asked.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>The publisher's latest tag, or null when the check has not run or failed.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>Why the check could not answer, when it could not.</summary>
    public string? Problem { get; init; }

    /// <summary>Whether the installed copy came from an update rather than the build.</summary>
    public bool FromUserData { get; init; }

    /// <summary>
    /// True only when both versions are known and they differ.
    /// </summary>
    /// <remarks>
    /// <b>Differ, not "is older".</b> yt-dlp versions are dates and Deno's are semantic, and a
    /// comparison that had to understand both would be two parsers written to answer a question the
    /// publisher already answers: the tag on the latest release <i>is</i> the newest one. An
    /// installed copy ahead of it — a nightly, or a hand-placed build — reports as different rather
    /// than as current. Note what the one-button flow then does with that: pressing "Update yt-dlp
    /// and Deno" replaces it with the publisher's release under an "Updating…" message, so an ahead
    /// copy is downgraded by pressing it — there is no separate reinstall label.
    /// </remarks>
    public bool UpdateAvailable =>
        InstalledVersion is { Length: > 0 } installed
        && LatestVersion is { Length: > 0 } latest
        && !string.Equals(Normalise(installed), Normalise(latest), StringComparison.OrdinalIgnoreCase);

    /// <summary>Deno tags itself <c>v2.9.6</c> and reports <c>2.9.6</c>; the v is not a version difference.</summary>
    private static string Normalise(string version) => version.Trim().TrimStart('v', 'V');
}

/// <summary>Downloads newer yt-dlp and Deno binaries, verified against the publisher's own digest.</summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, when everything else in this repository is pinned.</b> yt-dlp is the
/// one vendored binary whose pin has a shelf life measured in weeks: YouTube changes what it serves
/// and yt-dlp changes to match, so the version that worked at release is routinely the version that
/// does not work by the time somebody installs it. Deno goes with it because yt-dlp needs a
/// JavaScript runtime for YouTube's signature challenge and that interface moves too. A pinned
/// yt-dlp is eventually a broken one, and telling a user to reinstall the application to fix a
/// download is not an answer.
/// </para>
/// <para>
/// <b>What replaces the pin is the publisher's own checksum, not nothing.</b> Both projects publish
/// a digest beside every release asset, so an update is still verified — against the release rather
/// than against a hash committed here. That is weaker than `vendor-tools.ps1`'s pin and it is
/// deliberately weaker: a hash this repository has never seen cannot be checked against a hash this
/// repository committed. What it does rule out is the failure that matters — a truncated or
/// tampered download being written over a working tool — and it fails closed, leaving what was
/// there.
/// </para>
/// <para>
/// <b>Nothing is ever written into the application directory.</b> Updates land in
/// <c>%LOCALAPPDATA%\Uindosill\tools</c>, which <see cref="BundledTools"/> searches <i>before</i>
/// the vendored copy. The installed build stays exactly as it shipped, an application update cannot
/// silently revert a tool the user fixed, and deleting that one directory restores the pinned
/// binaries. Writing into Program Files would need elevation and would be undone by the next
/// Velopack update besides.
/// </para>
/// </remarks>
public sealed class ToolUpdater(HttpClient? http = null) : IDisposable
{
    private readonly HttpClient _http = http ?? CreateClient();
    private readonly bool _ownsHttp = http is null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // GitHub's API refuses a request with no user agent. Named rather than generic so a rate
        // limit or an abuse report can be traced to this application rather than to "unknown".
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Uindosill", "1.0"));
        return client;
    }

    /// <summary>Where an updated copy goes, ahead of the vendored one on the search.</summary>
    public static string UserToolsDirectory =>
        Path.Combine(UserDataPaths.RootDirectory(), "tools");

    private sealed record Source(
        string Repository,
        string AssetName,
        string ChecksumAssetName,
        ChecksumFormat Format,
        string ExecutableName,
        bool IsZip);

    private static Source SourceFor(UpdatableTool tool) => tool switch
    {
        UpdatableTool.YtDlp => new Source(
            "yt-dlp/yt-dlp", "yt-dlp.exe", "SHA2-256SUMS",
            ChecksumFormat.Sha256Sums, "yt-dlp.exe", IsZip: false),

        UpdatableTool.Deno => new Source(
            "denoland/deno", "deno-x86_64-pc-windows-msvc.zip",
            "deno-x86_64-pc-windows-msvc.zip.sha256sum",
            ChecksumFormat.PowerShellFormatList, "deno.exe", IsZip: true),

        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    /// <summary>The path the tool resolves to today, or null when this build has neither copy.</summary>
    public static string? PathFor(UpdatableTool tool) => tool switch
    {
        UpdatableTool.YtDlp => BundledTools.YtDlpPath,
        UpdatableTool.Deno => BundledTools.DenoPath,
        _ => null,
    };

    /// <summary>
    /// Asks the binary what version it is, rather than remembering what was installed.
    /// </summary>
    /// <remarks>
    /// <b>The binary is the authority and a stored string is not.</b> A tool can be replaced by
    /// hand, restored by an application update, or left half-written by an interrupted one, and in
    /// every case a remembered version would describe something that is no longer on disk. This is
    /// two process starts on a Settings page rather than a value carried in a settings file that
    /// can be wrong.
    /// </remarks>
    public static async Task<string?> InstalledVersionAsync(UpdatableTool tool, CancellationToken ct = default)
    {
        var path = PathFor(tool);
        if (path is null)
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // Deno answers "deno 2.9.6 (stable, release, x86_64-pc-windows-msvc)" over several
            // lines; yt-dlp answers with the bare date. The first token of the first line is the
            // version in one case and the word "deno" in the other, so both are handled by taking
            // the first line and dropping a leading program name.
            var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(first))
            {
                return null;
            }

            var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 && !char.IsDigit(parts[0][0]) ? parts[1] : parts[0];
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Where the tool that would run right now came from.</summary>
    public static bool IsFromUserData(UpdatableTool tool)
    {
        var path = PathFor(tool);
        return path is not null
            && path.StartsWith(UserToolsDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What is installed and what is published, without changing anything.</summary>
    public async Task<ToolStatus> CheckAsync(UpdatableTool tool, CancellationToken ct = default)
    {
        var installed = await InstalledVersionAsync(tool, ct).ConfigureAwait(false);
        var fromUserData = IsFromUserData(tool);

        try
        {
            var (tag, _, _) = await LatestAsync(tool, ct).ConfigureAwait(false);
            return new ToolStatus
            {
                Tool = tool,
                InstalledVersion = installed,
                LatestVersion = tag,
                FromUserData = fromUserData,
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                              or JsonException or InvalidOperationException)
        {
            return new ToolStatus
            {
                Tool = tool,
                InstalledVersion = installed,
                FromUserData = fromUserData,
                Problem = exception.Message,
            };
        }
    }

    private async Task<(string Tag, Uri Asset, Uri Checksum)> LatestAsync(
        UpdatableTool tool, CancellationToken ct)
    {
        var source = SourceFor(tool);
        var url = $"https://api.github.com/repos/{source.Repository}/releases/latest";

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidOperationException($"{source.Repository} published a release with no tag.");

        Uri? asset = null;
        Uri? checksum = null;
        foreach (var candidate in root.GetProperty("assets").EnumerateArray())
        {
            var name = candidate.GetProperty("name").GetString();
            var download = candidate.GetProperty("browser_download_url").GetString();
            if (name is null || download is null)
            {
                continue;
            }

            if (name == source.AssetName) { asset = new Uri(download); }
            if (name == source.ChecksumAssetName) { checksum = new Uri(download); }
        }

        if (asset is null || checksum is null)
        {
            // **Refused rather than downloaded unverified.** A release that stops publishing its
            // digest is exactly when an unchecked download is least advisable, and the tool that is
            // already installed still works.
            throw new InvalidOperationException(
                $"{source.Repository} release {tag} does not publish both {source.AssetName} and " +
                $"{source.ChecksumAssetName}, so an update could not be checked against anything.");
        }

        return (tag, asset, checksum);
    }

    /// <summary>
    /// Downloads the published release, verifies it, and installs it into the user tools directory.
    /// </summary>
    /// <returns>The tag installed.</returns>
    public async Task<string> UpdateAsync(
        UpdatableTool tool,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var source = SourceFor(tool);
        progress?.Report("Asking the publisher what is current…");
        var (tag, assetUrl, checksumUrl) = await LatestAsync(tool, ct).ConfigureAwait(false);

        progress?.Report($"Downloading {tag}…");
        var payload = await _http.GetByteArrayAsync(assetUrl, ct).ConfigureAwait(false);

        progress?.Report("Checking it against the publisher's digest…");
        var expected = ParseChecksum(
            await _http.GetStringAsync(checksumUrl, ct).ConfigureAwait(false),
            source.Format,
            source.AssetName);

        var actual = Convert.ToHexStringLower(SHA256.HashData(payload));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The {source.AssetName} that arrived hashes to {actual} and {source.Repository} " +
                $"publishes {expected}. Nothing was installed and the tool you have is untouched.");
        }

        progress?.Report("Installing…");
        Directory.CreateDirectory(UserToolsDirectory);
        var destination = Path.Combine(UserToolsDirectory, source.ExecutableName);

        // Staged beside the destination and moved onto it, so an interruption cannot leave a
        // half-written executable where a working one was. The tool is running from here the next
        // time something spawns it, and a truncated yt-dlp.exe is worse than an out-of-date one.
        var staging = destination + ".incoming";
        DeleteIfExists(staging);

        if (source.IsZip)
        {
            ExtractExecutable(payload, source.ExecutableName, staging);
        }
        else
        {
            await File.WriteAllBytesAsync(staging, payload, ct).ConfigureAwait(false);
        }

        File.Move(staging, destination, overwrite: true);
        return tag;
    }

    private static void ExtractExecutable(byte[] archive, string executableName, string destination)
    {
        using var stream = new MemoryStream(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var entry = zip.Entries.FirstOrDefault(
            e => string.Equals(Path.GetFileName(e.FullName), executableName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The archive does not contain {executableName}, so there was nothing to install.");

        entry.ExtractToFile(destination, overwrite: true);
    }

    /// <summary>Pulls the digest out of whichever shape the publisher writes.</summary>
    internal static string ParseChecksum(string content, ChecksumFormat format, string assetName)
    {
        switch (format)
        {
            case ChecksumFormat.Sha256Sums:
                foreach (var line in content.Split('\n'))
                {
                    var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2
                        && string.Equals(parts[^1], assetName, StringComparison.OrdinalIgnoreCase))
                    {
                        return parts[0];
                    }
                }

                throw new InvalidOperationException(
                    $"The published checksums do not mention {assetName}.");

            case ChecksumFormat.PowerShellFormatList:
                foreach (var line in content.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Hash", StringComparison.OrdinalIgnoreCase)
                        && trimmed.Contains(':', StringComparison.Ordinal))
                    {
                        return trimmed[(trimmed.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                    }
                }

                throw new InvalidOperationException(
                    "The published checksum file has no Hash line, so nothing could be verified.");

            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    private static void DeleteIfExists(string path)
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
