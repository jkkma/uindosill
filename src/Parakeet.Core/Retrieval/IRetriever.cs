namespace Parakeet.Core.Retrieval;

/// <summary>A window that matched a query, with the score that ranked it.</summary>
public sealed record RetrievalHit
{
    public required TranscriptWindow Window { get; init; }

    /// <summary>
    /// The retriever's own scale — comparable within one result list, meaningless across
    /// retrievers or queries. Nobody quotes this as a quality figure.
    /// </summary>
    public required double Score { get; init; }
}

/// <summary>
/// Finds the windows a question should be answered from. One implementation in v2.0 —
/// <see cref="Bm25Retriever"/> — with the interface here so that a dense retriever, if recall
/// measurements ever demand one, slots in without touching a caller.
/// </summary>
public interface IRetriever
{
    /// <summary>
    /// The top <paramref name="limit"/> windows for <paramref name="query"/>, best first, empty
    /// when nothing matches at all. An empty result is a real answer — it is what the abstain
    /// path is made of — never an error.
    /// </summary>
    IReadOnlyList<RetrievalHit> Retrieve(string query, int limit);
}
