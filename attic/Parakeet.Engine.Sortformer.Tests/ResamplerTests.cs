namespace Parakeet.Engine.Sortformer.Tests;

/// <summary>
/// The resampler that puts arbitrary audio on the 16 kHz grid the model was trained at.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing measured has been through this code</b>, and these tests do not change that. AMI is
/// 16 kHz, so every DER this project reports was produced with the resampler bypassed entirely.
/// What is asserted here is that it does the arithmetic it claims to — the right number of samples,
/// a tone that survives the trip, and a band above the output's Nyquist that is removed rather than
/// folded down into speech — not that a transcript of a 44.1 kHz podcast is as good as one of a
/// 16 kHz meeting. That is recorded as unproven in <c>docs/UNPROVEN.md</c>.
/// </para>
/// <para>
/// Aliasing is the failure worth testing for, because it is the one that is silent: dropping every
/// third sample of a 48 kHz file produces audio that sounds roughly right and puts everything above
/// 8 kHz back down on top of the speech band, where it degrades the features without breaking
/// anything.
/// </para>
/// </remarks>
public class ResamplerTests
{
    private static float[] Run(int sourceRate, IEnumerable<float> samples, int blockSize = 1024)
    {
        var resampler = new Resampler(sourceRate);
        var output = new List<float>();
        var block = new List<float>(blockSize);

        foreach (var sample in samples)
        {
            block.Add(sample);
            if (block.Count == blockSize)
            {
                resampler.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(block), output);
                block.Clear();
            }
        }

        resampler.Process(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(block), output);
        resampler.Complete(output);
        return [.. output];
    }

    private static float[] Tone(int rate, double hz, double seconds, double amplitude = 0.5)
    {
        var samples = new float[(int)(rate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        }

        return samples;
    }

    /// <summary>Root mean square over the steady middle, away from the filter's edges.</summary>
    private static double Level(float[] samples)
    {
        var from = samples.Length / 4;
        var to = samples.Length - samples.Length / 4;
        var sum = 0.0;
        for (var i = from; i < to; i++)
        {
            sum += samples[i] * (double)samples[i];
        }

        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    [Fact]
    public void AtSixteenKilohertzTheSamplesPassThroughUntouched()
    {
        // The measured path. Not "close enough": a file already at the model's rate must reach the
        // featurizer as the bytes that were decoded, or the DER this project reports was produced
        // by a code path the product does not take.
        var input = Tone(SortformerGeometry.SampleRate, 440, 0.5);
        var output = Run(SortformerGeometry.SampleRate, input);

        Assert.Equal(input.Length, output.Length);
        for (var i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i], output[i]);
        }
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(22050)]
    [InlineData(8000)]
    public void ItProducesTheRightNumberOfSamples(int rate)
    {
        const double Seconds = 1.5;
        var output = Run(rate, Tone(rate, 300, Seconds));

        // Within one sample of the ideal: the last output whose whole kernel fits is the last one
        // produced, and where that falls depends on the ratio.
        var expected = (int)(SortformerGeometry.SampleRate * Seconds);
        Assert.InRange(output.Length, expected - 2, expected + 2);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void ASpeechBandToneSurvivesAtItsOwnAmplitude(int rate)
    {
        // 1 kHz is comfortably inside the passband at every rate here, so what comes out should be
        // the same tone at the same level. A resampler that got its gain wrong would change every
        // feature by a constant, which the log-mel would carry straight through.
        var output = Run(rate, Tone(rate, 1000, 1.0, amplitude: 0.5));
        Assert.Equal(0.5 / Math.Sqrt(2), Level(output), 2);
    }

    [Fact]
    public void ATonePastTheOutputNyquistIsRemovedRatherThanFoldedDown()
    {
        // 15 kHz in a 48 kHz file is above the 8 kHz the 16 kHz output can represent. Decimating by
        // taking every third sample would alias it to 1 kHz, in the middle of speech, at full
        // amplitude and with nothing to show for it. It has to be filtered out instead.
        var output = Run(48000, Tone(48000, 15000, 1.0, amplitude: 0.5));

        var passband = Level(Run(48000, Tone(48000, 1000, 1.0, amplitude: 0.5)));
        Assert.True(
            Level(output) < passband / 100,
            $"a 15 kHz tone came through at {Level(output):g3} against a passband level of {passband:g3}; " +
            "that is aliasing, not resampling.");
    }

    [Fact]
    public void BlockBoundariesDoNotChangeTheResult()
    {
        // The filter reaches 32 zero crossings either side of every output sample, so it spans many
        // input blocks. A caller's block size is an implementation detail of whatever decoded the
        // file and must not reach the output.
        var input = Tone(48000, 700, 0.4);

        var whole = Run(48000, input, blockSize: input.Length);
        var trickled = Run(48000, input, blockSize: 1);
        var awkward = Run(48000, input, blockSize: 997);

        Assert.Equal(whole.Length, trickled.Length);
        Assert.Equal(whole.Length, awkward.Length);
        for (var i = 0; i < whole.Length; i++)
        {
            Assert.Equal(whole[i], trickled[i]);
            Assert.Equal(whole[i], awkward[i]);
        }
    }

    [Fact]
    public void SilenceStaysSilent()
    {
        var output = Run(44100, new float[44100]);
        Assert.All(output, s => Assert.Equal(0f, s));
    }
}
