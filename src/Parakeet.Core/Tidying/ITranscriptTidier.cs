using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tidying;

/// <summary>
/// What a tidying model can say about itself before it is asked anything — the same provenance
/// discipline as every other engine here, because a tidied transcript names the model that
/// edited it and the backend it ran on.
/// </summary>
public sealed record TidierCapabilities
{
    public required string EngineName { get; init; }

    public required string ModelId { get; init; }

    public ComputeBackend Backend { get; init; }

    public string? Quantisation { get; init; }
}

/// <summary>What a caller may ask of the tidy.</summary>
public sealed record TidyOptions
{
    public static TidyOptions Default { get; } = new();

    /// <summary>
    /// The one door in the delete-only contract: a spoken word whose confidence is below this may
    /// be replaced by the model's word; a word the recogniser was sure of never is. The default is
    /// the threshold the low-confidence report already flags segments by
    /// (<see cref="TranscriptionOptions.LowConfidenceThreshold"/>), which is what the decision of
    /// 2026-09-01 named. Zero shuts the door: no substitution is ever accepted.
    /// </summary>
    public float LowConfidenceThreshold { get; init; } = 0.45f;

    /// <summary>
    /// How many lines are in flight at once. Four is what was measured: the server's own default
    /// slot count, and 1.6x the sequential pass on the second machine's Vulkan path — not 4x,
    /// because the adapter is already busy with one sequence (docs/UNPROVEN.md, *Gemma 4 E4B as
    /// a transcript tidy*).
    /// </summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>
    /// What one request carries. The joined run — whole lines to fifteen seconds of speech — is
    /// the shipped unit since the decision of 2026-09-03 (docs/PHASES.md, *Decided 2026-09-03*):
    /// measured on both machines against the segment and the sentence-run, it landed the tidied
    /// copy soonest (11.9 s after the plain transcript on the desktop's CUDA path, 83 s on the
    /// laptop's Vulkan path, against the segment's 38 s and 194 s), tidied better under both
    /// references, and under the refusal clause as decided — refused requests, not the lines they
    /// carry — qualified on both. The segment was the shipped unit until then; the sentence-run
    /// stays behind the measurement's options. Nothing in the window asks for either.
    /// </summary>
    public TidyUnitKind Unit { get; init; } = TidyUnitKind.JoinedRun;

    /// <summary>
    /// Beside the recogniser, the shipped shape, or after it. The pass exists for the same
    /// measurement, as the arm the tandem shape is compared against.
    /// </summary>
    public TidyShape Shape { get; init; } = TidyShape.Tandem;

    /// <summary>
    /// The most of a line's spoken words a rewrite may drop, as a fraction of the words the
    /// normaliser can see. The contract admits deletions without bound, which is what the
    /// delete-only shape is for; on real audio that let the model take out whole clauses and
    /// whole lines, so the ceiling bounds how much of a line may go, not whether any of it may.
    /// Half is what separated clause removal from stutter removal on the first call it was
    /// measured against; the two ceilings are read together and either one refuses.
    /// </summary>
    public double MaxDeletedFraction { get; init; } = DefaultMaxDeletedFraction;

    /// <summary>
    /// The most spoken words in a row a rewrite may drop. A stutter is scattered and a clause is
    /// contiguous, which is what this reads and <see cref="MaxDeletedFraction"/> cannot: on the
    /// call this was measured against, every clause the model removed ran to five words or more
    /// and every legitimate cleanup to four or fewer. Fillers are transparent — one between two
    /// dropped words neither breaks the run nor extends it — because they are the words the
    /// normaliser cannot see.
    /// </summary>
    public int MaxConsecutiveDeletedWords { get; init; } = DefaultMaxConsecutiveDeletedWords;

    /// <summary>The default <see cref="MaxDeletedFraction"/>, a constant so the contract can take it as a parameter default.</summary>
    public const double DefaultMaxDeletedFraction = 0.5;

    /// <summary>The default <see cref="MaxConsecutiveDeletedWords"/>, a constant for the same reason.</summary>
    public const int DefaultMaxConsecutiveDeletedWords = 4;

    public void Validate()
    {
        if (!Enum.IsDefined(Unit))
        {
            throw new ArgumentOutOfRangeException(nameof(Unit), Unit, "Unknown unit kind.");
        }

        if (!Enum.IsDefined(Shape))
        {
            throw new ArgumentOutOfRangeException(nameof(Shape), Shape, "Unknown shape.");
        }

        if (LowConfidenceThreshold is < 0f or > 1f || float.IsNaN(LowConfidenceThreshold))
        {
            throw new ArgumentOutOfRangeException(
                nameof(LowConfidenceThreshold), LowConfidenceThreshold, "Confidence threshold must be within [0, 1].");
        }

        if (Concurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Concurrency), Concurrency, "At least one line must be in flight.");
        }

        if (MaxDeletedFraction is <= 0d or > 1d || double.IsNaN(MaxDeletedFraction))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDeletedFraction), MaxDeletedFraction, "The deletion ceiling must be within (0, 1]: a line may not be forbidden every deletion.");
        }

        if (MaxConsecutiveDeletedWords < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConsecutiveDeletedWords), MaxConsecutiveDeletedWords, "At least one word in a row must be droppable.");
        }
    }
}

/// <summary>
/// Rewrites one line of a transcript. The one abstraction the rest of the application knows
/// about for tidying: no detail of the model, the server or the prompt may leak through it, for
/// the reason <see cref="Translation.ITranscriptTranslator"/> hides its checkpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes a line and returns a line, and nothing it returns is trusted.</b> The model's
/// rewrite is a candidate; <see cref="TidyContract"/> is what decides whether the transcript
/// takes it, and a tidier has no way to bypass that. That is the whole of the design: the
/// measured risk of a rewrite is its rare substitutions (one per ~300 words, several of them
/// wrong in ways a reader cannot see), and the contract refuses every one of them but the door.
/// </para>
/// <para>
/// <b>One line per call, concurrently.</b> The stage keeps several calls in flight at once, so an
/// implementation must tolerate that — the server behind the shipping one runs four slots.
/// </para>
/// </remarks>
public interface ITranscriptTidier : IAsyncDisposable
{
    TidierCapabilities Capabilities { get; }

    /// <summary>Loads the model. Idempotent, expensive, never on a UI thread.</summary>
    ValueTask LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// The model's rewrite of <paramref name="line"/>: the same words with fillers, false starts
    /// and stutters taken out and the punctuation and casing repaired, as far as the model
    /// obeyed. What it returns is checked by the caller, never taken on trust.
    /// </summary>
    Task<string> TidyLineAsync(string line, CancellationToken ct = default);
}
