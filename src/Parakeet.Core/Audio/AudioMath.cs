namespace Parakeet.Core.Audio;

/// <summary>Small, allocation-free measurements over float32 PCM.</summary>
public static class AudioMath
{
    /// <summary>dBFS value used for a frame that is exactly zero, so callers never see -infinity.</summary>
    public const float SilenceFloorDb = -120f;

    /// <summary>Root-mean-square amplitude of a block, in [0, 1] for well-formed audio.</summary>
    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0f;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return (float)Math.Sqrt(sum / samples.Length);
    }

    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        return peak;
    }

    /// <summary>Converts a linear amplitude in [0, 1] to dBFS, floored at <see cref="SilenceFloorDb"/>.</summary>
    public static float ToDecibels(float amplitude)
    {
        if (amplitude <= 0f || float.IsNaN(amplitude))
        {
            return SilenceFloorDb;
        }

        var db = 20f * MathF.Log10(amplitude);
        return db < SilenceFloorDb ? SilenceFloorDb : db;
    }

    public static float RmsDecibels(ReadOnlySpan<float> samples) => ToDecibels(Rms(samples));

    /// <summary>
    /// A sample count at a rate, as a <see cref="TimeSpan"/> exact to the nearest tick.
    /// </summary>
    /// <remarks>
    /// Not <c>TimeSpan.FromSeconds(samples / (double)rate)</c>: that truncates to the tick, so a
    /// time one tick under a millisecond boundary prints as the millisecond before — 9,120 samples
    /// at 16 kHz is 0.57 s, and came out as <c>00:00:00,569</c> in a subtitle while the JSON beside
    /// it said 0.57. Integer arithmetic here, so every sample-indexed time in the pipeline lands on
    /// the tick it means. GOTCHAS §25 has the history; until 2026-08-22 only the RTTM path had the
    /// fix.
    /// </remarks>
    public static TimeSpan SamplesToTime(long samples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        return TimeSpan.FromTicks((samples * TimeSpan.TicksPerSecond + sampleRate / 2) / sampleRate);
    }

    /// <summary>
    /// Seconds as a <see cref="TimeSpan"/>, rounded to the nearest tick rather than truncated — for
    /// a time that arrives as a decimal, which is what the native decoder's word times and the
    /// sidecar's turn boundaries are.
    /// </summary>
    public static TimeSpan SecondsToTime(double seconds) =>
        TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));

    /// <summary>
    /// True when every sample is exactly zero. Worth distinguishing from "quiet": a muted
    /// input device, a dead track in a video container and a wrong channel all produce this,
    /// and telling the user "no audio on this track" is help, where an empty transcript is not.
    /// </summary>
    public static bool IsDigitalSilence(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            if (sample != 0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with low-level dither around zero. Used to warm an
    /// engine up on audio that is quiet enough to decode to nothing but not digitally silent,
    /// so the warm-up exercises the real code path.
    /// </summary>
    public static void FillDither(Span<float> destination, int seed = 0x5EED, float amplitude = 1e-4f)
    {
        // Deterministic on purpose: a warm-up that differs run to run makes the first
        // measured decode differ run to run.
        var random = new Random(seed);
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = (float)((random.NextDouble() * 2.0 - 1.0) * amplitude);
        }
    }
}
