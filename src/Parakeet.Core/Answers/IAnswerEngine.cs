using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Answers;

/// <summary>
/// What a language-model engine can say about itself before anything is asked — the same
/// provenance discipline as <see cref="EngineCapabilities"/>, because every answer's provenance
/// line is filled from here.
/// </summary>
public sealed record AnswerEngineCapabilities
{
    public required string EngineName { get; init; }

    public required string ModelId { get; init; }

    public ComputeBackend Backend { get; init; }

    public string? Quantisation { get; init; }

    /// <summary>
    /// Whether decoding can be constrained to a grammar over the live segment ids. Without it
    /// the citation rule still holds — parse and resolve — but enforcement is post-hoc.
    /// </summary>
    public bool SupportsGrammar { get; init; }

    /// <summary>
    /// The model's trained context length, in tokens, when known. The measured lesson behind
    /// carrying it: raising a context flag does not raise this ceiling, and a transcript larger
    /// than it does not fit in one pass no matter what the flag says.
    /// </summary>
    public int? TrainedContextTokens { get; init; }
}

/// <summary>One question against one transcript, with everything the engine may see.</summary>
public sealed record AskRequest
{
    public required string Question { get; init; }

    /// <summary>
    /// The transcript the ids in the answer are meaningful against — the ask's entire world:
    /// windows, grammar, quote checks and validation all run over this one document. On a
    /// translated recording this is the translated document, whole (the maintainer's decision,
    /// 2026-08-24: the model sees the English pane), whose sentence-cut segment array is its own
    /// and not the original's — `docs/V2-ASK-THE-TRANSCRIPT.md` records what follows from that.
    /// </summary>
    public required TranscriptDocument Transcript { get; init; }

    public AnswerMode Mode { get; init; } = AnswerMode.Retrieval;

    /// <summary>
    /// What the model sees, in every mode: retrieval passes the windows it chose in rank order,
    /// the whole-transcript path passes windows covering the whole recording, and the id set a
    /// grammar enumerates is exactly these windows' ids either way. Empty evidence in retrieval
    /// mode is the abstain path — the model is never asked to answer from nothing.
    /// </summary>
    public IReadOnlyList<TranscriptWindow> Evidence { get; init; } = [];

    /// <summary>
    /// BCP-47 tag the answer should be written in, when known. Filled from the transcript's
    /// language hint and from nowhere else — the maintainer's decision, 2026-08-24:
    /// <c>TranscriptDocument.Language</c> is the request hint or null, nothing detects one, and
    /// null here means the prompt makes no claim about the answer's language.
    /// </summary>
    public string? Language { get; init; }
}

/// <summary>
/// Progress of one answer. Prefill is the wait that matters — 467.9 s measured for a full
/// three-hour transcript on the laptop's Vulkan path — and a panel that cannot show it is a
/// panel that looks hung.
/// </summary>
public sealed record AskProgress
{
    /// <summary>Prompt tokens processed so far.</summary>
    public int PrefillTokens { get; init; }

    /// <summary>Prompt tokens in total, when the engine knows before finishing.</summary>
    public int? PrefillTotalTokens { get; init; }

    /// <summary>
    /// Answer tokens produced so far; zero until prefill completes. Approximate by contract:
    /// the server engine counts streamed content chunks, which is one per token on the server
    /// it drives in practice but is not a promise of the protocol — a consumer may treat this
    /// as progress, never as a token count to quote.
    /// </summary>
    public int GeneratedTokens { get; init; }

    /// <summary>
    /// Thinking tokens produced so far, when the engine lets the model think before answering
    /// (the maintainer's 2026-08-24 decision) — the stretch where the answer stream is silent
    /// but the model is working, which a panel showing nothing would render as a hang.
    /// Approximate by the same contract as <see cref="GeneratedTokens"/>.
    /// </summary>
    public int ThinkingTokens { get; init; }

    /// <summary>Prefill completion in [0, 1] when the total is known, otherwise null.</summary>
    public double? PrefillFraction =>
        PrefillTotalTokens is > 0
            ? Math.Clamp(PrefillTokens / (double)PrefillTotalTokens.Value, 0d, 1d)
            : null;
}

/// <summary>
/// A question-answering engine over a finished transcript. The one abstraction the rest of the
/// app is allowed to know about, exactly as <see cref="ITranscriptionEngine"/> is for speech:
/// no llama-server, no process, no HTTP may leak through this interface.
/// </summary>
/// <remarks>
/// The stream is raw model text, not parsed structure. <see cref="AnswerParser"/> is the single
/// place structure comes from — an engine that returned bullets would be a second parser with a
/// process attached — and a caller renders the stream as provisional until the parse and
/// <see cref="CitationValidator"/> have said what actually resolved.
/// </remarks>
public interface IAnswerEngine : IAsyncDisposable
{
    AnswerEngineCapabilities Capabilities { get; }

    /// <summary>
    /// Loads the model. Idempotent, expensive — a ~9 GB file is seconds to tens of seconds —
    /// and never called on a UI thread. Under the residency policy the caller unloads the
    /// transcription model before calling this; the engine does not do that itself.
    /// </summary>
    ValueTask LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Asks one question, yielding the model's output as it decodes so a caller can render a
    /// long answer incrementally. The chunks concatenate to exactly the text
    /// <see cref="AnswerParser.Parse"/> is given. The token is the only timeout: an engine
    /// streams for as long as an answer takes, so a caller that passes none has asked to wait
    /// forever — the app always passes one.
    /// </summary>
    IAsyncEnumerable<string> AskAsync(
        AskRequest request,
        IProgress<AskProgress>? progress = null,
        CancellationToken ct = default);
}
