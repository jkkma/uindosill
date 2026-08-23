namespace Parakeet.Audio;

/// <summary>
/// Converts an arbitrary sample rate to the 16 kHz the model was trained at, with a windowed-sinc
/// filter that band-limits before it decimates.
/// </summary>
/// <remarks>
/// <para>
/// The ASR path does not need this — parakeet.cpp resamples inside the native library, which is why
/// <see cref="Parakeet.Core.Audio.IAudioSource"/> deliberately does not force a rate. The diariser
/// does: the ONNX graph takes mel features computed at exactly 16 kHz, and feeding it a 48 kHz file
/// decimated by dropping samples would fold everything above 8 kHz back down into the speech band.
/// </para>
/// <para>
/// <b>Nothing measured has been through this code.</b> The DER this project reports is on AMI, which
/// is 16 kHz already, so the resampler is bypassed entirely on the only material it has been scored
/// against. That is recorded in <c>docs/UNPROVEN.md</c> rather than left to be assumed either way.
/// </para>
/// <para>
/// The filter is a Blackman-windowed sinc with 32 zero crossings either side of the cutoff, which
/// is at the lower of the two Nyquist frequencies. Note what that means when downsampling: the
/// cutoff moves down by the ratio, so the kernel stretches by it, and the tap count per output
/// grows with it — <c>64 x ratio</c>, so 193 taps for 48 kHz and 771 for 192 kHz, not a fixed 64.
/// </para>
/// <para>
/// <b>The kernel is tabulated, and that is what makes this usable on a long recording.</b> Until
/// 2026-08-23 <see cref="Kernel"/> was evaluated per tap per output sample — a sine and two cosines
/// each time — which at 48 kHz is about 9.3 million transcendental calls per second of audio.
/// Measured on the desktop it ran at <b>25.7x realtime</b>; tabulated it runs at <b>722x</b>, and
/// 44.1 kHz at 784x. The old remark called a tabulated kernel the answer "if a very high input rate
/// ever matters"; 48 kHz is not a high rate, and it mattered.
/// </para>
/// <para>
/// <b>This was most of what a diarisation cost, which nobody had noticed because the two were
/// timed together.</b> On <c>csb384-8438.m4a</c> (10 min) the labelling pass reported 25.3 s and
/// 24x realtime; after this change the same file reports <b>3.3 s and 183x</b>, with identical
/// turns. So roughly nine tenths of what looked like the diariser was this filter, and the model's
/// own speed — the thing every earlier sentence about "the diariser runs at Nx" was describing —
/// was never measured apart from it. It also hid the GPU: CPU against WebGPU was 37.0 s to 25.3 s,
/// a ratio of 1.5x that says the provider barely mattered, and is 10.6 s to 3.2 s once the shared
/// bottleneck is gone.
/// </para>
/// <para>
/// <b>It is a phase table rather than an interpolated one, so nothing is approximated.</b> Every
/// sample rate is a rational multiple of 16 kHz. Reduce <c>source/target</c> to <c>A/B</c> and
/// output <c>n</c> sits at input position <c>n*A/B</c>, whose fractional part depends only on
/// <c>n mod B</c> — one phase for 48 kHz (where every output lands exactly on an input sample), 160
/// for 44.1 kHz. Each phase's taps are computed once with the same <see cref="Kernel"/> and reused,
/// so a tap value here is a value that function returned, not an interpolation between two of them.
/// </para>
/// <para>
/// <b>What that does and does not preserve.</b> For an integer ratio the phase is zero and the
/// centre is an exact integer either way, so the output is bit-identical to the untabulated
/// version — pinned by test. For a non-integer ratio it is not: the centre now comes from exact
/// integer arithmetic on <c>A</c> and <c>B</c> rather than from <c>n * ratio</c> accumulating
/// rounding in a double, which is the more accurate of the two and differs from the old result in
/// the last bits. That difference is measured rather than asserted — see <c>docs/UNPROVEN.md</c>.
/// </para>
/// </remarks>
public sealed class Resampler
{
    /// <summary>Zero crossings of the sinc kept either side of each output sample.</summary>
    private const int HalfWidth = 32;

