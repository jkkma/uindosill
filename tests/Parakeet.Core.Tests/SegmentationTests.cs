using Parakeet.Core.Segmentation;

namespace Parakeet.Core.Tests;

public class StreamingSegmenterTests
{
    private static (List<AudioSegment> Segments, SegmentationReport Report) Run(
        float[] samples, VoiceActivityOptions? options = null, int blockSize = 4096)
    {
        var segmenter = new StreamingSegmenter(TestAudio.SampleRate, options);
        var segments = new List<AudioSegment>();

        for (var offset = 0; offset < samples.Length; offset += blockSize)
        {
            segmenter.Push(samples.AsSpan(offset, Math.Min(blockSize, samples.Length - offset)), segments);
        }

        segmenter.Flush(segments);
        return (segments, segmenter.CreateReport());
    }

    [Fact]
    public void DigitalSilenceProducesNoSegmentsAndIsReportedAsSuch()
    {
        var (segments, report) = Run(new float[TestAudio.SampleRate * 5]);

        Assert.Empty(segments);
        Assert.True(report.IsDigitalSilence);
        Assert.False(report.AnySpeechDetected);

        // The distinction matters: a digitally silent track is a recording problem to report,
        // not a transcription that happened to come back empty.
        Assert.False(report.LooksLikeMissedSpeech);
    }

    [Fact]
    public void QuietRoomToneProducesNoSegments()
    {
        var (segments, report) = Run(TestAudio.Build((4, false)));

        Assert.Empty(segments);
        Assert.False(report.IsDigitalSilence);
    }

    [Fact]
    public void SingleBurstBecomesOneSegmentWithPadding()
    {
        var samples = TestAudio.Build((1.0, false), (2.0, true), (1.5, false));
        var (segments, report) = Run(samples);

        var segment = Assert.Single(segments);
        Assert.True(report.AnySpeechDetected);

        // Starts before the onset (pre-roll) and ends after the offset (post-roll), so word
        // onsets and trailing consonants survive.
        Assert.InRange(segment.Start.TotalSeconds, 0.6, 1.0);
        Assert.InRange(segment.End.TotalSeconds, 3.0, 3.6);
    }

    [Fact]
    public void SeparateUtterancesBecomeSeparateSegments()
    {
        var samples = TestAudio.Build((0.5, false), (1.5, true), (1.5, false), (1.5, true), (0.5, false));
        var (segments, _) = Run(samples);

        Assert.Equal(2, segments.Count);
        Assert.True(segments[1].Start > segments[0].End);
        Assert.Equal(0, segments[0].Index);
        Assert.Equal(1, segments[1].Index);
    }

    [Fact]
    public void ContinuousSpeechIsCutAtTheCapAndNeverExceedsIt()
    {
        // 70 seconds with no gap: exactly the shape that makes an unsegmented product produce
        // quietly wrong text.
        var samples = TestAudio.Build((70, true));
        var options = VoiceActivityOptions.Default with { MaxSegmentLength = TimeSpan.FromSeconds(30) };
        var (segments, _) = Run(samples, options);

        Assert.True(segments.Count >= 3, $"expected at least three segments, got {segments.Count}");

        foreach (var segment in segments)
        {
            Assert.True(
                segment.Duration <= TimeSpan.FromSeconds(30.001),
                $"segment {segment.Index} is {segment.Duration.TotalSeconds:0.###}s, past the cap");
        }
    }

    [Fact]
    public void ForcedCutsAreContiguousSoNoAudioIsLost()
    {
        var samples = TestAudio.Build((70, true));
        var (segments, _) = Run(samples);

        for (var i = 1; i < segments.Count; i++)
        {
            var gap = (segments[i].Start - segments[i - 1].End).Duration();
            Assert.True(
                gap < TimeSpan.FromMilliseconds(1),
                $"forced cut between {i - 1} and {i} left a {gap.TotalMilliseconds:0.##} ms hole in continuous speech");
        }
    }

    [Fact]
    public void FixedWindowModeCoversTheWholeRecording()
    {
        // The escape hatch for material the energy gate mishandles. It must decode everything.
        var samples = TestAudio.Build((45, false));
        var (segments, report) = Run(samples, VoiceActivityOptions.Disabled);

        Assert.NotEmpty(segments);

        var covered = segments.Sum(s => s.Duration.TotalSeconds);
        Assert.InRange(covered, report.TotalAudio.TotalSeconds - 0.1, report.TotalAudio.TotalSeconds + 0.1);
        Assert.Equal(TimeSpan.Zero, segments[0].Start);
    }

