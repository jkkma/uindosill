namespace Parakeet.Engine.Sortformer.Tests;

/// <summary>
/// The fixtures' input signals, generated rather than committed.
/// </summary>
/// <remarks>
/// The mirror of the identically-named functions in <c>scripts/make-diariser-fixtures.py</c>. Only
/// the reference implementation's <i>output</i> is committed; the input is a formula evaluated on
/// both sides, so a fixture cannot come adrift from the signal that produced it and no audio has to
/// enter the repository. Every expression here is exact in the arithmetic it is written in — the
/// generator is stepped in integers and the signals are built in <see langword="double"/> and
/// narrowed once, as NumPy does — so the two languages agree bit for bit rather than nearly.
/// </remarks>
internal static class DeterministicInputs
{
    /// <summary>
    /// The glibc linear congruential generator, in exact 31-bit integer arithmetic, yielding
    /// values in [-1, 1). Only the top bits are taken: an LCG's low-order bits are notoriously
    /// short-period, and dividing the whole state would put that structure into the signal.
    /// </summary>
    public static double[] Lcg(int seed, int count)
    {
        var values = new double[count];
        long state = seed & 0x7FFFFFFF;
        for (var i = 0; i < count; i++)
        {
            state = (1103515245L * state + 12345L) & 0x7FFFFFFF;
            values[i] = ((state >> 7) / (double)(1 << 23)) - 1.0;
        }

        return values;
    }

    public static float[] Signal(string name, int count)
    {
        var samples = new float[count];
        switch (name)
        {
            case "silence":
                return samples;

            case "ramp":
                for (var i = 0; i < count; i++)
                {
                    samples[i] = (float)(-1.0 + 2.0 * i / (count - 1));
                }

                return samples;

            case "noise":
                var noise = Lcg(20260819, count);
                for (var i = 0; i < count; i++)
                {
                    samples[i] = (float)noise[i] * 0.25f;
                }

                return samples;

            case "tones":
                for (var i = 0; i < count; i++)
                {
                    var t = i / (double)SortformerGeometry.SampleRate;
                    var envelope = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 3.0 * t);
                    samples[i] = (float)(envelope * (
                        0.30 * Math.Sin(2 * Math.PI * 220.0 * t)
                        + 0.20 * Math.Sin(2 * Math.PI * 1310.0 * t + 0.7)
                        + 0.10 * Math.Sin(2 * Math.PI * 3700.0 * t + 1.9)));
                }

                return samples;

            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "No such fixture signal.");
        }
    }

    /// <summary>Per-frame speaker activity: soft square waves at co-prime periods, plus jitter.</summary>
    public static float[] Probabilities(int frames, int speakers)
    {
        var jitter = Lcg(770415, frames * speakers);
        var periods = new[] { 97, 131, 163, 211 };
        var probabilities = new float[frames * speakers];

        for (var c = 0; c < speakers; c++)
        {
            var period = periods[c];
            for (var f = 0; f < frames; f++)
            {
                var wave = Math.Sin(2 * Math.PI * f / period + 0.9 * c);
                var value = 1.0 / (1.0 + Math.Exp(-6.0 * wave));
                probabilities[f * speakers + c] = (float)Math.Min(1.0, Math.Max(0.0, value + 0.14 * jitter[f * speakers + c]));
            }
        }

        return probabilities;
    }
}
