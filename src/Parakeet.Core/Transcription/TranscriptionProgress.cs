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

    /// <summary>The opt-in second pass: reading the audio again and deciding who spoke when.</summary>
    LabellingSpeakers,

    /// <summary>
    /// The opt-in last pass: rewriting the finished transcript in English. No audio is read — it
    /// works on segments — so what it reports progress against is the transcript rather than the
    /// file.
    /// </summary>
    Translating,
}

/// <summary>Progress of a single file. Immutable so it can cross threads freely.</summary>
public sealed record TranscriptionProgress
{
    public required TranscriptionStage Stage { get; init; }

    /// <summary>
    /// What is happening inside the stage, when the stage alone does not say it. Null when the
    /// stage is the whole answer, which is every report but one.
    /// </summary>
    /// <remarks>
    /// <see cref="TranscriptionStage.LabellingSpeakers"/> is two pieces of work with one name: the
    /// host reads and resamples the whole file again and writes it out for the sidecar, and only
    /// then does the sidecar run the model. On a three-hour recording the first piece is minutes
    /// long and reported nothing at all, so the row sat at whatever the transcription pass had left
    /// on it — 100% — with a status that never changed. That is the shape of a hang, and it was
    /// mistaken for one. A stage cannot carry the distinction because both halves genuinely are
    /// speaker labelling; this says which half.
    /// </remarks>
    public string? Detail { get; init; }

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