    [Fact]
    public void EveryDetectedSpeechFrameEndsUpInsideSomeSegment()
    {
        var samples = TestAudio.Build((0.4, false), (2, true), (0.8, false), (3, true), (0.6, false), (1, true));
        var (segments, report) = Run(samples);

        var covered = TimeSpan.FromSeconds(segments.Sum(s => s.Duration.TotalSeconds));

        // Segmented audio includes padding, so it is always at least the detected speech. If it
        // is ever less, speech was dropped — the failure this whole class exists to prevent.
        Assert.True(
            covered >= report.SpeechAudio,
            $"segments cover {covered.TotalSeconds:0.###}s but {report.SpeechAudio.TotalSeconds:0.###}s was detected as speech");
    }

    [Fact]
    public void UtteranceAtEndOfFileIsStillEmitted()
    {
        // No trailing silence to close the segment: flush has to emit it or the last sentence
        // of every recording disappears.
        var samples = TestAudio.Build((0.5, false), (1.5, true));
        var (segments, _) = Run(samples);

        var segment = Assert.Single(segments);
        Assert.True(segment.End.TotalSeconds > 1.5);
    }

    [Fact]
    public void VeryShortBurstIsMergedIntoTheFollowingUtteranceRatherThanDropped()
    {
        var samples = TestAudio.Build((0.4, false), (0.2, true), (0.5, false), (1.5, true), (0.6, false));
        var (segments, _) = Run(samples);

        Assert.NotEmpty(segments);
        Assert.True(segments[0].Start.TotalSeconds < 0.6, "the short burst was dropped instead of merged");
    }

    [Theory]
    [InlineData(256)]
    [InlineData(1000)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void ResultIsIndependentOfTheBlockSizeItWasFedIn(int blockSize)
    {
        var samples = TestAudio.Build((0.5, false), (2, true), (1.2, false), (2, true), (0.5, false));
        var (reference, _) = Run(samples, blockSize: 4096);
        var (actual, _) = Run(samples, blockSize: blockSize);

        Assert.Equal(reference.Count, actual.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.Equal(reference[i].Start, actual[i].Start);
            Assert.Equal(reference[i].Samples.Length, actual[i].Samples.Length);
        }
    }

    [Fact]
    public void SegmentTimesAndSampleCountsAgree()
    {
        var samples = TestAudio.Build((0.5, false), (3, true), (1, false), (2, true), (0.5, false));
        var (segments, _) = Run(samples);

        foreach (var segment in segments)
        {
            var expected = TimeSpan.FromSeconds(segment.Samples.Length / (double)segment.SampleRate);
            Assert.Equal(expected, segment.Duration);
            Assert.Equal(segment.Start + expected, segment.End);
        }
    }

    [Fact]
    public void RecordingThatStartsOnSpeechIsStillDetected()
    {
        // A clip already trimmed to the speech has no leading silence to learn the noise floor
        // from. A detector that seeds itself on the first frame returns nothing for the whole
        // file — silently, which is how a transcription tool loses somebody's recording.
        var samples = TestAudio.Build((3, true), (1, false));
        var (segments, report) = Run(samples);

        Assert.NotEmpty(segments);
        Assert.True(report.AnySpeechDetected);
        Assert.Equal(TimeSpan.Zero, segments[0].Start);
    }

    [Fact]
    public void SustainedLoudPassageDoesNotHideTheSpeechAfterIt()
    {
        var samples = TestAudio.Build((40, true), (1.5, false), (2, true), (0.5, false));
        var (segments, _) = Run(samples);

        // The last utterance must survive the forty seconds of loud audio in front of it.
        Assert.True(
            segments[^1].End.TotalSeconds > 41,
            $"the trailing utterance was swallowed; last segment ends at {segments[^1].End.TotalSeconds:0.##}s");
    }

    [Fact]
    public void SegmentCapBeyondFiveMinutesIsRejected()
    {
        var options = VoiceActivityOptions.Default with { MaxSegmentLength = TimeSpan.FromMinutes(10) };
        var exception = Record.Exception(() => new StreamingSegmenter(TestAudio.SampleRate, options));

        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }
}
