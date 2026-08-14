using Parakeet.Core.Audio;

namespace Parakeet.Core.Segmentation;

/// <summary>
/// Cuts a stream of mono float32 PCM into segments short enough to decode reliably.
/// </summary>
/// <remarks>
/// <para>
/// Streaming rather than buffer-the-file: three hours of 16 kHz mono float32 is 690 MB, and a
/// desktop app that needs the whole recording resident before it can start is one that dies
/// on the recordings people actually have.
/// </para>
/// <para>
/// The detector is an adaptive energy gate with hysteresis. It has one job — put boundaries
/// in gaps rather than through words — and two rules it never breaks: audio classified as
/// speech is never dropped, and when the cap forces a cut, the cut goes at the quietest
/// frame nearby instead of at an arbitrary sample.
/// </para>
/// <para>
/// TEN-VAD is deliberately not used here: its modified Apache-2.0 licence carries an Agora
/// non-compete clause.
/// </para>
/// </remarks>
public sealed class StreamingSegmenter
{
    private enum State
    {
        Idle,
        Speech,
    }

    private readonly VoiceActivityOptions _options;
    private readonly int _sampleRate;
    private readonly int _frameSamples;
    private readonly int _minSpeechFrames;
    private readonly int _minSilenceFrames;
    private readonly int _preRollFrames;
    private readonly int _postRollFrames;
    private readonly int _maxSegmentFrames;
    private readonly int _minSegmentFrames;
    private readonly int _splitSearchFrames;

    private readonly List<float> _buffer = [];
    private readonly List<float> _frameDb = [];
    private readonly float[] _partialFrame;

    private int _partialCount;
    private long _bufferStartSample;
    private long _totalSamples;
    private State _state = State.Idle;
    private int _speechRun;
    private int _silenceRun;
    private int _nextIndex;
    private long _segmentedSamples;
    private long _speechFrames;
    private bool _digitalSilence = true;
    private float _peak;
    private float _noiseFloorDb;

    public StreamingSegmenter(int sampleRate, VoiceActivityOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _options = options ?? VoiceActivityOptions.Default;
        _options.Validate();
        _sampleRate = sampleRate;

        _frameSamples = Math.Max(1, (int)Math.Round(_options.FrameLength.TotalSeconds * sampleRate));
        _partialFrame = new float[_frameSamples];

        _minSpeechFrames = FramesFor(_options.MinSpeechDuration, minimum: 1);
        _minSilenceFrames = FramesFor(_options.MinSilenceDuration, minimum: 1);
        _preRollFrames = FramesFor(_options.PaddingBefore, minimum: 0);
        _postRollFrames = FramesFor(_options.PaddingAfter, minimum: 0);
        _maxSegmentFrames = Math.Max(2, FramesFor(_options.MaxSegmentLength, minimum: 2));
        _minSegmentFrames = FramesFor(_options.MinSegmentLength, minimum: 1);
        _splitSearchFrames = Math.Clamp(FramesFor(_options.ForcedSplitSearchWindow, minimum: 1), 1, _maxSegmentFrames - 1);

        // Start from "quiet room" and adapt upward, never from the first frame. Seeding from the
        // first frame means a recording that opens on speech teaches the detector that speech is
        // the noise floor, and the file comes back empty.
        _noiseFloorDb = _options.AbsoluteThresholdDb;
    }

    /// <summary>Frames whose energy sat above the speech threshold.</summary>
    public TimeSpan SpeechDuration => TimeSpan.FromSeconds(_speechFrames * _frameSamples / (double)_sampleRate);

    /// <summary>Running estimate of the noise floor, in dBFS.</summary>
    public float NoiseFloorDb => _noiseFloorDb;

