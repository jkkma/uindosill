using Parakeet.Core.Transcription;

namespace Parakeet.Core.Answers;

/// <summary>How much of the recording the answer could have seen — provenance, not tuning.</summary>
public enum AnswerMode
{
    /// <summary>Retrieved windows only: the citations are the windows retrieval handed back.</summary>
    Retrieval = 0,

    /// <summary>The whole transcript in one pass.</summary>
    WholeTranscript = 1,

    /// <summary>Windowed map steps merged by a reduce, ids carried through.</summary>
    MapReduce = 2,
}

/// <summary>One claim: its text, an optional label and verbatim quote, and the citations behind it.</summary>
public sealed record AnswerBullet
{
    /// <summary>The short topic label before the colon, when the model wrote one.</summary>
    public string? Label { get; init; }

    public required string Text { get; init; }

    /// <summary>
    /// The verbatim span the model claims to be quoting, when the prompt required one. Checked
    /// against the cited span, never trusted: FullCite measured roughly 40 % of forced verbatim
    /// snippets from an 8B model failing to match their source.
    /// </summary>
    public string? Quote { get; init; }

    public IReadOnlyList<Citation> Citations { get; init; } = [];

    /// <summary>No citation, or only the admitted <c>[?]</c> marker — rendered as uncited either way.</summary>
    public bool IsUncited => Citations.Count == 0 || Citations.All(c => c.IsUncitedMarker);
}

/// <summary>
/// A parsed answer plus the provenance a reader needs to judge it, mirroring
/// <see cref="TranscriptDocument"/>'s discipline: which model generated it, at which
/// quantisation, on which backend, seeing how much of the recording. An answer that cannot say
/// what produced it is not something anyone can act on — and unlike a transcript it reads
/// fluently when wrong, which is why the provenance is not optional decoration.
/// </summary>
public sealed record AnswerDocument
{
    public required IReadOnlyList<AnswerBullet> Bullets { get; init; }

    /// <summary>The model said the recording does not answer this, and said so explicitly.</summary>
    public bool Abstained { get; init; }

    public string? ModelId { get; init; }

    public string? Quantisation { get; init; }

    public ComputeBackend? Backend { get; init; }

    public AnswerMode? Mode { get; init; }

    /// <summary>BCP-47 tag of the language the answer was asked for, when known.</summary>
    public string? Language { get; init; }

    /// <summary>Neither claims nor an abstention: the model produced nothing usable.</summary>
    public bool IsEmpty => !Abstained && Bullets.Count == 0;
}
