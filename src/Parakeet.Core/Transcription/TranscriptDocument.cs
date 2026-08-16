namespace Parakeet.Core.Transcription;

/// <summary>
/// A finished transcript plus the provenance a reader needs to judge it: which model
/// produced it, at which quantisation, on which backend, and how long it took. Quantisation
/// quality on this engine is measured on one corpus only (docs/UNPROVEN.md), so a transcript
/// that cannot say which weights made it is not a result you can act on.
/// </summary>
public sealed record TranscriptDocument
{
    public required IReadOnlyList<TranscriptSegment> Segments { get; init; }

    /// <summary>Path or display name of the audio this came from.</summary>
    public string? SourceName { get; init; }

    public TimeSpan? AudioDuration { get; init; }

    public string? ModelId { get; init; }

    public string? Quantisation { get; init; }

    public ComputeBackend? Backend { get; init; }

    public string? Language { get; init; }

    /// <summary>Wall-clock time spent decoding, excluding model load.</summary>
    public TimeSpan? ProcessingTime { get; init; }

    /// <summary>
    /// Real-time factor: processing time divided by audio duration. Lower is faster.
    /// Null unless both durations are known and the audio is non-empty.
    /// </summary>
    public double? RealTimeFactor =>
        ProcessingTime is { } processing && AudioDuration is { Ticks: > 0 } audio
            ? processing.Ticks / (double)audio.Ticks
            : null;

    public static TranscriptDocument Empty { get; } = new() { Segments = [] };

    /// <summary>Joined text of every non-empty segment, one space between segments.</summary>
    public string Text => string.Join(" ", Segments.Where(s => !s.IsEmpty).Select(s => s.Text.Trim()));

    public bool IsEmpty => Segments.All(s => s.IsEmpty);

    /// <summary>
    /// Words whose confidence falls below <paramref name="threshold"/>. Not a correctness
    /// signal on its own — it is where a human should look first.
    /// </summary>
    public IEnumerable<TranscriptWord> LowConfidenceWords(float threshold) =>
        Segments.SelectMany(s => s.Words).Where(w => w.Confidence is { } c && c < threshold);
}
