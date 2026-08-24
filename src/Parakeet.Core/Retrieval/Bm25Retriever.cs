namespace Parakeet.Core.Retrieval;

/// <summary>
/// BM25 over transcript windows, hand-rolled so <c>Parakeet.Core</c> stays dependency-free.
/// About two hundred lines against a perpetual-beta package was the register's decision 3
/// arithmetic, and it holds: no model, no bytes to download, and the one part of v2 that is
/// testable with no language model in the room.
/// </summary>
/// <remarks>
/// The scoring function is Robertson &amp; Zaragoza's, with Lucene's idf:
/// <c>idf(t) = ln(1 + (N − n + 0.5) / (n + 0.5))</c>, which is never negative where the
/// classical form goes below zero for terms in more than half the windows — a transcript's
/// commonest words would otherwise subtract relevance. Defaults k1 = 1.2, b = 0.75, the
/// reference values everywhere BM25 is written down. Query terms are counted once each: the
/// questions this serves are a dozen words long, and a repeated word in one is emphasis, not
/// evidence.
/// </remarks>
public sealed class Bm25Retriever : IRetriever
{
    private readonly IReadOnlyList<TranscriptWindow> _windows;
    private readonly Dictionary<string, List<Posting>> _postings;
    private readonly double[] _lengths;
    private readonly double _averageLength;
    private readonly double _k1;
    private readonly double _b;

    public Bm25Retriever(IReadOnlyList<TranscriptWindow> windows, double k1 = 1.2, double b = 0.75)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentOutOfRangeException.ThrowIfNegative(k1);
        if (b is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(b), b, "b interpolates length normalisation and lives in [0, 1].");
        }

        _windows = windows;
        _k1 = k1;
        _b = b;
        _postings = new Dictionary<string, List<Posting>>(StringComparer.Ordinal);
        _lengths = new double[windows.Count];

        for (var i = 0; i < windows.Count; i++)
        {
            var tokens = SearchTokenizer.Tokenize(windows[i].Text);
            _lengths[i] = tokens.Count;

            var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var token in tokens)
            {
                frequencies[token] = frequencies.TryGetValue(token, out var n) ? n + 1 : 1;
            }

            foreach (var (term, frequency) in frequencies)
            {
                if (!_postings.TryGetValue(term, out var list))
                {
                    list = [];
                    _postings[term] = list;
                }

                list.Add(new Posting(i, frequency));
            }
        }

        _averageLength = windows.Count == 0 ? 0 : _lengths.Average();
    }

    public IReadOnlyList<RetrievalHit> Retrieve(string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        if (_windows.Count == 0)
        {
            return [];
        }

        var scores = new double[_windows.Count];
        foreach (var term in SearchTokenizer.Tokenize(query).Distinct(StringComparer.Ordinal))
        {
            if (!_postings.TryGetValue(term, out var postings))
            {
                continue;
            }

            var idf = Math.Log(1 + ((_windows.Count - postings.Count + 0.5) / (postings.Count + 0.5)));
            foreach (var (window, frequency) in postings)
            {
                var normalisedLength = 1 - _b + (_b * _lengths[window] / _averageLength);
                scores[window] += idf * frequency * (_k1 + 1) / (frequency + (_k1 * normalisedLength));
            }
        }

        var hits = new List<RetrievalHit>();
        for (var i = 0; i < scores.Length; i++)
        {
            if (scores[i] > 0)
            {
                hits.Add(new RetrievalHit { Window = _windows[i], Score = scores[i] });
            }
        }

        // OrderByDescending is stable, so equal scores keep transcript order and a tie between
        // two windows never reorders between runs.
        return [.. hits.OrderByDescending(h => h.Score).Take(limit)];
    }

    private readonly record struct Posting(int Window, int Frequency);
}
