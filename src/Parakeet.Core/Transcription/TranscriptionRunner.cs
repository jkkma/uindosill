using System.Diagnostics;
using Parakeet.Core.Audio;

namespace Parakeet.Core.Transcription;

/// <summary>Drives one engine over one source and assembles the finished document.</summary>
public static class TranscriptionRunner
{
    public static async Task<TranscriptDocument> RunAsync(
        ITranscriptionEngine engine,
        IAudioSource audio,
        TranscriptionOptions? options = null,
        string? sourceName = null,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(audio);
        options ??= TranscriptionOptions.Default;

        // Validated before anything is paid for. TranscribeAsync validates too, but it is a lazy
        // iterator whose first line runs only at the first MoveNext — after the load below — so
        // without this check a typo'd option would cost a multi-hundred-megabyte model load
        // before it was refused.
        options.Validate();

        // Loaded before the stopwatch starts, because the stopwatch's figure is documented as
        // excluding model load and is the basis of every published real-time factor. LoadAsync is
        // idempotent, so a caller that pre-loaded (every shipping caller does) pays nothing —
        // but a caller that leans on TranscribeAsync's load-for-you contract used to get seconds
        // of model load silently inside ProcessingTime, and a real-time factor that measured the
        // wrong thing with no indication anything was off.
        await engine.LoadAsync(ct).ConfigureAwait(false);

        var segments = new List<TranscriptSegment>();
        var stopwatch = Stopwatch.StartNew();

        await foreach (var segment in engine.TranscribeAsync(audio, options, progress, ct).ConfigureAwait(false))
        {
            segments.Add(segment);
        }

        stopwatch.Stop();

        var capabilities = engine.Capabilities;

        return new TranscriptDocument
        {
            Segments = segments,
            SourceName = sourceName,
            AudioDuration = audio.Duration,
            ModelId = capabilities.ModelId,
            Quantisation = capabilities.Quantisation,
            Backend = capabilities.Backend,
            Language = options.Language,
            ProcessingTime = stopwatch.Elapsed,

            // The model's own share, from an engine that measures it; the wall figure above is the
            // whole pass and is what every published real-time factor is.
            DecodeTime = (engine as SegmentingTranscriptionEngine)?.LastDecodeDuration,

            // What cut it, from the same engine and for the same reason: a segment count is a
            // figure with a method, and since 2026-08-23 the method is not one thing.
            SpeechDetector = (engine as SegmentingTranscriptionEngine)?.LastSegmentationReport?.SpeechDetector,
        };
    }
}
