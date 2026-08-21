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
/// Each tap costs a sine and two cosines, because the kernel is evaluated rather than tabulated.
/// At 48 kHz that is about 9 million transcendental calls per second of audio, which is real work
/// and is still small against the model: the diariser itself runs at roughly 65x realtime, so a
/// second of audio already costs it ~15 ms. Nothing here has been profiled, and if a very high
/// input rate ever matters the answer is a tabulated kernel with linear interpolation — measured
/// first.
/// </para>
/// </remarks>
public sealed class Resampler
{
    /// <summary>Zero crossings of the sinc kept either side of each output sample.</summary>
    private const int HalfWidth = 32;

    private readonly double _ratio;
    private readonly double _cutoff;
    private readonly double _taps;

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
    }

    /// <summary>True when the rates match and the samples can pass straight through.</summary>
    public bool IsIdentity => _ratio == 1.0;

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

    private void Emit(List<float> output, bool flush)
    {
        var lastAvailable = _historyBase + _historyCount - 1;

        while (true)
        {
            var centre = _outputIndex * _ratio;
            var last = (long)Math.Floor(centre + _taps);

            // Without every tap the sum would be truncated, which is a different filter rather than
            // a slightly worse one — so wait, unless there is nothing more coming.
            if (!flush && last > lastAvailable)
            {
                break;
            }

            // On the last flush the kernel is allowed to run off the end — beyond the signal there
            // is silence, and truncating the sum is what that means — but the output's own centre
            // must still land inside it. Emitting past that invents audio: the count would depend
            // on the filter's width rather than on the recording's length, and a 48 kHz file would
            // come out 32 samples longer than it is.
            if (flush && centre > lastAvailable)
            {
                break;
            }

            var first = (long)Math.Ceiling(centre - _taps);

            var sum = 0.0;
            for (var i = Math.Max(first, _historyBase); i <= Math.Min(last, lastAvailable); i++)
            {
                sum += _history[i - _historyBase] * Kernel(centre - i);
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

        var lowest = (long)Math.Ceiling(_outputIndex * _ratio - _taps);
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
