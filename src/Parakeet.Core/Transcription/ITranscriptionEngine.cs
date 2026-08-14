using Parakeet.Core.Audio;

namespace Parakeet.Core.Transcription;

/// <summary>
/// A speech-to-text engine. The one abstraction the rest of the app is allowed to know
/// about: no implementation detail of parakeet.cpp, ONNX Runtime or anything else may
/// leak through this interface.
/// </summary>
public interface ITranscriptionEngine : IAsyncDisposable
{
    EngineCapabilities Capabilities { get; }

    /// <summary>
    /// Loads the model. Idempotent, expensive (hundreds of milliseconds to seconds), and
    /// must never be called on a UI thread. Implementations warm up here so that the
    /// first real decode is not paying arena allocation and graph JIT.
    /// </summary>
    ValueTask LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Transcribes an audio source, yielding segments as they are produced so a caller can
    /// render a long file incrementally. The same shape serves one utterance at a time for
    /// push-to-talk dictation later.
    /// </summary>
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        IAudioSource audio,
        TranscriptionOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default);
}
