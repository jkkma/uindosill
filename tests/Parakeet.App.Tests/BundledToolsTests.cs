using Parakeet.App.Services.Tools;

namespace Parakeet.App.Tests;

/// <summary>Where the vendored command-line tools are, and why one of them is kept apart.</summary>
public class BundledToolsTests
{
    [Fact]
    public void TheMuxerIsNotVendoredBesideYtDlp()
    {
        // Not tidiness. yt-dlp looks for ffmpeg beside its own executable before it looks at PATH:
        // measured 2026-08-23, the same binary reports "exe versions: none" alone in a directory and
        // "ffmpeg n9.0.1" with ffmpeg next to it, on an identical PATH. So a drop that put the two
        // together would silently change what a download produces — and would retire the check that
        // says both of this application's readers open what yt-dlp writes today.
        //
        // Nothing needs yt-dlp to have a muxer. The one thing that does is FfmpegSubtitleMuxer,
        // which runs it by absolute path. Giving yt-dlp one may well be an improvement; it is a
        // thing to decide and measure, not to inherit from where a file was put.
        if (BundledTools.FfmpegPath is not { } ffmpeg || BundledTools.YtDlpPath is not { } ytDlp)
        {
            // A build that vendored neither, or only one, has nothing to keep apart.
            return;
        }

        Assert.NotEqual(
            Path.GetDirectoryName(Path.GetFullPath(ytDlp)),
            Path.GetDirectoryName(Path.GetFullPath(ffmpeg)));

        // And said the other way round, because the assertion above passes if ffmpeg moves
        // somewhere unrelated while a second copy stays beside yt-dlp.
        Assert.False(
            File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ytDlp))!, "ffmpeg.exe")),
            "there is an ffmpeg.exe beside yt-dlp.exe, which changes what a download produces");
    }

    [Fact]
    public void EachToolIsAskedAboutSeparately()
    {
        // A build with ffmpeg and no yt-dlp adds transcripts to files and cannot open links; the
        // reverse does the opposite. Neither is a prerequisite for the other, so there is no single
        // "the tools are present" flag that would disable both over one missing file.
        Assert.NotEqual(
            BundledTools.DirectoryEnvironmentVariable,
            BundledTools.FfmpegDirectoryEnvironmentVariable);
    }
}
