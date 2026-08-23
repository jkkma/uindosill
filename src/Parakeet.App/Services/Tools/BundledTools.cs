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

    private static readonly object Gate = new();
    private static bool _pathPrepared;

    /// <summary>The downloader, or null when this build did not vendor it.</summary>
    public static string? YtDlpPath => Find("yt-dlp.exe");

    /// <summary>The JavaScript runtime yt-dlp needs for YouTube, or null.</summary>
    public static string? DenoPath => Find("deno.exe");

    /// <summary>Whether a link can be fetched at all: both halves have to be there.</summary>
    public static bool CanFetchUrls => YtDlpPath is not null && DenoPath is not null;

    /// <summary>
    /// Why a link cannot be fetched, or null when it can. Names the missing half rather than
    /// saying "unavailable", because the two are vendored by the same script and a half-drop is
    /// the likely way this goes wrong.
    /// </summary>
    public static string? DescribeUnavailable()
    {
        if (CanFetchUrls)
        {
            return null;
        }

        var missing = YtDlpPath is null && DenoPath is null ? "yt-dlp and Deno"
            : YtDlpPath is null ? "yt-dlp"
            : "Deno, which yt-dlp needs for YouTube";

        return $"This build cannot open links: {missing} was not vendored. "
            + "Run scripts/vendor-tools.ps1 — see docs/NATIVE-BINARIES.md. Files still work.";
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

    private static string? Find(string fileName)
    {
        foreach (var directory in CandidateDirectories())
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
}
