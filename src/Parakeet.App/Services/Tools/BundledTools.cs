namespace Parakeet.App.Services.Tools;

/// <summary>
/// Finds the vendored command-line tools — yt-dlp, and the JavaScript runtime it needs — and says
/// whether this build has them.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <c>MpvNativeLibrary</c>: pinned binaries under <c>native/win-x64/tools/</c>,
/// located rather than assumed, and a build without them says so instead of failing at the moment
/// somebody pastes a link. See <c>docs/NATIVE-BINARIES.md</c>.
/// </para>
/// <para>
/// <b>Deno is not an optional extra.</b> yt-dlp needs a JavaScript runtime to answer YouTube's
/// signature challenge, and its own documentation enables exactly one by default: "Supported
/// runtimes are (in order of priority, from highest to lowest): deno, node, quickjs, bun. Only
/// 'deno' is enabled by default." Without one, YouTube extraction degrades or fails. It is found on
/// <c>PATH</c>, which is why <see cref="PrependToPath"/> exists rather than a flag: mpv spawns
/// yt-dlp itself and cannot be told about our directory layout, so the child inherits a
/// <c>PATH</c> that has the tools on it.
/// </para>
/// </remarks>
public static class BundledTools
{
    /// <summary>Overrides the search, for a developer with the tools somewhere else.</summary>
    public const string DirectoryEnvironmentVariable = "UINDOSILL_TOOLS_DIR";

    /// <summary>The same, for the muxer, which lives apart from the two above on purpose.</summary>
    public const string FfmpegDirectoryEnvironmentVariable = "UINDOSILL_FFMPEG_DIR";

    private static readonly object Gate = new();
    private static bool _pathPrepared;

    /// <summary>The downloader, or null when this build did not vendor it.</summary>
    public static string? YtDlpPath => Find("yt-dlp.exe");

    /// <summary>The JavaScript runtime yt-dlp needs for YouTube, or null.</summary>
    public static string? DenoPath => Find("deno.exe");

    /// <summary>
    /// The muxer that puts a transcript inside a recording, or null when this build did not vendor
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Independent of the two above. A build with ffmpeg and no yt-dlp adds transcripts to files
    /// and cannot open links; a build with the reverse does the opposite. Neither half is a
    /// prerequisite for the other, so they are asked about separately rather than through one
    /// "tools are present" flag.
    /// </para>
    /// <para>
    /// <b>It lives in a directory of its own, and that is the whole point of this method existing
    /// separately.</b> yt-dlp looks for ffmpeg beside its own executable before it looks anywhere
    /// else — measured on 2026-08-23: the same yt-dlp reports <c>exe versions: none</c> alone in a
    /// directory and <c>ffmpeg n9.0.1</c> with ffmpeg next to it, on an identical PATH. So dropping
    /// the muxer into <c>tools/</c> would silently change what a download produces, and would
    /// retire the check that says both of this application's readers open what yt-dlp writes today.
    /// Nothing here needs yt-dlp to have a muxer; the one thing that does is
    /// <c>FfmpegSubtitleMuxer</c>, which runs it by absolute path.
    /// </para>
    /// <para>
    /// Giving yt-dlp ffmpeg may well be an improvement — it is what would let it repair the DASH
    /// m4a its own warning is about — but it is a change to what a download produces, and that is a
    /// thing to decide and measure rather than to inherit from where a file was put.
    /// </para>
    /// </remarks>
    public static string? FfmpegPath => Find("ffmpeg.exe", FfmpegDirectories());

    /// <summary>Whether a link can be fetched at all: both halves have to be there.</summary>
    public static bool CanFetchUrls => YtDlpPath is not null && DenoPath is not null;

    /// <summary>
    /// Why a link cannot be fetched, or null when it can. Names which half is missing rather than
    /// saying "unavailable", because the two are vendored by the same script and a half-drop is
    /// the likely way this goes wrong.
    /// </summary>
    /// <remarks>
    /// Written for whoever is looking at the window, not for whoever builds it. It used to name
    /// the vendoring script and a repository document, on the assumption that only a developer
    /// would ever see a build without the tools — and then <c>v1.0.0-rc.3</c> shipped without them
    /// (the channel prune in <c>scripts/package-windows.ps1</c> dropped every directory that was
    /// not a backend) and every user met a sentence telling them to run a PowerShell script from a
    /// clone they do not have. Reinstalling is the action a user can actually take; the developer's
    /// route is in <c>docs/NATIVE-BINARIES.md</c>, where a developer will look anyway.
    /// </remarks>
    public static string? DescribeUnavailable()
    {
        if (CanFetchUrls)
        {
            return null;
        }

        var missing = YtDlpPath is null && DenoPath is null ? "the tools it downloads links with"
            : YtDlpPath is null ? "the downloader it opens links with"
            : "the JavaScript runtime its downloader needs for YouTube";

        return $"Uindosill cannot open links: this copy is missing {missing}. "
            + "Reinstalling should restore it. Opening files still works.";
    }

    /// <summary>
    /// Puts the tools directory at the front of this process's <c>PATH</c>, once.
    /// </summary>
    /// <remarks>
    /// Process-local — nothing is written to the machine or the user environment — and it is what
    /// makes two separate things work without either being told where anything is: yt-dlp finds
    /// Deno by looking on <c>PATH</c>, and mpv spawns yt-dlp as a child that inherits this one.
    /// Prepended rather than appended so a different yt-dlp already installed on the machine cannot
    /// silently take over from the pinned one.
    /// </remarks>
    public static void PrependToPath()
    {
        lock (Gate)
        {
            if (_pathPrepared)
            {
                return;
            }

            _pathPrepared = true;

            foreach (var directory in CandidateDirectories())
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

                if (!current.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + current);
                }

                return;
            }
        }
    }

    private static string? Find(string fileName) => Find(fileName, CandidateDirectories());

    private static string? Find(string fileName, IEnumerable<string> directories)
    {
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        if (Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            yield return Path.GetFullPath(fromEnvironment);
        }

        var baseDirectory = AppContext.BaseDirectory;

        yield return Path.Combine(baseDirectory, "native", "win-x64", "tools");
        yield return Path.Combine(baseDirectory, "native", "tools");
    }

    /// <summary>
    /// Where the muxer is looked for — deliberately not beside yt-dlp. See
    /// <see cref="FfmpegPath"/>.
    /// </summary>
    private static IEnumerable<string> FfmpegDirectories()
    {
        if (Environment.GetEnvironmentVariable(FfmpegDirectoryEnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            yield return Path.GetFullPath(fromEnvironment);
        }

        var baseDirectory = AppContext.BaseDirectory;

        yield return Path.Combine(baseDirectory, "native", "win-x64", "ffmpeg");
        yield return Path.Combine(baseDirectory, "native", "ffmpeg");
    }
}