    /// <summary>
    /// Feeds a block of samples and appends any segments it completed to
    /// <paramref name="completed"/>. Block sizes need not align to frames.
    /// </summary>
    public void Push(ReadOnlySpan<float> block, List<AudioSegment> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        _totalSamples += block.Length;
        TrackLevels(block);

        var offset = 0;
        while (offset < block.Length)
        {
            var take = Math.Min(_frameSamples - _partialCount, block.Length - offset);
            block.Slice(offset, take).CopyTo(_partialFrame.AsSpan(_partialCount));
            _partialCount += take;
            offset += take;

            if (_partialCount == _frameSamples)
            {
                ProcessFrame(_partialFrame, completed);
                _partialCount = 0;
            }
        }
    }

    /// <summary>
    /// Ends the stream, emitting whatever is still open. A short utterance that ends at
    /// end-of-file is emitted here even though it never satisfied the silence rule.
    /// </summary>
    public void Flush(List<AudioSegment> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);

        if (_partialCount > 0)
        {
            // Zero-pad the tail to a whole frame so its energy is measured on the same basis
            // as every other frame rather than exaggerated by a short window.
            Array.Clear(_partialFrame, _partialCount, _frameSamples - _partialCount);
            ProcessFrame(_partialFrame, completed);
            _partialCount = 0;
        }

        if (_state == State.Speech || _speechRun > 0)
        {
            EmitFrames(_frameDb.Count, completed, speechDetected: true);
        }

