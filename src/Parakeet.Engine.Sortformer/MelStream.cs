namespace Parakeet.Engine.Sortformer;

/// <summary>
/// NeMo's <c>FilterbankFeatures</c> for this checkpoint, as a forward-only window over a stream of
/// 16 kHz mono samples.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it streams.</b> The reference implementation loads a whole meeting into memory, features
/// and all, which is fine for a measurement script and not for a product: three hours of 16 kHz
/// float32 is 690 MB of samples before a single feature exists, on top of the ~1.3 GB the ONNX
/// session settles at. Here the samples pass through and the features exist only for the chunk
/// being fed to the graph, so the footprint is a few megabytes whatever the file's length. The cost
/// is that the total length is not known in advance, which the chunk loop is written to tolerate —
/// see <see cref="SortformerChunkPlan"/>.
/// </para>
/// <para>
/// <b>What it reproduces.</b> Pre-emphasis at 0.97, a 400-sample Hann window with
/// <c>periodic=false</c> zero-padded into a 512-point transform, a 160-sample hop, centred framing
/// with constant (zero) padding, the power spectrum, 128 Slaney-normalised mel filters,
/// <c>log(x + 2^-24)</c>, <b>no normalisation</b>, and frames at or past
/// <c>floor(samples / 160)</c> zeroed. The two that are easy to get wrong and expensive to get
/// wrong are the last two: <c>normalize: NA</c> in the checkpoint means this model does not
/// normalise, where nearly every NeMo ASR config says <c>per_feature</c>, and a frame past the
/// valid length carries zeros rather than whatever the padding happened to produce.
/// </para>
/// </remarks>
internal sealed class MelStream
{
    private const int Hop = SortformerGeometry.WindowStride;
    private const int FftSize = SortformerGeometry.FftSize;
    private const int Bins = FftSize / 2 + 1;
    private const int Bands = SortformerGeometry.MelBands;

    /// <summary>Samples of zero padding either side of the signal, from <c>center=True</c>.</summary>
    private const int CentrePad = FftSize / 2;

    private static readonly float[][] Filterbank = MelFilterbank.Build();
    private static readonly double[] Window = MelFilterbank.BuildPaddedWindow();

    private readonly Fft _fft = new();
    private readonly double[] _frameScratch = new double[FftSize];
    private readonly double[] _powerScratch = new double[Bins];

    /// <summary>Pre-emphasised samples, holding the tail the pending frames still need.</summary>
    private float[] _samples = new float[Hop * 512];
    private int _sampleCount;

    /// <summary>Global index of <c>_samples[0]</c> in the pre-emphasised signal.</summary>
    private long _sampleBase;

    /// <summary>Computed mel frames, left-aligned; <c>_frames[0]</c> is frame <see cref="_frameBase"/>.</summary>
    private float[] _frames = [];
    private int _frameCount;
    private int _frameBase;

    private float _previousSample;
    private bool _anySamples;
    private bool _completed;
    private long _totalSamples;

    /// <summary>
    /// Frames the transform produces in total, or null until the source has been read to its end.
    /// <c>1 + samples / 160</c>, before padding up to a multiple of 16.
    /// </summary>
    public int? TotalTransformFrames => _completed ? (int)(1 + _totalSamples / Hop) : null;

    /// <summary>
    /// Frames holding real audio, or null until the end is known. <c>floor(samples / 160)</c> —
    /// NeMo's <c>get_seq_len</c>, which cancels the centre padding out exactly. Frames at or past
    /// this are zeroed, so a transform frame that straddles the end contributes nothing.
    /// </summary>
    public int? ValidFrames => _completed ? (int)(_totalSamples / Hop) : null;

    /// <summary>
    /// Frames the graph is fed in total: <see cref="TotalTransformFrames"/> rounded up to a
    /// multiple of 16, the <c>pad_to</c> the checkpoint asks for.
    /// </summary>
    public int? PaddedFrames =>
        TotalTransformFrames is { } total ? total + ((-total) % SortformerGeometry.PadToMultiple + SortformerGeometry.PadToMultiple) % SortformerGeometry.PadToMultiple : null;

