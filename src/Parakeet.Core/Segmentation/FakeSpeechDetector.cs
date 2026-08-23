using Parakeet.Core.Audio;

namespace Parakeet.Core.Segmentation;

/// <summary>
/// A speech detector with no model behind it, for the tests and the canned pipeline: either a
/// script that says what the probability is at each moment, or a loudness rule that behaves like
/// the energy gate so the fake pipeline keeps producing what it always produced.
/// </summary>
/// <remarks>
/// It counts what was done to it — streams opened, at which rates, streams closed — because the
/// contract worth holding is the engine's: one stream per recording, opened at the recording's
/// own rate, closed when the recording is, and never shared between two files.
/// </remarks>
public sealed class FakeSpeechDetector : ISpeechDetector
{
    private readonly Func<double, float>? _probabilityAt;
    private readonly float _loudnessDb;
    private readonly List<int> _openedRates = [];

    /// <summary>
    /// A detector that answers <paramref name="probabilityAt"/> for the time (in seconds from the
    /// start of the recording) of the last sample pushed — or, when null, 1 for any block louder
    /// than <paramref name="loudnessDb"/> dBFS and 0 otherwise.
    /// </summary>
    public FakeSpeechDetector(Func<double, float>? probabilityAt = null, float loudnessDb = -40f)
    {
        _probabilityAt = probabilityAt;
        _loudnessDb = loudnessDb;
    }

    public string Name => "fake speech detector";

    /// <summary>How many streams have been opened, over the life of this detector.</summary>
    public int Opened => _openedRates.Count;

    /// <summary>The rate each stream was opened at, in order.</summary>
    public IReadOnlyList<int> OpenedRates => _openedRates;

    /// <summary>How many of those streams have been disposed.</summary>
    public int Closed { get; private set; }

    public bool Disposed { get; private set; }

    public ISpeechDetectorStream Open(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ObjectDisposedException.ThrowIf(Disposed, this);
        _openedRates.Add(sampleRate);
        return new Stream(this, sampleRate);
    }

    public void Dispose() => Disposed = true;

    private sealed class Stream : ISpeechDetectorStream
    {
        private readonly FakeSpeechDetector _owner;
        private readonly int _sampleRate;
        private long _samples;
        private bool _disposed;

        public Stream(FakeSpeechDetector owner, int sampleRate)
        {
            _owner = owner;
            _sampleRate = sampleRate;
        }

        public string Name => _owner.Name;

        public float Push(ReadOnlySpan<float> samples)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _samples += samples.Length;

            if (_owner._probabilityAt is { } script)
            {
                return Math.Clamp(script(_samples / (double)_sampleRate), 0f, 1f);
            }

            return samples.Length > 0 && AudioMath.RmsDecibels(samples) >= _owner._loudnessDb ? 1f : 0f;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Closed++;
        }
    }
}
