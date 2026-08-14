using Parakeet.Core.Audio;

namespace Parakeet.Core.Tests;

/// <summary>An in-memory audio source, so segmentation tests need no files.</summary>
internal sealed class ArrayAudioSource : IAudioSource
{
    private readonly float[] _samples;
    private readonly int _blockSize;

    public ArrayAudioSource(float[] samples, int sampleRate = 16_000, int blockSize = 4096)
    {
        _samples = samples;
        _blockSize = blockSize;
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    public TimeSpan? Duration => TimeSpan.FromSeconds(_samples.Length / (double)SampleRate);

    public bool Disposed { get; private set; }

    public async IAsyncEnumerable<ReadOnlyMemory<float>> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var offset = 0; offset < _samples.Length; offset += _blockSize)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return _samples.AsMemory(offset, Math.Min(_blockSize, _samples.Length - offset));
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal static class TestAudio
{
    public const int SampleRate = 16_000;

    /// <summary>Silence with a low dither floor, the way a real quiet room records.</summary>
    public static void FillQuiet(Span<float> destination, Random random, float amplitude = 0.0005f)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = (float)((random.NextDouble() * 2 - 1) * amplitude);
        }
    }

    /// <summary>A loud, modulated tone: not speech, but energy where speech would be.</summary>
    public static void FillTone(Span<float> destination, float amplitude = 0.4f, double frequency = 220)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            var t = i / (double)SampleRate;
            destination[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * t) * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 4 * t)));
        }
    }

    /// <summary>Builds quiet/loud stretches from a list of (seconds, isSpeech) pairs.</summary>
    public static float[] Build(params (double Seconds, bool Speech)[] parts)
    {
        var random = new Random(1234);
        var total = parts.Sum(p => (int)(p.Seconds * SampleRate));
        var buffer = new float[total];
        var offset = 0;

        foreach (var (seconds, speech) in parts)
        {
            var count = (int)(seconds * SampleRate);
            var span = buffer.AsSpan(offset, count);

            if (speech)
            {
                FillTone(span);
            }
            else
            {
                FillQuiet(span, random);
            }

            offset += count;
        }

        return buffer;
    }
}
