namespace Parakeet.Core.Transcription;

public enum TranscriptionStage
{
    /// <summary>Reading and decoding the container into PCM.</summary>
    Reading,

    /// <summary>Cutting the audio into decodable segments.</summary>
    Segmenting,

    /// <summary>Running the model.</summary>
    Decoding,

    /// <summary>Assembling the finished transcript.</summary>
    Finalising,
}

/// <summary>Progress of a single file. Immutable so it can cross threads freely.</summary>
public sealed record TranscriptionProgress
{
    public required TranscriptionStage Stage { get; init; }

    /// <summary>How much audio has been decoded so far.</summary>
    public TimeSpan Processed { get; init; }

    /// <summary>Total audio duration, when known.</summary>
    public TimeSpan? Total { get; init; }

    public int SegmentsCompleted { get; init; }

    /// <summary>Total segment count once segmentation has finished, otherwise null.</summary>
    public int? SegmentsTotal { get; init; }

    /// <summary>
    /// Completion in [0, 1] when the total is known, otherwise null. Callers must render
    /// indeterminate progress rather than inventing a number.
    /// </summary>
    public double? Fraction =>
        Total is { Ticks: > 0 } total
            ? Math.Clamp(Processed.Ticks / (double)total.Ticks, 0d, 1d)
            : SegmentsTotal is > 0
                ? Math.Clamp(SegmentsCompleted / (double)SegmentsTotal.Value, 0d, 1d)
                : null;
}
