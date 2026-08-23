using Parakeet.Core.Segmentation;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

/// <summary>
/// The segmenter with a speech detector in place of its energy gate. The detector here is scripted,
/// so what is under test is the segmenter's half of the arrangement — where it cuts when told,
/// the hysteresis it applies, what it reports — and never a model.
/// </summary>
public class NeuralSegmentationTests
{
    private static (List<AudioSegment> Segments, SegmentationReport Report) Run(
        float[] samples,
        ISpeechDetector? detector,
        VoiceActivityOptions? options = null,
        int blockSize = 4096)
    {
        using var stream = detector?.Open(TestAudio.SampleRate);
        var segmenter = new StreamingSegmenter(TestAudio.SampleRate, options, stream);
        var segments = new List<AudioSegment>();

        for (var offset = 0; offset < samples.Length; offset += blockSize)
        {
            segmenter.Push(samples.AsSpan(offset, Math.Min(blockSize, samples.Length - offset)), segments);
        }

        segmenter.Flush(segments);
        return (segments, segmenter.CreateReport());
    }

    /// <summary>A bed loud enough that the energy gate hears speech throughout.</summary>
    private static float[] Bed(double seconds, double dbfs = -26)
    {
        var amplitude = (float)(Math.Pow(10, dbfs / 20) * Math.Sqrt(2));
        var samples = new float[(int)(seconds * TestAudio.SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / TestAudio.SampleRate));
        }

        return samples;
    }

    [Fact]
    public void ADetectorCutsWhereTheEnergyGateCannot()
    {
        // The measured failure: a bed at −26 dBFS sits above the line the gate's threshold can never
        // rise past, so the gate hears twenty seconds of speech and cuts nothing. A detector that
        // hears the pause at 8–10 s cuts there.
        var bed = Bed(20);

        var (energyOnly, _) = Run(bed, detector: null);
        Assert.Single(energyOnly);

        using var detector = new FakeSpeechDetector(t => t < 8 || t >= 10 ? 1f : 0f);
        var (neural, report) = Run(bed, detector);

        Assert.Equal(2, neural.Count);

        // The first segment ends after the pause opens plus the post-roll; the second starts
        // before the speech resumes by the pre-roll. Neither is a guess at the instant.
        Assert.InRange(neural[0].End.TotalSeconds, 8.0, 9.0);
        Assert.InRange(neural[1].Start.TotalSeconds, 9.5, 10.2);
        Assert.Equal(detector.Name, report.SpeechDetector);
    }

    [Fact]
    public void TheDecisionHasHysteresisSoAWaveringProbabilityDoesNotChatter()
    {
        // Speech opens at 0.5 and closes below 0.35; in between, whatever it was stays. So a run
        // of 0.42 after speech is still speech and cuts nothing; the same 0.42 from a standing start
        // never opens a segment at all.
        var bed = Bed(6);

        using var wavering = new FakeSpeechDetector(t => t < 1 ? 0.6f : t < 4 ? 0.42f : 0.2f);
        var (segments, _) = Run(bed, wavering);
        var segment = Assert.Single(segments);
        Assert.InRange(segment.End.TotalSeconds, 4.0, 5.0);

        using var neverOpens = new FakeSpeechDetector(_ => 0.42f);
        var (none, report) = Run(bed, neverOpens);
        Assert.Empty(none);
        Assert.Equal(TimeSpan.Zero, report.SpeechAudio);
    }

    [Fact]
    public void TheThresholdsAreTheOptionsAndAreValidated()
    {
        var bed = Bed(4);
        using var detector = new FakeSpeechDetector(_ => 0.7f);

        // Raise the opening threshold past what the detector says and nothing opens.
        var strict = VoiceActivityOptions.Default with { SpeechProbability = 0.8f, SilenceProbability = 0.5f };
        var (none, _) = Run(bed, detector, strict);
        Assert.Empty(none);

        // Silence above speech is a rule that can never close or never open; refused.
        var inverted = VoiceActivityOptions.Default with { SpeechProbability = 0.3f, SilenceProbability = 0.6f };
        Assert.Throws<ArgumentOutOfRangeException>(() => inverted.Validate());
    }

    [Fact]
    public void FixedWindowsIgnoreTheDetector()
    {
        // Fixed windows are the escape hatch for material no detector handles, so a detector that
        // says "never speech" must not turn them into "never decode".
        var bed = Bed(45);
        using var detector = new FakeSpeechDetector(_ => 0f);
        var (segments, report) = Run(bed, detector, VoiceActivityOptions.Disabled);

        Assert.NotEmpty(segments);
        var covered = segments.Sum(s => s.Duration.TotalSeconds);
        Assert.InRange(covered, report.TotalAudio.TotalSeconds - 0.1, report.TotalAudio.TotalSeconds + 0.1);
    }

    [Fact]
    public void TheReportNamesWhatCutTheAudio()
    {
        var bed = Bed(3);

        var (_, energy) = Run(bed, detector: null);
        Assert.Equal(StreamingSegmenter.EnergyGateName, energy.SpeechDetector);

        using var detector = new FakeSpeechDetector(_ => 1f);
        var (_, neural) = Run(bed, detector);
        Assert.Equal("fake speech detector", neural.SpeechDetector);
    }

    [Fact]
    public async Task TheEngineOpensOneStreamPerRecordingAtItsOwnRateAndClosesItWithTheFile()
    {
        // The contract the detector is owed: a stream per recording, opened at the recording's rate
        // rather than the model's, closed when the recording is. Two files through one engine with
        // one detector must not share a window of context.
        using var detector = new FakeSpeechDetector();
        var options = TranscriptionOptions.Default with { SpeechDetector = detector };
        var engine = new FakeTranscriptionEngine();

        var first = new ArrayAudioSource(TestAudio.Build((0.5, false), (2, true), (0.5, false)), sampleRate: 44_100);
        await foreach (var _ in engine.TranscribeAsync(first, options))
        {
        }

        Assert.Equal(1, detector.Opened);
        Assert.Equal([44_100], detector.OpenedRates);
        Assert.Equal(1, detector.Closed);

        var second = new ArrayAudioSource(TestAudio.Build((0.5, false), (2, true), (0.5, false)), sampleRate: 16_000);
        await foreach (var _ in engine.TranscribeAsync(second, options))
        {
        }

        Assert.Equal(2, detector.Opened);
        Assert.Equal([44_100, 16_000], detector.OpenedRates);
        Assert.Equal(2, detector.Closed);
        Assert.False(detector.Disposed);   // the detector outlives the files; the caller owns it

        Assert.Equal("fake speech detector", engine.LastSegmentationReport?.SpeechDetector);
    }

    [Fact]
    public void TheLoudnessFakeBehavesLikeTheGateOnOrdinaryMaterial()
    {
        // The default fake — the one the canned pipeline runs — says speech for loud blocks and
        // nothing for quiet ones, so the app's fake provider keeps producing what it always did.
        var samples = TestAudio.Build((0.5, false), (1.5, true), (1.5, false), (1.5, true), (0.5, false));
        using var detector = new FakeSpeechDetector();

        var (segments, _) = Run(samples, detector);

        Assert.Equal(2, segments.Count);
    }
}
