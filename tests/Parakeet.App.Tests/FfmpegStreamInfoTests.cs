using Parakeet.App.Services.Tools;

namespace Parakeet.App.Tests;

public sealed class FfmpegStreamInfoTests
{
    [Fact]
    public void InputSubtitleOrderIgnoresMetadataAndOutputStreamReports()
    {
        var codecs = FfmpegStreamInfo.SubtitleCodecs([
            "warning before input",
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'recording.mp4':",
            "  Stream #0:0[0x1](und): Audio: aac (LC), 44100 Hz",
            "  Stream #0:1[0x2](eng): Subtitle: mov_text (tx3g / 0x67337874), 0 kb/s (default)",
            "    Metadata:",
            "      title:  Stream #0:9: Subtitle: mov_text",
            "      Stream #0:8: Subtitle: mov_text",
            "  Stream #0:3: Subtitle: ass",
            "  Stream #0:2(eng): Subtitle: webvtt",
            "Stream mapping:",
            "  Stream #0:1 -> #0:1 (copy)",
            "Output #0, null, to 'pipe:':",
            "  Stream #0:1(eng): Subtitle: subrip",
        ]);

        Assert.Equal(["mov_text", "webvtt", "ass"], codecs);
    }

    [Fact]
    public void ARecordingWithoutSubtitlesNeedsNoOverrides()
    {
        Assert.Empty(FfmpegStreamInfo.SubtitleCodecs([
            "Input #0, wav, from 'recording.wav':",
            "  Stream #0:0: Audio: pcm_u8, 44100 Hz, mono, u8, 352 kb/s",
            "Output #0, null, to 'pipe:':",
        ]));
    }

    [Fact]
    public void AFailedInspectionIsNotAssumedToHaveNoSubtitles()
    {
        Assert.Throws<SubtitleMuxException>(() => FfmpegStreamInfo.SubtitleCodecs([
            "recording.mp4: Invalid data found when processing input",
        ]));
    }

    [Fact]
    public void AmbiguousStreamNumbersAreRefusedBeforeBuildingOverrides()
    {
        Assert.Throws<SubtitleMuxException>(() => FfmpegStreamInfo.SubtitleCodecs([
            "Input #0, mov,mp4, from 'recording.mp4':",
            "  Stream #0:1: Subtitle: mov_text",
            "  Stream #0:1: Subtitle: webvtt",
        ]));
    }
}
