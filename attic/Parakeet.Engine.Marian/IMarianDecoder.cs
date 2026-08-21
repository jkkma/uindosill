namespace Parakeet.Engine.Marian;

/// <summary>
/// What the beam search needs from a model: one source encoded, then a step at a time over a fixed
/// number of beams, with the ability to reorder its own cache when the beams are reshuffled.
/// </summary>
/// <remarks>
/// <para>
/// An interface rather than the ONNX class directly, and the reason is testability rather than
/// taste. The search is where this feature's real risk lives — length penalty, tie-breaking, when a
/// finished beam displaces another, when the loop is allowed to stop — and every one of those can
/// be exercised exactly, with scripted logits, on a machine with no weights. A search that can only
/// be run against 1.34 GiB of graphs is a search nobody checks until it is already wrong.
/// </para>
/// <para>
/// The cache is the decoder's, not the search's. The search says which beam each new beam came
/// from and the decoder does whatever that means for its own state — which for the merged ONNX
/// decoder is permuting six layers of past keys and values, and for a test stub is nothing at all.
/// </para>
/// </remarks>
internal interface IMarianDecoder : IDisposable
{
    /// <summary>The width of one step's logits, per beam.</summary>
    int VocabularySize { get; }

    /// <summary>
    /// Encodes one source and prepares <paramref name="beams"/> identical decoder states from it.
    /// </summary>
    void Begin(IReadOnlyList<int> sourceIds, int beams);

    /// <summary>
    /// Advances every beam by one token and returns the next-token logits, <c>beams × vocabulary</c>
    /// row-major.
    /// </summary>
    /// <remarks>
    /// The span is the decoder's own buffer and is valid until the next call — the search reads it
    /// once, into scores, and never holds it.
    /// </remarks>
    ReadOnlySpan<float> Step(ReadOnlySpan<long> tokens);

    /// <summary>
    /// Rebuilds the per-beam state so that beam <c>i</c> continues from what was beam
    /// <c>order[i]</c>.
    /// </summary>
    void Reorder(ReadOnlySpan<int> order);
}
