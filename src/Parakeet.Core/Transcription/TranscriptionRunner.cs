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
        };
    }
}
