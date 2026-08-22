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
}
