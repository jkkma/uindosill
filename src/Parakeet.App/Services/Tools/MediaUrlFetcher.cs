using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Parakeet.App.Services.Tools;

/// <summary>What a fetch produced: the audio on disk, and where it came from.</summary>
/// <param name="Path">The downloaded audio file.</param>
/// <param name="Title">The title the site gave it, which is what the queue shows.</param>
/// <param name="SourceUrl">The link, kept so the Ask tab can stream the picture from it.</param>
public sealed record FetchedMedia(string Path, string Title, string SourceUrl);

/// <summary>How a fetch is going, for the row in the queue.</summary>
/// <param name="Fraction">0 to 1, or null while the stage cannot report one.</param>
/// <param name="Stage">What is happening, in words a person reads.</param>
public readonly record struct UrlFetchProgress(double? Fraction, string Stage);

/// <summary>Thrown when a link cannot be fetched, carrying a reason a person can act on.</summary>
public sealed class MediaFetchException : Exception
{
    public MediaFetchException(string message)
        : base(message)
    {
    }

    public MediaFetchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MediaFetchException()
    {
    }
}

/// <summary>Downloads the audio track of a link, so it can be transcribed like a file.</summary>
public interface IMediaUrlFetcher
{
    /// <summary>Whether this build can fetch links at all.</summary>
    bool IsAvailable { get; }

    /// <summary>Why not, or null when it can.</summary>
    string? DescribeUnavailable();