    /// <summary>
    /// Above this many phases the table is not built and the kernel is evaluated per tap, as it
    /// always was.
    /// </summary>
    /// <remarks>
    /// The phase count is the reduced denominator, so it is 1 for 48 kHz and 160 for 44.1 kHz —
    /// but a rate coprime with 16000, which a deliberately odd file could carry, makes it 16000 and
    /// the table tens of megabytes. Falling back keeps the memory bounded and costs only the speed
    /// this class had before, on inputs nobody has.
    /// </remarks>
    private const int MaximumPhases = 4096;

    private readonly double _ratio;
    private readonly double _cutoff;
    private readonly double _taps;

    /// <summary>Numerator and denominator of <c>source/target</c>, reduced.</summary>
    private readonly long _numerator;
    private readonly long _phases;

    /// <summary>Whole and fractional parts of <c>p*A/B</c>, per phase. Null when tabulation is off.</summary>
    private readonly long[]? _phaseWhole;
    private readonly double[]? _phaseFraction;

    /// <summary>The lowest tap offset each phase reaches, and that phase's tap values.</summary>
    private readonly long[]? _phaseLowest;
    private readonly double[][]? _phaseKernel;

    private float[] _history = [];
    private int _historyCount;
    private long _historyBase;
    private long _outputIndex;
    private bool _completed;

    public Resampler(int sourceRate, int targetRate = 16000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetRate, 1);

        _ratio = sourceRate / (double)targetRate;

        // Downsampling has to cut at the *output's* Nyquist or the discarded band aliases into
        // speech; upsampling cuts at the input's, because there is nothing above it to keep.
        _cutoff = Math.Min(1.0, 1.0 / _ratio);
        _taps = HalfWidth / _cutoff;

        var divisor = Gcd(sourceRate, targetRate);
        _numerator = sourceRate / divisor;
        _phases = targetRate / divisor;

        if (IsIdentity || _phases > MaximumPhases)
        {
            return;
        }

        _phaseWhole = new long[_phases];
        _phaseFraction = new double[_phases];
        _phaseLowest = new long[_phases];
        _phaseKernel = new double[_phases][];

