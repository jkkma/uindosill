using Parakeet.Audio;

namespace Parakeet.Audio.Tests;

/// <summary>
/// The resampler's accounting rather than its filter. Nothing measured has been through this code
/// — the DER material is 16 kHz already, and <see cref="Resampler"/>'s own remarks say so — so what
/// is held here is what can be held without a corpus: that a second of audio comes out as a second,
/// whatever rate it came in at and however it was chunked.
/// </summary>
public class ResamplerTests
{
    private static float[] Tone(int rate, double seconds, double hertz = 440)
    {
        var samples = new float[(int)Math.Round(rate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * hertz * i / rate));
        }

        return samples;
    }

    private static List<float> Whole(Resampler resampler, float[] input)
    {
        var output = new List<float>();
        resampler.Process(input, output);
        resampler.Complete(output);
        return output;
    }

    [Theory]
    [InlineData(8_000)]
    [InlineData(22_050)]
    [InlineData(44_100)]
    [InlineData(48_000)]
    [InlineData(96_000)]
    public void OneSecondInIsSixteenThousandSamplesOut(int rate)
    {
        // Upsampling 8 kHz stopped one sample short — 15,999 for a second — until 2026-08-22,
        // because the last output was allowed only up to the last input *sample* and not up to the
        // end of that sample's period, which is where the recording ends. Downsampling was exact.
        var output = Whole(new Resampler(rate), Tone(rate, 1.0));

        Assert.Equal(16_000, output.Count);
    }

    [Fact]
    public void ChunkedInputProducesTheSameSamplesAsTheWholeFile()
    {
        // The streaming contract: the caller's block size is not allowed to change the answer.
        var input = Tone(48_000, 2.0);
        var whole = Whole(new Resampler(48_000), input);

        var chunked = new List<float>();
        var resampler = new Resampler(48_000);
        for (var offset = 0; offset < input.Length; offset += 1_000)
        {
            resampler.Process(input.AsSpan(offset, Math.Min(1_000, input.Length - offset)), chunked);
        }

        resampler.Complete(chunked);

        Assert.Equal(whole, chunked);
    }

    [Fact]
    public void TheIdentityRatePassesSamplesThroughUntouched()
    {
        var input = Tone(16_000, 0.5);
        var resampler = new Resampler(16_000);

        Assert.True(resampler.IsIdentity);
        Assert.Equal(input, Whole(resampler, input));
    }

    // ── The phase table, added 2026-08-23 ──────────────────────────────────────────────────────
    //
    // The kernel used to be evaluated per tap per output sample — a sine and two cosines each time,
    // about 9.3 million transcendental calls per second of audio at 48 kHz. Measured, that ran at
    // 25.7x realtime against the diariser model's own 24x, so converting a recording cost as much
    // as labelling it. The taps are tabulated per phase now. What these hold is that tabulating did
    // not change the filter.

    /// <summary>
    /// The filter exactly as it was before the phase table, for the new one to be held against.
    /// </summary>
    /// <remarks>
    /// A copy, which is normally the thing this repository refuses — but a reference implementation
    /// is the one case where a second copy is the point: it is frozen on purpose, and its whole job
    /// is to disagree if the real one changes. It is here rather than in the library so that
    /// nothing ships it.
    /// </remarks>
    private static List<float> Untabulated(int sourceRate, float[] input, int targetRate = 16_000)
    {
        var ratio = sourceRate / (double)targetRate;
        var cutoff = Math.Min(1.0, 1.0 / ratio);
        var taps = 32 / cutoff;

        double Kernel(double distance)
        {
            if (Math.Abs(distance) >= taps)
            {
                return 0.0;
            }

            var x = cutoff * distance;
            var sinc = x == 0.0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

            var position = 0.5 * (1.0 + (distance / taps));
            var window = 0.42 - (0.5 * Math.Cos(2.0 * Math.PI * position)) + (0.08 * Math.Cos(4.0 * Math.PI * position));
            return sinc * window;
        }

        var output = new List<float>();
        if (ratio == 1.0)
        {
            output.AddRange(input);
            return output;
        }

        var lastAvailable = (long)input.Length - 1;

        for (long index = 0; ; index++)
        {
            var centre = index * ratio;
            if (centre >= lastAvailable + 1)
            {
                break;
            }

            var first = (long)Math.Ceiling(centre - taps);
            var last = (long)Math.Floor(centre + taps);

            var sum = 0.0;
            for (var i = Math.Max(first, 0); i <= Math.Min(last, lastAvailable); i++)
            {
                sum += input[i] * Kernel(centre - i);
            }

            output.Add((float)(sum * cutoff));
        }

        return output;
    }

    [Theory]
    [InlineData(32_000, 1)]
    [InlineData(48_000, 1)]
    [InlineData(96_000, 1)]
    [InlineData(8_000, 2)]
    public void ADyadicRatioIsBitIdenticalToTheUntabulatedFilter(int rate, int phases)
    {
        // What buys bit-identity is not "integer" but a reduced denominator that is a power of two,
        // which is what makes every phase fraction exactly representable and n * ratio exact. 48 kHz
        // reduces to 3/1 and every output lands on an input sample; 8 kHz reduces to 1/2 and half of
        // them land halfway between two, on the exact double 0.5. Either way the centre is the same
        // value both ways round, the tap distances are the same, and the sum is taken in the same
        // order — not "close", the same doubles, so the same floats out.
        var input = Tone(rate, 0.75, hertz: 300);
        var resampler = new Resampler(rate);

        Assert.Equal(phases, resampler.TabulatedPhases);

        var tabulated = Whole(resampler, input);
        var reference = Untabulated(rate, input);

        Assert.Equal(reference.Count, tabulated.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            // BitConverter rather than Assert.Equal on the float, so that a difference of one ulp
            // is a failure rather than a rounding nobody sees.
            Assert.Equal(
                BitConverter.SingleToInt32Bits(reference[i]),
                BitConverter.SingleToInt32Bits(tabulated[i]));
        }
    }

    [Theory]
    [InlineData(44_100, 160)]
    [InlineData(22_050, 320)]
    public void ANonIntegerRatioTabulatesEveryPhaseAndStaysWithinDoubleRounding(int rate, int phases)
    {
        // 44100/16000 reduces to 441/160, so the fractional part of the centre takes 160 values and
        // no more. The centre now comes from exact integer arithmetic on those two numbers rather
        // than from n * ratio accumulating rounding in a double — which is the more accurate of the
        // two and is NOT bit-identical to what came before. The difference is bounded here rather
        // than waved at: it is float rounding, not a different filter.
        var input = Tone(rate, 0.75, hertz: 300);
        var resampler = new Resampler(rate);

        Assert.Equal(phases, resampler.TabulatedPhases);

        var tabulated = Whole(resampler, input);
        var reference = Untabulated(rate, input);

        Assert.Equal(reference.Count, tabulated.Count);

        var worst = 0.0;
        for (var i = 0; i < reference.Count; i++)
        {
            worst = Math.Max(worst, Math.Abs(reference[i] - tabulated[i]));
        }

        // The samples are in [-1, 1]; a float carries about 1.2e-07 of resolution there. This bound
        // is a few of those, which is what re-associating the last bits of a 177-tap sum costs.
        Assert.True(worst < 1e-6, $"worst sample difference was {worst:e3}, which is more than rounding");
    }

    [Fact]
    public void AnOddRateFallsBackToEvaluatingTheKernel()
    {
        // A rate coprime with 16000 would need one phase per output sample of a second — tens of
        // megabytes of table — so it is not built, and the filter is the one this class always had.
        var resampler = new Resampler(44_101);

        Assert.Equal(0, resampler.TabulatedPhases);
        Assert.Equal(16_000, Whole(resampler, Tone(44_101, 1.0)).Count);
    }
}
