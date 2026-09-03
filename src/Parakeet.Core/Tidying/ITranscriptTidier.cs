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
    /// What one request carries. The segment is the shipped shape; the other two exist for the
    /// measurement that decides which ships (docs/PHASES.md, *Decided 2026-09-02, late evening*),
    /// and nothing in the window asks for them.
    /// </summary>
    public TidyUnitKind Unit { get; init; } = TidyUnitKind.Segment;

    /// <summary>
    /// Beside the recogniser, the shipped shape, or after it. The pass exists for the same
    /// measurement, as the arm the tandem shape is compared against.
    /// </summary>
    public TidyShape Shape { get; init; } = TidyShape.Tandem;

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
