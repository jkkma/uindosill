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

    /// <summary>
    /// The language hint the caller passed with the request, or null when none was — and that is
    /// the whole story, by decision (docs/V2-ASK-THE-TRANSCRIPT.md, 2026-08-24): nothing detects
    /// a language the user did not state, and consumers treat null as "unknown", never as a
    /// value to infer.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Wall-clock time of the whole transcription pass, excluding model load: from the first block
    /// read to the last segment out, with the container decode, the mixdown, the resampling and
    /// the segmentation inside it, serialised with the model. It is what <see cref="RealTimeFactor"/>
    /// and every published real-time factor are computed from. It is not the model's own time —
    /// that is <see cref="DecodeTime"/>, and until 2026-08-22 this one was the figure every
    /// document called "decode time".
    /// </summary>
    public TimeSpan? ProcessingTime { get; init; }

    /// <summary>
    /// Time spent inside the model's decode calls alone, summed over the pass, when the engine
    /// measured it. The difference from <see cref="ProcessingTime"/> is the read and the
    /// segmentation — a rounding error against a CPU decode and a material share of a fast GPU one.
    /// </summary>
    public TimeSpan? DecodeTime { get; init; }

    /// <summary>
    /// What cut this recording into the pieces the model decoded: the energy gate's name, a neural
    /// detector's <see cref="Segmentation.ISpeechDetector.Name"/> with its runtime, or
    /// <see cref="Segmentation.StreamingSegmenter.FixedWindowsName"/> when nothing decided. Null
    /// from an engine that does not report it. Provenance on the same terms as <see cref="Backend"/>
    /// and for the same reason: since 2026-08-23 a default run may be cut by either detector, and a
    /// segment count that cannot say which is a figure without its method.
    /// </summary>
    public string? SpeechDetector { get; init; }

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

    /// <summary>
    /// Which execution provider named the speakers, when a labeller ran. Beside
    /// <see cref="SpeakerModelId"/> rather than folded into it, because the model alone need not
    /// identify the answer. That was established rather than assumed, on the diariser retired
    /// 2026-08-27: measured on AMI test, that one graph scored 16.3324% DER on the CPU, 16.3319% on
    /// WebGPU and 16.1021% on CUDA (docs/UNPROVEN.md). Whether the shipping pipeline's device moves
    /// its labels is unmeasured, which is a reason to keep recording the device rather than to stop.
    /// A transcript naming only the model says which weights ran and not which labels they produced,
    /// which is half a provenance.
    /// </summary>
    public ComputeBackend? SpeakerBackend { get; init; }

    /// <summary>
    /// Which runtime ran the speaker embedder, as <c>runtime:provider</c> — <c>torch:cpu</c> or
    /// <c>onnxruntime:webgpu</c>. Null when the labeller reports no embedder — the canned one — and
    /// otherwise <c>torch:&lt;device&gt;</c>, since the shipping pipeline runs both stages on one
    /// runtime. It was null for every labeller but DiariZen, which had two; that engine left on
    /// 2026-08-27.
    /// </summary>
    /// <remarks>
    /// <b><see cref="SpeakerBackend"/> cannot carry this, which is the whole reason for a second
    /// field.</b> The second diariser runs segmentation in torch and negotiates a provider only for
    /// its embedder, and the torch embedder and ONNX Runtime's CPU provider both report
    /// <see cref="ComputeBackend.Cpu"/> — so a transcript recording only that word cannot say which
    /// of the two produced its labels. They are not interchangeable: measured 2026-08-26, the ONNX
    /// embedder returns 222 speaker turns where torch returns 225 on the same ten-minute recording,
    /// differing over 0.19% of the timeline. Recording the provider and not the runtime would be
    /// the same half-provenance this field's neighbour exists to prevent.
    /// </remarks>
    public string? SpeakerEmbeddingBackend { get; init; }

    /// <summary>
    /// The speaker count the caller asked for, when one was asked for at all. Null means the
    /// labeller's own estimate was taken as it came.
    /// </summary>
    /// <remarks>
    /// Recorded whether or not it changed anything, which is the whole of its value: a run made
    /// with <c>--speaker-count 2</c> that the model had already satisfied folds nothing, and
    /// without this field it is byte-for-byte indistinguishable from a run where no count was
    /// given. Those are different transcripts to judge — one had its label set constrained to a
    /// number a human supplied, the other did not — and <see cref="SpeakerFolds"/> alone cannot
    /// tell them apart, because it is empty in both.
    /// </remarks>
    public int? RequestedSpeakerCount { get; init; }

    /// <summary>
    /// The merges <see cref="RequestedSpeakerCount"/> forced, in the order they were made, or empty
    /// when it forced none. The labeller's own labels on both sides, before display renaming.
    /// </summary>
    /// <remarks>
    /// Provenance in the same sense as <see cref="SpeakerBackend"/>, and for a sharper reason than
    /// either model id: a fold does not merely say what produced these labels, it is an edit made
    /// to them after the model was done. Two labels the diariser kept apart were joined because a
    /// number was supplied, and each merge carries the evidence it was made on — near-zero overlap
    /// is one voice that drifted onto a second label, a large overlap with little margin is two
    /// people the count has just put under one name. A reader who cannot see that cannot tell a
    /// repaired transcript from an unedited one.
    /// </remarks>
    public IReadOnlyList<Diarisation.SpeakerFold> SpeakerFolds { get; init; } = [];

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

    /// <summary>
    /// Which execution provider wrote the English, when a translation pass ran. Its own field
    /// rather than one shared with <see cref="SpeakerBackend"/>: the two engines resolve their
    /// providers independently inside the sidecar, so a single run can label speakers on one and
    /// translate on another, and a shared field would have to pick which of them to lie about.
    /// </summary>
    public ComputeBackend? TranslationBackend { get; init; }

    /// <summary>
    /// The search that produced the English — beam width, length cap, length penalty, early
    /// stopping — as the translator described it, when it did. The graphs are pinned and the
    /// search over them is not, so a transcript that names the checkpoint and the provider has
    /// named half of what produced its English.
    /// </summary>
    public string? TranslationDecode { get; init; }

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

    /// <summary>
    /// <see cref="DecodeTime"/> over the audio's duration — the model's own real-time factor,
    /// beside <see cref="RealTimeFactor"/>, which is the whole pass's and the one the published
    /// figures are.
    /// </summary>
    public double? DecodeRealTimeFactor =>
        DecodeTime is { } decode && AudioDuration is { Ticks: > 0 } audio
            ? decode.Ticks / (double)audio.Ticks
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

    /// <summary>
    /// The segmentation's identity: a SHA-256 over the segments — per segment the start and the
    /// end in seconds as the JSON export renders them (three decimals, no trailing zeros), then
    /// the text, each followed by one LF, all UTF-8, in order. A segment id is only meaningful
    /// against one segmentation — the same audio transcribed again by another model gives ids
    /// that point at different words while looking perfectly fine — so this is what decision 5's
    /// transcript pin and the question sets' pin against `scripts/measure-answers.ps1` both
    /// hash. That script is this algorithm's other implementation; it hashes the exported JSON,
    /// which is why the times go through the JSON writer's own rounding here — a tick-exact
    /// rendering disagreed with it on any boundary off the millisecond grid — and the two are
    /// held together by a shared vector in the suite, so a change to either fails a test rather
    /// than quietly unpinning every labelled set.
    /// </summary>
    public string SegmentsSha256()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var segment in Segments)
        {
            builder.Append(Formatting.JsonTranscriptFormatter.FormatSeconds(segment.Start)).Append('\n');
            builder.Append(Formatting.JsonTranscriptFormatter.FormatSeconds(segment.End)).Append('\n');
            builder.Append(segment.Text).Append('\n');
        }

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
