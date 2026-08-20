namespace Parakeet.Engine.Marian.Tests;

/// <summary>
/// A decoder whose logits are written by the test rather than by a model.
/// </summary>
/// <remarks>
/// <para>
/// The beam search is where this feature's real risk lives, and almost none of that risk is
/// reachable with a real checkpoint: a model that is any good never puts a banned token first, and
/// the case where a short hypothesis beats a long one by a hair is not something you can ask 1.34
/// GiB of weights to produce on demand. Scripting the distribution makes every one of those a
/// three-line test — and makes them run on a machine with no weights, which is every machine CI
/// has.
/// </para>
/// <para>
/// It keeps a running sequence per beam, so the script is a function of what a beam has said so far
/// rather than of the step number. That is what lets a test express "this token looks good now and
/// leads nowhere", which is the whole reason beam search exists.
/// </para>
/// </remarks>
internal sealed class ScriptedDecoder(int vocabularySize, Func<IReadOnlyList<int>, float[]> script) : IMarianDecoder
{
    private readonly List<List<int>> _beams = [];

    public int VocabularySize { get; } = vocabularySize;

    /// <summary>Every <see cref="Reorder"/> the search asked for, in order.</summary>
    public List<int[]> Reorders { get; } = [];

    /// <summary>How many times the model was run.</summary>
    public int Steps { get; private set; }

    /// <summary>The source the search was started on.</summary>
    public IReadOnlyList<int> Source { get; private set; } = [];

    public void Begin(IReadOnlyList<int> sourceIds, int beams)
    {
        Source = sourceIds;
        _beams.Clear();
        for (var beam = 0; beam < beams; beam++)
        {
            _beams.Add([]);
        }
    }

    public ReadOnlySpan<float> Step(ReadOnlySpan<long> tokens)
    {
        Steps++;

        var logits = new float[_beams.Count * VocabularySize];
        for (var beam = 0; beam < _beams.Count; beam++)
        {
            // The token handed in is the last one of this beam's running sequence, so appending it
            // here keeps the script's view and the search's view of the beam identical.
            _beams[beam].Add((int)tokens[beam]);
            script(_beams[beam]).CopyTo(logits, beam * VocabularySize);
        }

        return logits;
    }

    public void Reorder(ReadOnlySpan<int> order)
    {
        Reorders.Add(order.ToArray());

        var rebuilt = new List<List<int>>(order.Length);
        for (var beam = 0; beam < order.Length; beam++)
        {
            rebuilt.Add([.. _beams[order[beam]]]);
        }

        _beams.Clear();
        _beams.AddRange(rebuilt);
    }

    public void Dispose()
    {
    }
}