    /// <summary>
    /// Downloads <paramref name="url"/>'s audio into a directory of its own under
    /// <paramref name="root"/> and returns what landed there.
    /// </summary>
    /// <exception cref="MediaFetchException">The link could not be fetched, and the message says why.</exception>
    Task<FetchedMedia> FetchAudioAsync(
        string url,
        string root,
        IProgress<UrlFetchProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The real one: the vendored yt-dlp, run as a child process, asked for the audio track alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audio only, and that is the whole design.</b> A link is transcribed from its sound, and the
/// picture — if anyone wants it — is streamed on demand by the Ask tab rather than downloaded. So a
/// three-hour video costs a few megabytes here instead of a few gigabytes, and the file this leaves
/// on disk is the same shape as one the user could have dropped in themselves.
/// </para>
/// <para>
/// <b>The format selector prefers m4a for a measured reason.</b> YouTube's best audio is usually
/// Opus in WebM, which `AudioSources.SupportedExtensions` does not list and Media Foundation cannot
/// decode on a stock Windows install — so a "best audio" download would produce a file this
/// application then refuses. Asking for m4a first gets AAC, which Media Foundation reads. When
/// ffmpeg is absent yt-dlp writes a DASH m4a and warns that "only some players support this
/// container"; both readers here were checked against one on 2026-08-23 and both open it, so the
/// warning did not apply and no ffmpeg was vendored for it.
/// </para>
/// <para>
/// <b>One is vendored now, for the muxer, and this hands it the location deliberately.</b> Measured
/// on the same video the same day: without it yt-dlp writes `ftyp iso6` with a `dash` brand and
/// fragment boxes; with it, `[FixupM4a] Correcting container` and a plain `isom` MP4, 6,637 bytes
/// smaller, with the audio samples bit-identical and this application's own reader returning the
/// same 26,306,560 samples either way. So the fixup buys nothing here — and it buys the person who
/// opens the downloaded file in something else a container that is not the one yt-dlp warns about,
/// which is the whole reason it is on. See `docs/UNPROVEN.md`.
/// </para>
/// <para>
/// <b>Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never a joined string.</b>
/// The URL comes from whatever the user pasted; the list form hands each argument to the child
/// without a shell and without quoting rules to get wrong.
/// </para>
/// <para>
/// Nothing in the suite runs this class — it needs the vendored tools and a network — so the tests
/// drive <see cref="FakeMediaUrlFetcher"/> and this is covered by driving it by hand. See
/// `docs/UNPROVEN.md` § <i>Fetching a link</i>.
/// </para>
/// </remarks>
public sealed partial class YtDlpMediaUrlFetcher : IMediaUrlFetcher
{
    /// <summary>`[download]  12.3% of ...`, which is what `--newline` emits.</summary>
    [GeneratedRegex(@"^\[download\]\s+(?<percent>\d{1,3}(?:\.\d+)?)%", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadProgress { get; }

    public bool IsAvailable => BundledTools.CanFetchUrls;

    public string? DescribeUnavailable() => BundledTools.DescribeUnavailable();

    public async Task<FetchedMedia> FetchAudioAsync(
        string url,
        string root,
        IProgress<UrlFetchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (BundledTools.YtDlpPath is not { } ytDlp || BundledTools.DenoPath is not { } deno)
        {
            throw new MediaFetchException(DescribeUnavailable() ?? "Uindosill cannot open links.");
        }

        // http and https only. Not defensive tidiness: yt-dlp accepts local paths and other
        // schemes, and this is the one place a string that arrived from the clipboard turns into
        // an argument to a process.
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new MediaFetchException("That is not a http or https link.");
        }

        // A directory of its own per fetch, which is what makes finding the result a directory
        // listing rather than a parse of yt-dlp's output interleaved with its own progress.
        var into = Path.Combine(root, "url-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(into);

        progress?.Report(new UrlFetchProgress(null, "Reading the link"));

        var start = new ProcessStartInfo
        {
            FileName = ytDlp,
            WorkingDirectory = into,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Named explicitly rather than found. yt-dlp searches its own directory before PATH, so a
        // muxer dropped beside it would be picked up by accident — measured 2026-08-23, and the
        // reason ffmpeg is vendored to a directory of its own. Passing the location says the
        // opposite: this download is meant to have one, and which one.
        var muxer = BundledTools.FfmpegPath is { } ffmpeg
            ? new[] { "--ffmpeg-location", ffmpeg }
            : [];

        foreach (var argument in new[]
        {
            // Deno by absolute path rather than left to PATH: this process spawns yt-dlp directly,
            // so it can be exact, and a different runtime installed on the machine cannot decide
            // how a download behaves. YouTube needs one at all — yt-dlp enables only deno by
            // default and uses it for the signature challenge.
            "--js-runtimes", $"deno:{deno}",

            // A link with a list= parameter is one video to the person who pasted it. Without this
            // it is however many the playlist holds.
            "--no-playlist",

            // AAC first, for the reason the remarks give: Opus in WebM is what "best" usually means
            // and is not something this pipeline can open.
            "-f", "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio",

            // Progress on its own lines rather than redrawn with carriage returns, which is what
            // makes it parseable at all.
            "--newline",
            "--no-colors",

            // No archive, no config, no plugins: this run must not depend on or write to whatever
            // the user has set up for their own yt-dlp.
            "--ignore-config",
            "--no-plugin-dirs",

            // 80 bytes of title keeps a long one inside MAX_PATH once the directory above is added.
            "-o", "%(title).80B.%(ext)s",
        }.Concat(muxer).Append(url.Trim()))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };

        var errors = new List<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line)
            {
                return;
            }

            if (DownloadProgress.Match(line) is { Success: true } match
                && double.TryParse(match.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                progress?.Report(new UrlFetchProgress(percent / 100, "Downloading the audio"));
            }
            else if (line.StartsWith("[youtube]", StringComparison.Ordinal) || line.StartsWith("[info]", StringComparison.Ordinal))
            {
                progress?.Report(new UrlFetchProgress(null, "Reading the link"));
            }
        };

        // Kept rather than shown as they arrive: yt-dlp writes warnings here that are not failures,
        // and the last few lines are what explain an exit code that is.
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                errors.Add(line);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new MediaFetchException($"Could not start yt-dlp: {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The whole tree, because yt-dlp spawns Deno and may spawn a downloader of its own; a
            // cancel that left either running would keep writing into a directory we are about to
            // delete.
            TryKill(process);
            TryDelete(into);
            throw;
        }

        if (process.ExitCode != 0)
        {
            TryDelete(into);

            var detail = errors.Count > 0
                ? string.Join(" ", errors.TakeLast(3))
                : $"yt-dlp exited {process.ExitCode}.";

            throw new MediaFetchException($"Could not fetch that link. {detail}");
        }

        // Whatever landed, minus yt-dlp's own partial files. There is exactly one on a successful
        // single-video run; the ordering is a backstop rather than an expectation.
        var produced = new DirectoryInfo(into).GetFiles()
            .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                     && !f.Name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();

        if (produced is null)
        {
            TryDelete(into);
            throw new MediaFetchException("yt-dlp reported success but left no audio file.");
        }

        progress?.Report(new UrlFetchProgress(1, "Downloaded"));

        return new FetchedMedia(
            produced.FullName,
            Path.GetFileNameWithoutExtension(produced.Name),
            uri.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
#pragma warning disable CA1031 // A process that has already gone is the outcome this wanted.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
#pragma warning disable CA1031 // A leftover temporary directory is not worth failing a fetch over.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}

/// <summary>
/// A fetcher that writes a file without a network, so the window's link path runs in the tests.
/// </summary>
public sealed class FakeMediaUrlFetcher : IMediaUrlFetcher
{
    /// <summary>When set, every fetch fails with this message.</summary>
    public string? RefuseWith { get; set; }

    /// <summary>Whether this build claims to be able to fetch at all.</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>What the title comes back as.</summary>
    public string Title { get; set; } = "A borrowed recording";

    /// <summary>The links it was asked for, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Set to block a fetch until a test lets it finish.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public string? DescribeUnavailable() =>
        IsAvailable
            ? null
            : "Uindosill cannot open links: this copy is missing the tools it downloads links with.";

    public async Task<FetchedMedia> FetchAudioAsync(
        string url,
        string root,
        IProgress<UrlFetchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(url);

        if (!IsAvailable)
        {
            throw new MediaFetchException(DescribeUnavailable()!);
        }

        if (RefuseWith is { Length: > 0 } reason)
        {
            throw new MediaFetchException(reason);
        }

        progress?.Report(new UrlFetchProgress(null, "Reading the link"));

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var into = Path.Combine(root, "url-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(into);

        // A real WAVE file, so everything downstream — the duration read, the queue, a transcription
        // run — behaves as it would on a real download rather than on an empty file.
        var path = Path.Combine(into, Title + ".wav");
        var samples = new float[16_000 * 2];

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 220 * i / 16_000.0));
        }

        Parakeet.Audio.WavWriter.WriteFile(path, samples, 16_000);

        progress?.Report(new UrlFetchProgress(1, "Downloaded"));
        return new FetchedMedia(path, Title, url);
    }
}
