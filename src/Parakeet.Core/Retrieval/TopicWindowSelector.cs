namespace Parakeet.Core.Retrieval;

/// <summary>
/// Retrieves a topic's surrounding discussion, keeping the original cover windows as citations.
/// A speaker often names the subject once and then explains it using pronouns; ranking isolated
/// minutes alone drops that explanation in favour of unrelated mentions elsewhere.
/// </summary>
public static class TopicWindowSelector
{
    private const int PassageSeconds = 300;
    private const int BoundarySeconds = 60;

    // Question scaffolding, not a general stopword list: words such as "remake", "cost" and
    // "opinion" remain searchable. Other languages are passed through unchanged. Falling back
    // when nothing remains preserves literal questions about these words themselves.
    private static readonly HashSet<string> QuestionWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "about", "of", "on", "in", "at", "to", "for", "and", "or",
        "what", "which", "who", "whom", "when", "where", "why", "how",
        "do", "does", "did", "is", "are", "was", "were", "be", "been",
        "have", "has", "had", "can", "could", "would", "should",
        "i", "me", "my", "you", "your", "we", "our", "they", "their", "them",
        "he", "his", "him", "she", "her", "it", "its", "this", "that", "these", "those",
        "say", "says", "said", "saying", "talk", "talks", "talked", "talking",
        "discuss", "discusses", "discussed", "discussing", "mention", "mentioned",
        "think", "thinks", "thought", "thinking", "feel", "felt", "tell", "tells", "told",
        "explain", "please", "video", "recording", "transcript", "hosts", "speakers",
        "s", "t", "ve", "re", "ll", "d", "m",
    };

    /// <summary>
    /// Ranks five-minute passages by query-term coverage, then BM25. Only passages with the best
    /// coverage are expanded: a passage matching both a title and "remake" takes precedence over
    /// a different game's remake. One neighbouring minute on each side preserves boundary turns.
    /// The character allowance includes citation brackets and separators, as sent to the model.
    /// No window is truncated. If matching passages exist but none of their anchors fits, an
    /// explicit budget failure prevents the caller mistaking that for absent source evidence.
    /// </summary>
    /// <param name="cover">Non-overlapping windows, normally built with TranscriptWindowOptions.Cover.</param>
    /// <param name="question">The question as typed, in any language.</param>
    /// <param name="budgetChars">Hard upper bound on evidence characters, including citation overhead.</param>
    /// <param name="maxWindows">Hard upper bound on the number of fine citation windows returned.</param>
    public static IReadOnlyList<TranscriptWindow> Select(
        IReadOnlyList<TranscriptWindow> cover, string question, int budgetChars, int maxWindows = 12)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetChars);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWindows, 1);

        var allTerms = SearchTokenizer.Tokenize(question).Distinct(StringComparer.Ordinal).ToArray();
        var terms = allTerms.Where(term => !QuestionWords.Contains(term)).ToArray();
        if (terms.Length == 0)
        {
            terms = allTerms;
        }

        if (cover.Count == 0 || terms.Length == 0)
        {
            return [];
        }

        var windows = cover
            .DistinctBy(window => (window.FirstSegment, window.LastSegment))
            .OrderBy(window => window.Start)
            .ThenBy(window => window.FirstSegment)
            .ToArray();
        var query = string.Join(' ', terms);
        var fineHits = new Bm25Retriever(windows).Retrieve(query, windows.Length);
        var fineScores = fineHits.ToDictionary(hit => hit.Window.CitationId, hit => hit.Score);
        var termSets = windows.Select(window => SearchTokenizer.Tokenize(window.Text)
            .ToHashSet(StringComparer.Ordinal)).ToArray();

        var passages = windows.Select((window, index) => (window, index))
            .GroupBy(item => (long)Math.Floor(item.window.Start.TotalSeconds / PassageSeconds))
            .Select(group => new Passage(group.Select(item => item.index).ToArray(), windows))
            .ToArray();
        var passageHits = new Bm25Retriever(passages.Select(passage => passage.Window).ToArray())
            .Retrieve(query, passages.Length);
        var passageScores = passageHits.ToDictionary(hit => hit.Window.CitationId, hit => hit.Score);
        var ranked = passages.Select(passage => new
            {
                Passage = passage,
                Coverage = terms.Count(term => passage.Indices.Any(index => termSets[index].Contains(term))),
                Score = passageScores.GetValueOrDefault(passage.Window.CitationId),
            })
            .Where(item => item.Coverage > 0)
            .OrderByDescending(item => item.Coverage)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.Passage.Window.Start)
            .ToArray();

        if (ranked.Length == 0)
        {
            return [];
        }

        var selected = new HashSet<int>();
        var remaining = budgetChars;
        foreach (var item in ranked.Where(item => item.Coverage == ranked[0].Coverage))
        {
            // Start at actual matching evidence, so a tight allowance never spends itself on
            // context while dropping the line that made this passage relevant.
            var anchor = item.Passage.Indices
                .OrderByDescending(index => terms.Count(term => termSets[index].Contains(term)))
                .ThenByDescending(index => fineScores.GetValueOrDefault(windows[index].CitationId))
                .ThenBy(index => index)
                .First();
            var first = item.Passage.Indices[0];
            var last = item.Passage.Indices[^1];
            if (first > 0 && windows[first].Start - windows[first - 1].End <= TimeSpan.FromSeconds(BoundarySeconds))
            {
                first--;
            }

            if (last + 1 < windows.Length
                && windows[last + 1].Start - windows[last].End <= TimeSpan.FromSeconds(BoundarySeconds))
            {
                last++;
            }

            if (!TryAdd(anchor))
            {
                continue;
            }

            // Grow both sides without jumping a window that did not fit. A tight budget can
            // shorten a passage, but cannot turn it into disconnected snippets presented as
            // contiguous context. The closest temporal neighbour goes first, earlier on ties.
            var left = anchor - 1;
            var right = anchor + 1;
            while ((left >= first || right <= last) && selected.Count < maxWindows)
            {
                var goLeft = left >= first && (right > last
                    || windows[anchor].Start - windows[left].End <= windows[right].Start - windows[anchor].End);
                if (goLeft)
                {
                    if (windows[left + 1].Start - windows[left].End > TimeSpan.FromSeconds(BoundarySeconds)
                        || !TryAdd(left))
                    {
                        left = first - 1;
                    }
                    else
                    {
                        left--;
                    }
                }
                else if (windows[right].Start - windows[right - 1].End > TimeSpan.FromSeconds(BoundarySeconds)
                    || !TryAdd(right))
                {
                    right = last + 1;
                }
                else
                {
                    right++;
                }
            }

            if (selected.Count == maxWindows)
            {
                break;
            }
        }

        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                "Matching transcript evidence exceeds the answer's character budget. "
                + "Split the oversized transcript window or read the whole transcript.");
        }

        return selected.Order().Select(index => windows[index]).ToArray();

        bool TryAdd(int index)
        {
            if (selected.Contains(index))
            {
                return true;
            }

            var cost = windows[index].Text.Length + windows[index].CitationId.Length + 4;
            if (selected.Count >= maxWindows || cost > remaining)
            {
                return false;
            }

            selected.Add(index);
            remaining -= cost;
            return true;
        }
    }

    private sealed class Passage
    {
        public Passage(int[] indices, IReadOnlyList<TranscriptWindow> windows)
        {
            Indices = indices;
            var first = windows[indices[0]];
            var last = windows[indices[^1]];
            Window = first with
            {
                LastSegment = last.LastSegment,
                End = last.End,
                Text = string.Join(' ', indices.Select(index => windows[index].Text)),
            };
        }

        public int[] Indices { get; }

        public TranscriptWindow Window { get; }
    }
}
