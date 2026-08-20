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
    /// The speaker turns a labeller produced for this audio, in file time, or empty when speaker
    /// labelling did not run. These are the labeller's output as such — what an RTTM file carries
    /// and what a diarisation scorer reads — not the per-word attribution derived from them, which
    /// lives on the segments.
    /// </summary>
    public IReadOnlyList<Diarisation.SpeakerTurn> SpeakerTurns { get; init; } = [];

    /// <summary>
    /// Which model named the speakers, when one did. Provenance, for the same reason
    /// <see cref="ModelId"/> is: a label whose source is unknown cannot be re-examined.
    /// </summary>
    public string? SpeakerModelId { get; init; }

    /// <summary>True when speaker labelling ran, whether or not it found anyone.</summary>
    public bool HasSpeakers => SpeakerModelId is not null || SpeakerTurns.Count > 0;

    /// <summary>
    /// BCP-47 tag of the language this transcript was translated into, or null when it is what the
    /// engine wrote. Only ever <c>en</c> today; carried as a tag rather than a flag so a document
    /// that grows a second target does not have to grow a second field.
    /// </summary>
    /// <remarks>
    /// Provenance, and not decoration: a translated transcript is a second model's opinion of a
    /// first model's output, and a reader who cannot tell which they are holding cannot judge
    /// either. The formats that have somewhere to put it say so in-band; the ones that do not —
    /// SubRip has no comment syntax and plain text has no header — are covered by the <c>.en</c>
    /// infix in their file names.
    /// </remarks>
    public string? TranslatedTo { get; init; }

    /// <summary>
    /// Which model wrote the English, when one did. Beside <see cref="ModelId"/> and
    /// <see cref="SpeakerModelId"/>, for the same reason both of those are here.
    /// </summary>
    public string? TranslationModelId { get; init; }

    /// <summary>True when a translation pass ran.</summary>
    public bool IsTranslated => TranslatedTo is not null;

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