        for (var phase = 0; phase < _phases; phase++)
        {
            // Integer arithmetic, so the fractional part is the exact rational one rather than
            // whatever a double multiply accumulated by output n.
            var position = phase * _numerator;
            _phaseWhole[phase] = position / _phases;
            var fraction = (position % _phases) / (double)_phases;
            _phaseFraction[phase] = fraction;

            // The same window Emit derives from the centre: i runs over [ceil(c - taps),
            // floor(c + taps)], so k = centreInt - i runs over [-floor(f + taps), floor(taps - f)].
            var lowest = -(long)Math.Floor(fraction + _taps);
            var highest = (long)Math.Floor(_taps - fraction);
            _phaseLowest[phase] = lowest;

            var taps = new double[highest - lowest + 1];
            for (var index = 0; index < taps.Length; index++)
            {
                taps[index] = Kernel(lowest + index + fraction);
            }

            _phaseKernel[phase] = taps;
        }
    }

    private static int Gcd(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    /// <summary>True when the rates match and the samples can pass straight through.</summary>
    public bool IsIdentity => _ratio == 1.0;

    /// <summary>
    /// How many distinct phases the tabulated kernel holds, or 0 when it is evaluated per tap.
    /// </summary>
    /// <remarks>Exposed so a test can assert which of the two paths a given rate takes.</remarks>
    public int TabulatedPhases => _phaseKernel?.Length ?? 0;

    /// <summary>Adds source samples and appends whatever output they complete to <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> block, List<float> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (IsIdentity)
        {
            foreach (var sample in block)
            {
                output.Add(sample);
            }

            return;
        }

        Append(block);
        Emit(output, flush: false);
    }

    /// <summary>Declares the input finished and emits the tail, which needs no future samples.</summary>
    public void Complete(List<float> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _completed = true;
        if (!IsIdentity)
        {
            Emit(output, flush: true);
        }
    }

    private void Append(ReadOnlySpan<float> block)
    {
        if (_historyCount + block.Length > _history.Length)
        {
            Array.Resize(ref _history, Math.Max(_historyCount + block.Length, Math.Max(1024, _history.Length * 2)));
        }

        block.CopyTo(_history.AsSpan(_historyCount));
        _historyCount += block.Length;
    }

    /// <summary>
    /// Where output <paramref name="index"/> sits in the input, split into an exact integer and a
    /// fraction below one.
    /// </summary>
    /// <remarks>
    /// <c>n = q*B + p</c>, so the position is <c>q*A + p*A/B</c> — the first term exact, the second
    /// one of <c>B</c> values that depend only on the phase. Without a table it falls back to the
    /// double multiply this class always used.
    /// </remarks>
    private void Centre(long index, out long whole, out double fraction, out int phase)
    {
        if (_phaseWhole is null || _phaseFraction is null)
        {
            var centre = index * _ratio;
            whole = (long)Math.Floor(centre);
            fraction = centre - whole;
            phase = -1;
            return;
        }

        phase = (int)(index % _phases);
        whole = ((index / _phases) * _numerator) + _phaseWhole[phase];
        fraction = _phaseFraction[phase];
    }

    private void Emit(List<float> output, bool flush)
    {
        var lastAvailable = _historyBase + _historyCount - 1;

        while (true)
        {
            Centre(_outputIndex, out var centreWhole, out var centreFraction, out var phase);
            var centre = centreWhole + centreFraction;
            var last = (long)Math.Floor(centre + _taps);

            // Without every tap the sum would be truncated, which is a different filter rather than
            // a slightly worse one — so wait, unless there is nothing more coming.
            if (!flush && last > lastAvailable)
            {
                break;
            }

            // On the last flush the kernel is allowed to run off the end — beyond the signal there
            // is silence, and truncating the sum is what that means — but the output's own centre
            // must still land inside it: at or after the first sample, and before the point one
            // sample past the last, which is where the recording ends. Emitting past that invents
            // audio: the count would depend on the filter's width rather than on the recording's
            // length, and a 48 kHz file would come out 32 samples longer than it is. Stopping at
            // the last sample itself was the opposite mistake, and until 2026-08-22 it was this
            // line's: 8,000 samples at 8 kHz came out as 15,999 at 16 kHz, one short of the second.
            if (flush && centre >= lastAvailable + 1)
            {
                break;
            }

            var first = (long)Math.Ceiling(centre - _taps);
            var from = Math.Max(first, _historyBase);
            var to = Math.Min(last, lastAvailable);

            var sum = 0.0;

            if (phase >= 0 && _phaseKernel is not null && _phaseLowest is not null)
            {
                // k = centreWhole - i, and the tap for it was computed once when this phase's table
                // was built. Walking i upwards walks k downwards, which is why the index is
                // subtracted rather than added.
                var taps = _phaseKernel[phase];
                var lowest = _phaseLowest[phase];

                for (var i = from; i <= to; i++)
                {
                    var index = centreWhole - i - lowest;
                    if ((ulong)index < (ulong)taps.Length)
                    {
                        sum += _history[i - _historyBase] * taps[index];
                    }
                }
            }
            else
            {
                for (var i = from; i <= to; i++)
                {
                    sum += _history[i - _historyBase] * Kernel(centre - i);
                }
            }

            output.Add((float)(sum * _cutoff));
            _outputIndex++;
        }

        Discard();
    }

    /// <summary>A Blackman-windowed sinc, zero outside its support.</summary>
    private double Kernel(double distance)
    {
        if (Math.Abs(distance) >= _taps)
        {
            return 0.0;
        }

        var x = _cutoff * distance;
        var sinc = x == 0.0 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

        var position = 0.5 * (1.0 + distance / _taps);
        var window = 0.42 - 0.5 * Math.Cos(2.0 * Math.PI * position) + 0.08 * Math.Cos(4.0 * Math.PI * position);
        return sinc * window;
    }

    private void Discard()
    {
        if (_completed)
        {
            return;
        }

        Centre(_outputIndex, out var centreWhole, out var centreFraction, out _);
        var lowest = (long)Math.Ceiling(centreWhole + centreFraction - _taps);
        var drop = (int)Math.Min(Math.Max(0L, lowest - _historyBase), _historyCount);
        if (drop <= 0)
        {
            return;
        }

        Array.Copy(_history, drop, _history, 0, _historyCount - drop);
        _historyCount -= drop;
        _historyBase += drop;
    }
}