        _state = State.Idle;
        _speechRun = 0;
        _silenceRun = 0;
    }

    public SegmentationReport CreateReport() => new()
    {
        SegmentCount = _nextIndex,
        TotalAudio = TimeSpan.FromSeconds(_totalSamples / (double)_sampleRate),
        SegmentedAudio = TimeSpan.FromSeconds(_segmentedSamples / (double)_sampleRate),
        SpeechAudio = SpeechDuration,
        IsDigitalSilence = _digitalSilence,
        PeakDb = AudioMath.ToDecibels(_peak),
        NoiseFloorDb = _noiseFloorDb,
    };

    private int FramesFor(TimeSpan duration, int minimum) =>
        Math.Max(minimum, (int)Math.Round(duration.TotalSeconds * _sampleRate / _frameSamples));

    private void TrackLevels(ReadOnlySpan<float> block)
    {
        foreach (var sample in block)
        {
            if (sample != 0f)
            {
                _digitalSilence = false;
            }

            var magnitude = Math.Abs(sample);
            if (magnitude > _peak)
            {
                _peak = magnitude;
            }
        }
    }

    private void ProcessFrame(ReadOnlySpan<float> frame, List<AudioSegment> completed)
    {
        var db = AudioMath.RmsDecibels(frame);

        // The gate runs even in fixed-window mode so the report still describes the audio
        // honestly — the mode changes what is decoded, not what was measured.
        var aboveThreshold = UpdateGate(db);
        var isSpeech = !_options.Enabled || aboveThreshold;

        if (aboveThreshold)
        {
            _speechFrames++;
        }

        _buffer.AddRange(frame);
        _frameDb.Add(db);

        if (_state == State.Idle)
        {
            ProcessIdleFrame(isSpeech);
        }
        else
        {
            ProcessSpeechFrame(isSpeech, completed);
        }
    }

    /// <summary>
    /// Updates the adaptive noise floor and answers whether this frame is speech. The floor
    /// falls quickly and rises slowly, so a passage of quiet does not drag the threshold up
    /// behind it and swallow the speech that follows.
    /// </summary>
    private bool UpdateGate(float db)
    {
        if (db < _noiseFloorDb)
        {
            _noiseFloorDb = (0.90f * _noiseFloorDb) + (0.10f * db);
        }
        else
        {
            _noiseFloorDb = (0.995f * _noiseFloorDb) + (0.005f * db);
        }

        // The floor may not climb high enough to hide speech behind it. Without this clamp a
        // sustained loud passage drags the threshold up over the following speech.
        var ceiling = _options.AbsoluteSpeechDb - _options.SpeechMarginDb;
        if (_noiseFloorDb > ceiling)
        {
            _noiseFloorDb = ceiling;
        }

        var threshold = Math.Max(_noiseFloorDb + _options.SpeechMarginDb, _options.AbsoluteThresholdDb);
        return db >= threshold;
    }

    private void ProcessIdleFrame(bool isSpeech)
    {
        if (isSpeech)
        {
            _speechRun++;
            if (_speechRun >= _minSpeechFrames)
            {
                // Open a segment starting pre-roll frames before the onset.
                var onsetFrame = _frameDb.Count - _speechRun;
                var keepFrom = Math.Max(0, onsetFrame - _preRollFrames);
                TrimFront(keepFrom);
                _state = State.Speech;
                _silenceRun = 0;
            }

            return;
        }

        _speechRun = 0;

        // Idle: retain only enough tail to serve as pre-roll for a future onset.
        var retain = _preRollFrames + _minSpeechFrames;
        if (_frameDb.Count > retain)
        {
            TrimFront(_frameDb.Count - retain);
        }
    }

    private void ProcessSpeechFrame(bool isSpeech, List<AudioSegment> completed)
    {
        if (isSpeech)
        {
            _silenceRun = 0;
        }
        else
        {
            _silenceRun++;
        }

        if (_silenceRun >= _minSilenceFrames)
        {
            var offsetFrame = _frameDb.Count - _silenceRun;
            var endFrame = Math.Min(_frameDb.Count, offsetFrame + _postRollFrames);

            // Too short to be worth a decode on its own: keep accumulating so the fragment is
            // glued to the next utterance rather than decoded alone or, worse, dropped.
            if (endFrame < _minSegmentFrames)
            {
                return;
            }

            EmitFrames(endFrame, completed, speechDetected: true);
            _state = State.Idle;
            _speechRun = 0;
            _silenceRun = 0;
            return;
        }

        if (_frameDb.Count >= _maxSegmentFrames)
        {
            var cut = FindQuietestFrame(_frameDb.Count - _splitSearchFrames, _frameDb.Count);
            EmitFrames(cut + 1, completed, speechDetected: true);

            // Still mid-utterance; the remaining tail carries whatever silence run it had.
            _silenceRun = Math.Min(_silenceRun, _frameDb.Count);
        }
    }

    /// <summary>Index of the lowest-energy frame in [from, to), clamped to a valid range.</summary>
    private int FindQuietestFrame(int from, int to)
    {
        from = Math.Clamp(from, 1, Math.Max(1, _frameDb.Count - 1));
        to = Math.Clamp(to, from + 1, _frameDb.Count);

        var best = from;
        var bestDb = float.MaxValue;
        for (var i = from; i < to; i++)
        {
            if (_frameDb[i] < bestDb)
            {
                bestDb = _frameDb[i];
                best = i;
            }
        }

        return best;
    }

    private void EmitFrames(int frameCount, List<AudioSegment> completed, bool speechDetected)
    {
        frameCount = Math.Clamp(frameCount, 0, _frameDb.Count);
        if (frameCount == 0)
        {
            return;
        }

        var sampleCount = Math.Min(frameCount * _frameSamples, _buffer.Count);
        if (sampleCount == 0)
        {
            return;
        }

        var samples = new float[sampleCount];
        _buffer.CopyTo(0, samples, 0, sampleCount);

        completed.Add(new AudioSegment
        {
            Index = _nextIndex++,
            SampleRate = _sampleRate,
            Start = TimeSpan.FromSeconds(_bufferStartSample / (double)_sampleRate),
            Samples = samples,
            SpeechDetected = speechDetected,
        });

        _segmentedSamples += sampleCount;
        TrimFront(frameCount);
    }

    private void TrimFront(int frameCount)
    {
        if (frameCount <= 0)
        {
            return;
        }

        frameCount = Math.Min(frameCount, _frameDb.Count);
        var sampleCount = Math.Min(frameCount * _frameSamples, _buffer.Count);

        _buffer.RemoveRange(0, sampleCount);
        _frameDb.RemoveRange(0, frameCount);
        _bufferStartSample += sampleCount;
    }
}
