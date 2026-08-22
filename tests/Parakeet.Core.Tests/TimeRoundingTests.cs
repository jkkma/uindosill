using Parakeet.Core.Audio;
using Parakeet.Core.Formatting;
using Parakeet.Core.Segmentation;

namespace Parakeet.Core.Tests;

/// <summary>
/// Times that land on the tick they mean. <c>TimeSpan.FromSeconds(double)</c> truncates, so a value
/// a hair under a millisecond boundary prints as the millisecond before; the RTTM path had the fix
/// (GOTCHAS §25) and until 2026-08-22 the ASR path — segment starts and durations, the segmenter's
/// report, the WAV duration, the native decoder's word times — did not.
/// </summary>
public class TimeRoundingTests
{
    [Fact]
    public void ASampleCountLandsOnTheTickItMeans()
    {
        // 9,120 samples at 16 kHz is 0.57 s. Through FromSeconds that is 5,699,999 ticks, and a
        // subtitle reads 00:00:00,569 while the JSON beside it says 0.57.
        Assert.Equal(5_700_000, AudioMath.SamplesToTime(9_120, 16_000).Ticks);
        Assert.Equal("00:00:00,570", Timecode.ToSrt(AudioMath.SamplesToTime(9_120, 16_000)));
        Assert.Equal("00:00:00.570", Timecode.ToVtt(AudioMath.SamplesToTime(9_120, 16_000)));
    }

    [Fact]
    public void ASampleCountAtAnyRateIsExactAndNeverNegative()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), AudioMath.SamplesToTime(48_000, 48_000));
        Assert.Equal(TimeSpan.FromSeconds(1), AudioMath.SamplesToTime(44_100, 44_100));
        Assert.Equal(TimeSpan.Zero, AudioMath.SamplesToTime(0, 16_000));

        // Three hours at 16 kHz, and a day at 192 kHz, without overflowing the tick arithmetic.
        Assert.Equal(TimeSpan.FromHours(3), AudioMath.SamplesToTime(3L * 3_600 * 16_000, 16_000));
        Assert.Equal(TimeSpan.FromHours(24), AudioMath.SamplesToTime(24L * 3_600 * 192_000, 192_000));
    }

    [Fact]
    public void AParsedDecimalIsRoundedRatherThanTruncated()
    {
        Assert.Equal(5_700_000, AudioMath.SecondsToTime(0.57).Ticks);

        // GOTCHAS §25's own example: 10.200 + 8.100 is 18.299999999999997 in binary64.
        Assert.Equal(183_000_000, AudioMath.SecondsToTime(10.2 + 8.1).Ticks);
    }

    [Fact]
    public void ASegmentsStartDurationAndEndAreExact()
    {
        // A segment that starts at sample 5,280 (0.33 s) and holds 9,120 samples (0.57 s) ends at
        // 0.9 s exactly — all three of which FromSeconds leaves a tick short.
        var segment = new AudioSegment
        {
            Index = 0,
            SampleRate = 16_000,
            Start = AudioMath.SamplesToTime(5_280, 16_000),
            Samples = new float[9_120],
        };

        Assert.Equal(3_300_000, segment.Start.Ticks);
        Assert.Equal(5_700_000, segment.Duration.Ticks);
        Assert.Equal(9_000_000, segment.End.Ticks);
        Assert.Equal("00:00:00,330", Timecode.ToSrt(segment.Start));
    }

    [Fact]
    public void TheSegmenterPutsASegmentsStartOnTheTickItMeans()
    {
        // 0.57 s of quiet, then tone: the onset is at sample 9,120 and the pre-roll of 240 ms puts
        // the segment's start at sample 5,280, which is 0.33 s and was 0.329 s in a subtitle.
        var samples = TestAudio.Build((0.57, false), (1, true), (0.5, false));
        var segmenter = new StreamingSegmenter(TestAudio.SampleRate);
        var segments = new List<AudioSegment>();
        segmenter.Push(samples, segments);
        segmenter.Flush(segments);

        Assert.NotEmpty(segments);
        Assert.Equal(3_300_000, segments[0].Start.Ticks);
        Assert.Equal("00:00:00,330", Timecode.ToSrt(segments[0].Start));
    }
}
