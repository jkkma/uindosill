using Parakeet.Core.Muxing;

namespace Parakeet.Core.Tests;

/// <summary>
/// Which container a transcript goes into when it is put back inside its recording.
/// </summary>
/// <remarks>
/// Every rule these assert was measured against FFmpeg 9.0.1 on 2026-08-23 rather than read off a
/// specification — the two that matter most are the ones a specification would have got wrong. See
/// <see cref="SubtitleMux"/> for the measurements and <c>docs/UNPROVEN.md</c> for what running the
/// real muxer has and has not established.
/// </remarks>
public class SubtitleMuxTests
{
    [Theory]
    [InlineData("txt")]
    [InlineData("json")]
    [InlineData("md")]
    [InlineData("rttm")]
    public void ADocumentIsNotASubtitleTrackAndIsRefusedWithAReason(string format)
    {
        // The formats that have no subtitle codec anywhere. Refused with a sentence rather than
        // silently offered and then failing inside ffmpeg, which is where the reason would be lost.
        Assert.False(SubtitleMux.TryPlan("/tmp/talk.mp4", format, out var plan, out var refusal));
        Assert.Null(plan);
        Assert.Contains(format, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnSrtKeepsTheFileAnMp4()
    {
        // mov_text is exact for what this product writes — 19 lines in, the same 19 back — so there
        // is no reason to move a video out of the container that plays everywhere.
        Assert.True(SubtitleMux.TryPlan("/tmp/talk.mp4", "srt", out var plan, out _));

        Assert.Equal(MuxContainer.Mp4, plan.Container);
        Assert.Equal("mov_text", plan.SubtitleCodec);
        Assert.EndsWith("talk.subtitled.mp4", plan.OutputPath, StringComparison.Ordinal);
        Assert.Null(plan.Note);
    }

    [Theory]
    [InlineData("vtt")]
    [InlineData("vtt-words")]
    public void WebVttForcesMatroskaBecauseMp4HasNoWebVttAtAll(string format)
    {
        // Not a preference and not a quality judgement: the MP4 muxer refuses the stream outright.
        Assert.True(SubtitleMux.TryPlan("/tmp/talk.mp4", format, out var plan, out _));

        Assert.Equal(MuxContainer.Matroska, plan.Container);
        Assert.Equal("copy", plan.SubtitleCodec);
        Assert.EndsWith("talk.subtitled.mkv", plan.OutputPath, StringComparison.Ordinal);
        Assert.NotNull(plan.Note);
    }

    [Fact]
    public void OnlyTheMatroskaRouteKeepsTheTimesOfIndividualWords()
    {
        // The whole reason the container rule exists. Converting a word-timed cue to mov_text
        // strips every inline timestamp — measured 60 in, 0 out — and copying it into Matroska
        // keeps all 60.
        Assert.True(SubtitleMux.TryPlan("/tmp/talk.mp4", "vtt-words", out var matroska, out _));
        Assert.True(matroska.KeepsWordTiming);

        // And nothing else claims to. A plain WebVTT has no word times to keep in the first place.
        Assert.True(SubtitleMux.TryPlan("/tmp/talk.mp4", "vtt", out var plain, out _));
        Assert.False(plain.KeepsWordTiming);

        Assert.True(SubtitleMux.TryPlan("/tmp/talk.mp4", "srt", out var srt, out _));
        Assert.False(srt.KeepsWordTiming);
    }

    [Theory]
    [InlineData("/tmp/episode.mp3")]
    [InlineData("/tmp/episode.m4a")]
    [InlineData("/tmp/episode.wav")]
    public void ARecordingWithNoPictureBecomesAnMp4RatherThanBeingReEncoded(string path)
    {
        // An MP3 cannot hold subtitles — "Only audio streams and pictures are allowed in MP3" — but
        // its audio copies into an MP4 with the samples bit-identical. Converting it to AAC to get
        // an .m4a would cost a generation of quality for nothing.
        Assert.True(SubtitleMux.TryPlan(path, "srt", out var plan, out _));

        Assert.Equal(MuxContainer.Mp4, plan.Container);
        Assert.EndsWith(".subtitled.mp4", plan.OutputPath, StringComparison.Ordinal);
        Assert.Contains("not re-encoded", plan.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/tmp/talk.wma")]
    [InlineData("/tmp/talk.asf")]
    [InlineData("/tmp/talk.wmv")]
    public void WindowsMediaTakesTheMatroskaRouteWhateverWasAskedFor(string path)
    {
        // The exception that shapes the fallback: ASF audio refuses to copy into an MP4 and copies
        // into Matroska happily. So the container moves rather than the audio being re-encoded.
        Assert.True(SubtitleMux.TryPlan(path, "srt", out var plan, out _));

        Assert.Equal(MuxContainer.Matroska, plan.Container);
        Assert.EndsWith(".subtitled.mkv", plan.OutputPath, StringComparison.Ordinal);
        Assert.Contains("re-encoding", plan.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewFileSitsBesideTheOriginalAndCanNeverBeIt()
    {
        // Nothing here writes over a recording somebody gave us, and the marker is what guarantees
        // it: an MP4 in and an MP4 out would otherwise be the same path.
        Assert.True(SubtitleMux.TryPlan("/media/pod/talk.mp4", "srt", out var plan, out _));

        Assert.NotEqual(Path.GetFullPath("/media/pod/talk.mp4"), plan.OutputPath);
        Assert.Equal(
            Path.GetDirectoryName(Path.GetFullPath("/media/pod/talk.mp4")),
            Path.GetDirectoryName(plan.OutputPath));
    }

    [Fact]
    public void AFormatIsReadTheSameWayTheRestOfTheProductReadsOne()
    {
        // ".SRT" and "srt" are one format everywhere else in this product; they are here too.
        Assert.True(SubtitleMux.TryPlan("/tmp/talk.mp4", ".SRT", out var plan, out _));
        Assert.Equal("srt", plan.FormatId);
    }

    [Fact]
    public void TheCommandLineCopiesEveryStreamAndNamesItsOwnContainer()
    {
        Assert.True(SubtitleMux.TryPlan("/tmp/talk.mp4", "srt", out var plan, out _));

        var arguments = SubtitleMux.Arguments(plan, "/tmp/talk.srt", plan.OutputPath);

        // -map 0 rather than -map 0:a, so a podcast's cover art comes across with its sound.
        Assert.Equal(["-map", "0", "-map", "1:0"], Window(arguments, "-map", 4));

        // The point of the whole exercise: nothing is decoded and nothing is re-encoded.
        Assert.Equal(["-c", "copy"], Window(arguments, "-c", 2));
        Assert.Equal(["-c:s", "mov_text"], Window(arguments, "-c:s", 2));

        // The container is named rather than inferred from the extension — an inferred ".m4a" goes
        // through the iPod muxer, which refuses MP3 audio the general MP4 muxer takes.
        Assert.Equal(["-f", "mp4"], Window(arguments, "-f", 2));

        // Both inputs, in the order the maps refer to them, and the output last.
        Assert.Equal("/tmp/talk.mp4", arguments[arguments.ToList().IndexOf("-i") + 1]);
        Assert.Equal(plan.OutputPath, arguments[^1]);

        // Never through a shell, so nothing here is quoted or escaped by us.
        Assert.DoesNotContain(arguments, a => a.Contains('"', StringComparison.Ordinal));
    }

    [Fact]
    public void TheMatroskaRouteCopiesTheSubtitlesRatherThanConvertingThem()
    {
        // The one flag the word-level timing depends on. Anything but "copy" here sends the cue
        // through FFmpeg's WebVTT decoder, which drops every inline timestamp in it.
        Assert.True(SubtitleMux.TryPlan("/tmp/talk.mp4", "vtt-words", out var plan, out _));

        var arguments = SubtitleMux.Arguments(plan, "/tmp/talk.words.vtt", plan.OutputPath);

        Assert.Equal(["-c:s", "copy"], Window(arguments, "-c:s", 2));
        Assert.Equal(["-f", "matroska"], Window(arguments, "-f", 2));
    }

    /// <summary>The <paramref name="length"/> arguments starting at <paramref name="flag"/>.</summary>
    private static string[] Window(IReadOnlyList<string> arguments, string flag, int length)
    {
        var at = arguments.ToList().IndexOf(flag);
        Assert.True(at >= 0, $"{flag} is not on the command line");

        return [.. arguments.Skip(at).Take(length)];
    }

    [Fact]
    public void NoRecordingIsRefusedRatherThanPlannedFor()
    {
        Assert.False(SubtitleMux.TryPlan(string.Empty, "srt", out _, out var refusal));
        Assert.NotNull(refusal);
    }
}
