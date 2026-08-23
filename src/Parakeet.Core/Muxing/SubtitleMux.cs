using System.Diagnostics.CodeAnalysis;

namespace Parakeet.Core.Muxing;

/// <summary>The two containers this product will write a subtitle track into.</summary>
public enum MuxContainer
{
    /// <summary>ISO base media — <c>.mp4</c>. Carries 3GPP timed text and nothing else.</summary>
    Mp4,

    /// <summary>Matroska — <c>.mkv</c>. Carries SubRip and WebVTT as they are.</summary>
    Matroska,
}

/// <summary>
/// What it would take to put one transcript format inside one media file: which container, which
/// subtitle codec, what comes out, and what — if anything — is lost on the way.
/// </summary>
public sealed record SubtitleMuxPlan
{
    public required string InputPath { get; init; }

    /// <summary>The new file. Never the input: nothing here writes over what the user gave us.</summary>
    public required string OutputPath { get; init; }

    public required string FormatId { get; init; }

    public required MuxContainer Container { get; init; }

    /// <summary>
    /// What to encode the subtitles as. <c>copy</c> where the container takes the format unchanged,
    /// <c>mov_text</c> where MP4 has to be given 3GPP timed text instead.
    /// </summary>
    public required string SubtitleCodec { get; init; }

    /// <summary>
    /// Whether the times of individual words survive. False for every MP4 route, and that is a
    /// property of the container rather than a setting — see <see cref="SubtitleMux"/>.
    /// </summary>
    public required bool KeepsWordTiming { get; init; }

    /// <summary>
    /// What this costs, in a sentence the window can show, or null when it costs nothing. A plan
    /// that quietly downgrades what somebody asked for is the failure this whole type exists to
    /// prevent.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>
/// Chooses the container for putting a transcript inside the recording it came from.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule is: the container follows the format, and nothing is ever re-encoded.</b> A subtitle
/// track is added by rewriting the file around the streams it already has, so the audio and the
/// picture are copied through untouched and the only new bytes are the words. Where a container
/// cannot hold what was asked for, this says so rather than converting something.
/// </para>
/// <para>
/// <b>Every rule below was measured with FFmpeg 9.0.1 on 2026-08-23</b>, because none of it is
/// guessable from the specifications and two of the answers are the opposite of what they look:
/// </para>
/// <list type="bullet">
/// <item><b>MP4 cannot hold WebVTT at all.</b> Not "loses the styling" — the muxer refuses the
/// stream: <c>Could not find tag for codec webvtt in stream, codec not currently supported in
/// container</c>. Its only subtitle codec is <c>mov_text</c>, 3GPP timed text, which is plain
/// text.</item>
/// <item><b>So word-level timing always forces Matroska.</b> A word-timed WebVTT cue carries inline
/// timestamps — <c>&lt;c&gt;All&lt;/c&gt;&lt;00:00:37.570&gt;</c> — and converting it to
/// <c>mov_text</c> strips every one of them: 60 timestamps in, 0 out. Copied into Matroska instead,
/// all 60 survive and come back byte for byte.</item>
/// <item><b>SubRip through <c>mov_text</c> is exact</b> for what this product writes — 19 lines in,
/// the same 19 back — so an SRT keeps the file an MP4, which is the container that plays
/// everywhere.</item>
/// <item><b>An MP3 cannot hold subtitles</b> — <c>Only audio streams and pictures are allowed in
/// MP3</c> — but its audio copies into an MP4 unchanged, samples bit-identical, at the cost of a
/// couple of kilobytes of container. So a podcast becomes an MP4 rather than being re-encoded to
/// AAC, which is what "convert it to something that can hold a track" would otherwise mean.</item>
/// <item><b>ASF is the exception that shapes the fallback.</b> A <c>.wma</c> refuses to copy into
/// MP4 and copies into Matroska happily, so a Windows Media file takes the Matroska route whatever
/// format was asked for. Re-encoding it to fit the other container would be the one thing this
/// class will not do.</item>
/// </list>
/// <para>
/// <b>mkvmerge was measured and rejected on 2026-08-23</b>, and the reason is worth keeping because
/// it is backwards. MKVToolNix writes WebVTT under <c>S_TEXT/WEBVTT</c>, which is the identifier
/// Matroska actually specifies; FFmpeg writes <c>D_WEBVTT/SUBTITLES</c>, which is the older WebM
/// one. FFmpeg's demuxer reads its own and not the specified one — it reports the track's codec as
/// <c>none</c> and refuses to decode it, while carrying a perfectly good WebVTT decoder. This
/// application plays through libmpv, which is FFmpeg, so a file muxed by the more correct tool is a
/// file whose subtitles our own Ask tab cannot show. SubRip is unaffected: both write
/// <c>S_TEXT/UTF8</c>.
/// </para>
/// </remarks>
public static class SubtitleMux
{
    /// <summary>
    /// What the new file's name says about itself. An infix rather than a different directory, so
    /// the two files sort together, and never the input's own name, so nothing can overwrite a
    /// recording somebody gave us.
    /// </summary>
    public const string OutputMarker = ".subtitled";

