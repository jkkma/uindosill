namespace Parakeet.Engine.Sortformer;

/// <summary>
/// A fixed-size radix-2 FFT for the featurizer's 512-point frames, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. The one caller wants the power spectrum of a real 512-sample frame, so the
/// transform size is a compile-time constant, the bit-reversal permutation and twiddle factors are
/// built once, and there is no planning, no strategy and no other size. That is what makes it
/// reviewable next to the reference it has to agree with.
/// </para>
/// <para>
/// A real-input transform would halve the work. It is not here because the featurizer runs its
/// frames in parallel and the whole mel stage is a small fraction of the model's own cost, so the
/// simpler transform is the one that can be checked by eye against the fixture.
/// </para>
/// <para>
/// Computed in <see langword="double"/> where PyTorch's is <see langword="float"/>. The port is
/// therefore not bit-identical to the reference and cannot be: the two sum the same products in
/// different orders, and one of them carries more mantissa. The featurizer test measures the
/// resulting deviation rather than asserting there is none.
/// </para>
/// </remarks>
internal sealed class Fft
{
    private const int Size = SortformerGeometry.FftSize;
    private const int Stages = 9; // 2^9 == 512

    private static readonly int[] Reversed = BuildBitReversal();
    private static readonly double[] TwiddleReal = BuildTwiddles(cosine: true);
    private static readonly double[] TwiddleImaginary = BuildTwiddles(cosine: false);

    private readonly double[] _real = new double[Size];
    private readonly double[] _imaginary = new double[Size];

    /// <summary>
    /// Transforms <paramref name="frame"/> (exactly <see cref="SortformerGeometry.FftSize"/> real
    /// samples) and writes <c>|X[k]|^2</c> for the non-negative frequencies into
    /// <paramref name="power"/>, which must hold <c>FftSize / 2 + 1</c> values.
    /// </summary>
    /// <remarks>
    /// The magnitude is squared, not taken and then squared: NeMo computes
    /// <c>sqrt(re^2 + im^2)</c> and raises it to <c>mag_power = 2</c>, which is the same number
    /// with one fewer rounding, so this returns the sum directly.
    /// </remarks>
    public void PowerSpectrum(ReadOnlySpan<double> frame, Span<double> power)
    {
        if (frame.Length != Size)
        {
            throw new ArgumentException($"The transform is fixed at {Size} points, got {frame.Length}.", nameof(frame));
        }

        if (power.Length != Size / 2 + 1)
        {
            throw new ArgumentException($"Expected {Size / 2 + 1} power bins, got {power.Length}.", nameof(power));
        }

        var re = _real;
        var im = _imaginary;

        for (var i = 0; i < Size; i++)
        {
            re[i] = frame[Reversed[i]];
            im[i] = 0.0;
        }

        var twiddleStride = Size / 2;
        for (var stage = 0; stage < Stages; stage++)
        {
            var half = 1 << stage;
            var span = half << 1;

            for (var start = 0; start < Size; start += span)
            {
                var twiddle = 0;
                for (var k = 0; k < half; k++, twiddle += twiddleStride)
                {
                    var lower = start + k;
                    var upper = lower + half;

                    var wr = TwiddleReal[twiddle];
                    var wi = TwiddleImaginary[twiddle];

                    var tr = re[upper] * wr - im[upper] * wi;
                    var ti = re[upper] * wi + im[upper] * wr;

                    re[upper] = re[lower] - tr;
                    im[upper] = im[lower] - ti;
                    re[lower] += tr;
                    im[lower] += ti;
                }
            }

            twiddleStride >>= 1;
        }

        for (var k = 0; k < power.Length; k++)
        {
            power[k] = re[k] * re[k] + im[k] * im[k];
        }
    }

    private static int[] BuildBitReversal()
    {
        var reversed = new int[Size];
        for (var i = 0; i < Size; i++)
        {
            var value = 0;
            for (var bit = 0; bit < Stages; bit++)
            {
                value = (value << 1) | ((i >> bit) & 1);
            }

            reversed[i] = value;
        }

        return reversed;
    }

    /// <summary>
    /// <c>exp(-2*pi*i*k/Size)</c> for every k, so each stage reads a stride of this one table
    /// rather than recomputing a trigonometric function inside the butterfly loop.
    /// </summary>
    private static double[] BuildTwiddles(bool cosine)
    {
        var table = new double[Size / 2];
        for (var k = 0; k < table.Length; k++)
        {
            var angle = -2.0 * Math.PI * k / Size;
            table[k] = cosine ? Math.Cos(angle) : Math.Sin(angle);
        }

        return table;
    }
}