    /// <summary>Appends the next block of 16 kHz mono samples, pre-emphasising as it goes.</summary>
    public void Append(ReadOnlySpan<float> block)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The stream has already been completed.");
        }

        if (block.IsEmpty)
        {
            return;
        }

        EnsureSampleCapacity(_sampleCount + block.Length);
        var destination = _samples.AsSpan(_sampleCount);

        // y[0] = x[0]; y[n] = x[n] - 0.97 x[n-1]. The carry crosses block boundaries, which is the
        // whole reason this is a field rather than a local.
        var start = 0;
        if (!_anySamples)
        {
            destination[0] = block[0];
            _previousSample = block[0];
            _anySamples = true;
            start = 1;
        }

        for (var i = start; i < block.Length; i++)
        {
            var sample = block[i];
            destination[i] = sample - SortformerGeometry.Preemphasis * _previousSample;
            _previousSample = sample;
        }

        _sampleCount += block.Length;
        _totalSamples += block.Length;
    }

    /// <summary>Declares the source exhausted, which is what makes the totals knowable.</summary>
    public void Complete() => _completed = true;

    /// <summary>
    /// Makes frames <c>[first, first + count)</c> available and returns how many of them exist —
    /// fewer than asked for only when the source has ended, which is how the chunk loop discovers
    /// it has reached the end without being told the length in advance.
    /// </summary>
    /// <remarks>
    /// <paramref name="first"/> must not go backwards: everything below it is discarded, because
    /// holding it would defeat the point of streaming.
    /// </remarks>
    public int Prepare(int first, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (first < _frameBase)
        {
            throw new ArgumentOutOfRangeException(
                nameof(first), first, $"Frames are produced forward only and {_frameBase} has already been passed.");
        }

        DiscardBefore(first);

        // Capped at the padded length, not the transform length: the frames between the two are the
        // `pad_to: 16` rounding, and the graph is fed them as zeros rather than being given a short
        // chunk. ComputeFrame already zeroes anything at or past the valid length, so they cost
        // nothing to produce.
        var available = PaddedFrames ?? int.MaxValue;
        var wanted = (int)Math.Min(count, Math.Max(0L, available - (long)first));

        // A frame is computable once the samples it reads have arrived. Reading past the end is
        // only legitimate after Complete(), where the missing samples are the centre padding.
        while (_frameBase + _frameCount < first + wanted)
        {
            var next = _frameBase + _frameCount;
            if (!_completed && (long)next * Hop + FftSize - CentrePad > _sampleBase + _sampleCount)
            {
                break;
            }

            EnsureFrameCapacity(_frameCount + 1);
            ComputeFrame(next, _frames.AsSpan(_frameCount * Bands, Bands));
            _frameCount++;
        }

        return Math.Max(0, Math.Min(wanted, _frameBase + _frameCount - first));
    }

    /// <summary>One prepared frame, 128 log-mel values.</summary>
    public ReadOnlySpan<float> Frame(int index)
    {
        var offset = index - _frameBase;
        if (offset < 0 || offset >= _frameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "That frame has not been prepared.");
        }

        return _frames.AsSpan(offset * Bands, Bands);
    }

    /// <summary>
    /// Whether the frame carries audio rather than padding. Frames at or past the valid length are
    /// zeroed by <c>FilterbankFeatures</c> before the model sees them.
    /// </summary>
    private bool IsPadding(int frame) => ValidFrames is { } valid && frame >= valid;

    private void ComputeFrame(int frame, Span<float> destination)
    {
        if (IsPadding(frame))
        {
            destination.Clear();
            return;
        }

        // The transform window over the centre-padded signal: frame f reads padded[f*160 ..
        // f*160+512), and padded[i] is the signal at i - 256, zero outside it.
        var origin = (long)frame * Hop - CentrePad;
        var scratch = _frameScratch;
        for (var i = 0; i < FftSize; i++)
        {
            var index = origin + i - _sampleBase;
            var sample = index >= 0 && index < _sampleCount ? _samples[index] : 0f;
            scratch[i] = sample * Window[i];
        }

        _fft.PowerSpectrum(scratch, _powerScratch);

        for (var band = 0; band < Bands; band++)
        {
            var weights = Filterbank[band];
            var sum = 0.0;
            for (var bin = 0; bin < Bins; bin++)
            {
                sum += weights[bin] * _powerScratch[bin];
            }

            destination[band] = (float)Math.Log(sum + SortformerGeometry.LogZeroGuard);
        }
    }

    private void DiscardBefore(int frame)
    {
        var drop = frame - _frameBase;
        if (drop > 0 && _frameCount > 0)
        {
            var keep = Math.Max(0, _frameCount - drop);
            if (keep > 0)
            {
                Array.Copy(_frames, drop * Bands, _frames, 0, keep * Bands);
            }

            _frameCount = keep;
        }

        _frameBase = frame;

        // Frame `frame` reads from sample frame*160 - 256; nothing before that is ever wanted again.
        var lowest = (long)frame * Hop - CentrePad;
        var dropSamples = (int)Math.Min(Math.Max(0L, lowest - _sampleBase), _sampleCount);
        if (dropSamples > 0)
        {
            Array.Copy(_samples, dropSamples, _samples, 0, _sampleCount - dropSamples);
            _sampleCount -= dropSamples;
            _sampleBase += dropSamples;
        }
    }

    private void EnsureSampleCapacity(int required)
    {
        if (_samples.Length >= required)
        {
            return;
        }

        Array.Resize(ref _samples, Math.Max(required, _samples.Length * 2));
    }

    private void EnsureFrameCapacity(int frames)
    {
        if (_frames.Length >= frames * Bands)
        {
            return;
        }

        Array.Resize(ref _frames, Math.Max(frames, 64) * Bands * 2);
    }
}