    /// <summary>The formats a media container can carry. The rest are documents, not tracks.</summary>
    public static IReadOnlyList<string> MuxableFormats { get; } = ["srt", "vtt", "vtt-words"];

    /// <summary>
    /// Extensions whose audio MP4 will not take, measured rather than assumed. Everything else this
    /// product accepts — MPEG, ISO base media and RIFF/WAVE — copies into an MP4 unchanged.
    /// </summary>
    private static readonly string[] MatroskaOnlySources = [".wma", ".asf", ".wmv"];

    /// <summary>
    /// Works out how to put <paramref name="formatId"/> inside <paramref name="inputPath"/>, or
    /// says why it cannot be done.
    /// </summary>
    /// <returns>True with a plan; false with a <paramref name="refusal"/> fit to show a reader.</returns>
    public static bool TryPlan(
        string inputPath,
        string formatId,
        [NotNullWhen(true)] out SubtitleMuxPlan? plan,
        [NotNullWhen(false)] out string? refusal)
    {
        plan = null;
        refusal = null;

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            refusal = "There is no recording to add a transcript to.";
            return false;
        }

        var format = (formatId ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();

        if (!MuxableFormats.Contains(format))
        {
            refusal = $"A media file can carry subtitles, and '{format}' is not subtitles — "
                + "only SRT and WebVTT go inside a recording. The rest stay as files beside it.";
            return false;
        }

        var source = Path.GetExtension(inputPath).ToLowerInvariant();

        // WebVTT of either kind is Matroska's, because MP4 has no WebVTT at all. So is anything
        // whose audio MP4 will not take.
        var webVtt = format is "vtt" or "vtt-words";
        var asf = MatroskaOnlySources.Contains(source);
        var container = webVtt || asf ? MuxContainer.Matroska : MuxContainer.Mp4;

        var directory = Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var extension = container == MuxContainer.Matroska ? ".mkv" : ".mp4";

        plan = new SubtitleMuxPlan
        {
            InputPath = inputPath,
            OutputPath = Path.Combine(directory, stem + OutputMarker + extension),
            FormatId = format,
            Container = container,
            SubtitleCodec = container == MuxContainer.Matroska ? "copy" : "mov_text",
            KeepsWordTiming = format == "vtt-words" && container == MuxContainer.Matroska,
            Note = Explain(format, container, source, asf),
        };

        return true;
    }

    /// <summary>
    /// The command line that carries out <paramref name="plan"/>, as an argument list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A list and never a joined string, for the same reason
    /// <c>YtDlpMediaUrlFetcher</c> gives: these are paths a user chose, and the list form hands each
    /// one to the child without a shell and without quoting rules to get wrong.
    /// </para>
    /// <para>
    /// <b><c>-map 0</c> takes everything the recording already has</b>, not just its sound. A
    /// podcast's cover art is a video stream, and mapping only the audio would quietly throw it
    /// away — measured on a cover-art MP3 on 2026-08-23, where it survives into both containers.
    /// <c>-c copy</c> then says the point of the whole exercise: every existing stream is copied
    /// through, nothing is decoded, and the only new bytes are words.
    /// </para>
    /// <para>
    /// The container is named with <c>-f</c> rather than inferred from the output's extension.
    /// FFmpeg guesses well, but a guess is what put a <c>.m4a</c> through the iPod muxer, which
    /// refuses MP3 audio the general MP4 muxer accepts — so the container this class decided on is
    /// the container that runs.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Arguments(SubtitleMuxPlan plan, string subtitlePath, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return
        [
            "-nostdin",
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-i", plan.InputPath,
            "-i", subtitlePath,
            "-map", "0",
            "-map", "1:0",
            "-c", "copy",
            "-c:s", plan.SubtitleCodec,
            "-f", plan.Container == MuxContainer.Matroska ? "matroska" : "mp4",
            outputPath,
        ];
    }

    /// <summary>
    /// What the reader is about to get that they did not ask for, or null when the answer is simply
    /// what they asked for.
    /// </summary>
    private static string? Explain(string format, MuxContainer container, string source, bool asf)
    {
        if (container == MuxContainer.Matroska)
        {
            if (asf)
            {
                return "Windows Media audio cannot go inside an MP4, so this makes an MKV rather "
                    + "than re-encoding the sound. The audio is copied across untouched.";
            }

            return format == "vtt-words"
                ? "Word-by-word timing only survives in an MKV — an MP4 has no WebVTT track to put "
                  + "it in — so this makes one. The audio and picture are copied across untouched."
                : "WebVTT only goes inside an MKV, so this makes one rather than an MP4. The audio "
                  + "and picture are copied across untouched.";
        }

        return IsAudioOnlySource(source)
            ? "A recording with no picture becomes an MP4, because that is the container that can "
              + "carry a subtitle track. The sound is copied across unchanged, not re-encoded."
            : null;
    }

    private static bool IsAudioOnlySource(string source) =>
        source is ".mp3" or ".m4a" or ".m4b" or ".aac" or ".wav" or ".wave" or ".rf64" or ".bwf";
}
