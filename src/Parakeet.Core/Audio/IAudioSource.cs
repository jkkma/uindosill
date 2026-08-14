namespace Parakeet.Core.Audio;

/// <summary>
/// A source of mono float32 PCM. Serves files today and live microphone capture later:
/// the only difference a consumer sees is that <see cref="Duration"/> is null when the
/// end is not known in advance.
/// </summary>
public interface IAudioSource : IAsyncDisposable
{
    /// <summary>Sample rate of the samples yielded by <see cref="ReadAsync"/>.</summary>
    /// <remarks>
    /// Deliberately not forced to 16 kHz. parakeet.cpp resamples internally when the rate
    /// differs, which keeps a resampler off the critical path and out of this codebase.
    /// </remarks>
    int SampleRate { get; }

    /// <summary>Total duration when known, or null for an open-ended source.</summary>
    TimeSpan? Duration { get; }

    /// <summary>
    /// Reads the source as a sequence of mono float32 blocks in the range [-1, 1].
    /// Block sizes are an implementation detail and must not be relied upon.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<float>> ReadAsync(CancellationToken ct = default);
}
