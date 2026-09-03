using Parakeet.Core.Text;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tidying;

/// <summary>One word the tidy replaced through the low-confidence door.</summary>
/// <param name="SpokenWordIndex">Index into the spoken segment's words.</param>
/// <param name="Spoken">The word the recogniser wrote.</param>
/// <param name="Replacement">The word the model put in its place.</param>
/// <param name="Confidence">The recogniser's confidence in the spoken word — below the threshold, or it would have been refused.</param>
public sealed record TidyReplacement(int SpokenWordIndex, string Spoken, string Replacement, float Confidence);

/// <summary>What the contract made of one candidate rewrite.</summary>
public sealed record TidyOutcome
{
    /// <summary>True when the rewrite was taken; false when the spoken line was kept.</summary>
    public required bool Accepted { get; init; }

    /// <summary>The tidied segment when accepted, the spoken one unchanged when not.</summary>
    public required TranscriptSegment Segment { get; init; }

    /// <summary>Spoken words the rewrite dropped — counted only among words the normaliser can see, so a removed filler is not one.</summary>
    public int DeletedWords { get; init; }

    /// <summary>The words that went through the door, in order. Empty on a refused line.</summary>
    public IReadOnlyList<TidyReplacement> Replacements { get; init; } = [];

    /// <summary>Why the spoken line was kept, or null when the rewrite was accepted.</summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// What the contract made of one piece of a unit: the tidied words that fell on that piece's
/// range, or the refusal the whole unit met. The stage assembles a segment's outcome from its
/// pieces' once every one has landed.
/// </summary>
public sealed record TidyPieceOutcome
{
    public required TidyPiece Piece { get; init; }

    public required bool Accepted { get; init; }

    /// <summary>The tidied words on this piece, in order; empty when the piece tidied to nothing, and when its segment carried no timings.</summary>
    public IReadOnlyList<TranscriptWord> Words { get; init; } = [];

    /// <summary>The tidied text of this piece: its words joined by single spaces.</summary>
    public string Text { get; init; } = string.Empty;

    public int DeletedWords { get; init; }

    /// <summary>Replacements on this piece, indexed into the piece's own segment's words.</summary>
    public IReadOnlyList<TidyReplacement> Replacements { get; init; } = [];

    public string? Refusal { get; init; }
}

/// <summary>
/// The delete-only contract every tidied line is held to, decided 2026-09-01 (docs/PHASES.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>A tidied line is accepted only when its words are a subsequence of the spoken line's</b>,
/// under the normalisation the WER harness scores by — <see cref="TranscriptNormalizer.WordErrorRateTokens"/>:
/// lower-cased, non-alphanumerics stripped, the six filler tokens dropped. <see cref="WordAlignment"/>
/// produces exactly that alignment, and a line is in contract when its operations are matches and
/// deletions only. Punctuation and casing changes on kept words pass because the normaliser does
/// not see them. Any other line keeps the spoken text, and the outcome says why.
/// </para>
/// <para>
/// <b>The one exception is the recogniser's own doubt.</b> A substitution is accepted where the
/// spoken word's confidence is below the threshold, so a fragment the recogniser was unsure of
/// may be replaced and a word it was sure of never is. The replacement keeps the spoken word's
/// span and records what it replaced (<see cref="TranscriptWord.ReplacedFrom"/>). What the door
/// admits is unmeasured — docs/UNPROVEN.md — and the contract without it is the fallback.
/// </para>
/// <para>
/// <b>Every kept word maps to the timed word it came from.</b> The normalisation runs word by
/// word on both sides, so each normalised token knows which raw word it belongs to, and the
/// alignment over the tokens becomes a mapping from the rewrite's words to the spoken ones. A
/// rewrite word that spans two spoken words — a hyphenation the normaliser splits — takes the
/// span from the first to the last; a rewrite word the normaliser cannot see at all (a stray dash,
/// a filler the model kept) borrows the timing of the word beside it. So the tidied pane keeps the
/// spoken-word highlight and the word-timed formats stay writable, and the rule that the pass
/// writes words and never times holds: no time in the result is one the recogniser did not report.
/// Where the spoken segment has no verified word timings, the tidied one has none either.
/// </para>
/// <para>
/// <b>A unit of several pieces is judged as one line and cut back by the same mapping.</b> Each
/// kept word belongs to the piece its spoken word came from; a word that borrows a neighbour's
/// time goes with that neighbour. A rewrite word whose spoken words lie on two pieces — a
/// hyphenation across the join — refuses the unit, and a refused unit refuses every piece in it,
/// because a line kept as spoken beside a line tidied is what the shape decided on
/// 2026-09-02 asked for rather than a partial tidy nobody measured.
/// </para>
/// <para>
/// Word by word rather than over the whole line is a deliberate narrowing of the harness's
/// normaliser: its one cross-word rule, joining a run of number words into digits, cannot apply,
/// so <i>eighty seven</i> normalises to two tokens here where the harness scores one. Both sides
/// are treated alike, so the only effect is that a rewrite joining or splitting number words is
/// refused — which is the conservative direction.
/// </para>
/// <para>
/// <b>One guard beyond the contract:</b> a rewrite that comes back empty for a line that held
/// content words is refused, although an empty line is a subsequence of anything. The measurement
/// saw that only for lines that were nothing but <i>um</i> or <i>uh</i>, and there it is right;
/// on a line with words it is the model giving up, and the spoken line is the better answer.
/// </para>
/// </remarks>
public static class TidyContract
{
    /// <summary>
    /// Holds <paramref name="candidate"/> to the contract against <paramref name="spoken"/> and
    /// returns what the transcript should carry.
    /// </summary>
    public static TidyOutcome Apply(TranscriptSegment spoken, string candidate, float lowConfidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(spoken);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateThreshold(lowConfidenceThreshold);

        // The timed words when they spell the text, the text's own words otherwise. The second
        // case is a segment whose words the recogniser did not report or did not reproduce, and it
        // gets a tidy with no timings — the rule the English pane set stands for the tidied one.
        var timed = spoken.WordsReproduceText();
        var spokenWords = timed
            ? spoken.Words.Select(w => w.Text.Trim()).ToArray()
            : SplitWords(spoken.Text);

        var judged = Judge(spokenWords, timed ? spoken.Words : null, candidate, lowConfidenceThreshold);
        if (judged.Refusal is { } why)
        {
            return Refused(spoken, why);
        }

        // The tidied text is the rewrite's words joined by single spaces, so that the words
        // reproduce the text and the sentence splitter and the highlight can read it the way they
        // read the spoken one.
        var text = string.Join(' ', judged.CandidateWords);
        var words = timed ? MapWords(spoken.Words, judged.CandidateWords, judged.Mapped, judged.Replaced) : [];

        return new TidyOutcome
        {
            Accepted = true,
            Segment = spoken with { Text = text, Words = words },
            DeletedWords = judged.CountDeleted(0, spokenWords.Length),
            Replacements = judged.Replacements,
        };
    }

    /// <summary>
    /// Holds <paramref name="candidate"/> to the contract against the whole of
    /// <paramref name="unit"/> and cuts the result back to the unit's pieces: one outcome per
    /// piece, in order, every one refused when the unit is.
    /// </summary>
    public static IReadOnlyList<TidyPieceOutcome> Apply(TidyUnit unit, string candidate, float lowConfidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateThreshold(lowConfidenceThreshold);

        if (unit.IsSingleWholeSegment)
        {
            // The shipped shape, through the one path it always took.
            var piece = unit.Pieces[0];
            var outcome = Apply(piece.Segment, candidate, lowConfidenceThreshold);
            return
            [
                new TidyPieceOutcome
                {
                    Piece = piece,
                    Accepted = outcome.Accepted,
                    Words = outcome.Segment.Words,
                    Text = outcome.Segment.Text,
                    DeletedWords = outcome.DeletedWords,
                    Replacements = outcome.Replacements,
                    Refusal = outcome.Refusal,
                },
            ];
        }

        var composite = unit.Composite;
        var spokenWords = composite.Words.Select(w => w.Text.Trim()).ToArray();
        var judged = Judge(spokenWords, composite.Words, candidate, lowConfidenceThreshold);
        if (judged.Refusal is { } why)
        {
            return RefuseAll(unit, why);
        }

        // Which piece each spoken word of the composite belongs to, by the pieces' ranges.
        var pieces = unit.Pieces;
        var offsets = new int[pieces.Count + 1];
        for (var p = 0; p < pieces.Count; p++)
        {
            offsets[p + 1] = offsets[p] + pieces[p].WordCount;
        }

        var pieceOfSpoken = new int[spokenWords.Length];
        for (var p = 0; p < pieces.Count; p++)
        {
            for (var s = offsets[p]; s < offsets[p + 1]; s++)
            {
                pieceOfSpoken[s] = p;
            }
        }

        // Which piece each rewrite word lands on: the piece of the spoken words it maps to, all of
        // which must be on one piece; a word mapping to nothing goes with the neighbour whose time
        // it borrows, earlier first, the way MapWords lends it.
        var candidateWords = judged.CandidateWords;
        var pieceOfCandidate = new int[candidateWords.Length];
        Array.Fill(pieceOfCandidate, -1);
        for (var c = 0; c < candidateWords.Length; c++)
        {
            if (judged.Mapped[c] is not { Count: > 0 } sources)
            {
                continue;
            }

            var piece = pieceOfSpoken[sources[0]];
            foreach (var s in sources)
            {
                if (pieceOfSpoken[s] != piece)
                {
                    return RefuseAll(unit, $"the rewrite joined '{candidateWords[c]}' across a line break");
                }
            }

            pieceOfCandidate[c] = piece;
        }

        for (var c = 0; c < candidateWords.Length; c++)
        {
            if (pieceOfCandidate[c] >= 0)
            {
                continue;
            }

            var neighbour = -1;
            for (var k = c - 1; k >= 0 && neighbour < 0; k--) neighbour = pieceOfCandidate[k];
            for (var k = c + 1; k < candidateWords.Length && neighbour < 0; k++) neighbour = judged.Mapped[k] is { Count: > 0 } ? pieceOfCandidate[k] : -1;

            if (neighbour < 0)
            {
                // Nothing in the rewrite maps to anything spoken. For a unit that held words,
                // there is no line to put the rewrite on.
                if (judged.SpokenTokens > 0)
                {
                    return RefuseAll(unit, "the rewrite could not be placed on its lines");
                }

                break;
            }

            pieceOfCandidate[c] = neighbour;
        }

        var mappedWords = MapWords(composite.Words, candidateWords, judged.Mapped, judged.Replaced);
        var outcomes = new List<TidyPieceOutcome>(pieces.Count);
        for (var p = 0; p < pieces.Count; p++)
        {
            var words = new List<TranscriptWord>();
            var texts = new List<string>();
            for (var c = 0; c < candidateWords.Length; c++)
            {
                if (pieceOfCandidate[c] != p)
                {
                    continue;
                }

                texts.Add(candidateWords[c]);
                if (c < mappedWords.Count)
                {
                    words.Add(mappedWords[c]);
                }
            }

            var shift = pieces[p].WordStart - offsets[p];
            var replacements = judged.Replacements
                .Where(r => r.SpokenWordIndex >= offsets[p] && r.SpokenWordIndex < offsets[p + 1])
                .Select(r => r with { SpokenWordIndex = r.SpokenWordIndex + shift })
                .ToList();

            outcomes.Add(new TidyPieceOutcome
            {
                Piece = pieces[p],
                Accepted = true,
                Words = words,
                Text = string.Join(' ', texts),
                DeletedWords = judged.CountDeleted(offsets[p], offsets[p + 1]),
                Replacements = replacements,
            });
        }

        return outcomes;
    }

    private static void ValidateThreshold(float lowConfidenceThreshold)
    {
        if (lowConfidenceThreshold is < 0f or > 1f || float.IsNaN(lowConfidenceThreshold))
        {
            throw new ArgumentOutOfRangeException(nameof(lowConfidenceThreshold), lowConfidenceThreshold, "Confidence threshold must be within [0, 1].");
        }
    }

    /// <summary>The alignment and the door, over any line: what the two public entries share.</summary>
    private sealed class Judgement
    {
        public string? Refusal { get; init; }

        public string[] CandidateWords { get; init; } = [];

        public List<int>[] Mapped { get; init; } = [];

        public bool[] Replaced { get; init; } = [];

        public bool[] Kept { get; init; } = [];

        public int[] SpokenOwners { get; init; } = [];

        public int SpokenTokens => SpokenOwners.Length;

        public List<TidyReplacement> Replacements { get; init; } = [];

        /// <summary>Spoken words in [from, to) that were dropped and that the normaliser could see.</summary>
        public int CountDeleted(int from, int to)
        {
            var deleted = 0;
            for (var s = from; s < to; s++)
            {
                if (!Kept[s] && OwnsAToken(SpokenOwners, s))
                {
                    deleted++;
                }
            }

            return deleted;
        }
    }

    private static Judgement Judge(string[] spokenWords, IReadOnlyList<TranscriptWord>? timedWords, string candidate, float lowConfidenceThreshold)
    {
        var candidateWords = SplitWords(candidate);

        var spokenTokens = Tokenise(spokenWords, out var spokenOwners);
        var candidateTokens = Tokenise(candidateWords, out var candidateOwners);

        if (candidateTokens.Length == 0 && spokenTokens.Length > 0)
        {
            return new Judgement { Refusal = "the rewrite came back empty for a line that held words", SpokenOwners = spokenOwners };
        }

        var ops = WordAlignment.Align(spokenTokens, candidateTokens);

        // Which spoken words each rewrite word came from, and through which door.
        var mapped = new List<int>[candidateWords.Length];
        var replaced = new bool[candidateWords.Length];
        var kept = new bool[spokenWords.Length];
        var replacements = new List<TidyReplacement>();

        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case AlignmentOpKind.Match:
                {
                    var c = candidateOwners[op.HypothesisIndex];
                    var s = spokenOwners[op.ReferenceIndex];
                    (mapped[c] ??= []).Add(s);
                    kept[s] = true;
                    break;
                }

                case AlignmentOpKind.Delete:
                    break;

                case AlignmentOpKind.Insert:
                    return new Judgement
                    {
                        Refusal = $"the rewrite added '{candidateWords[candidateOwners[op.HypothesisIndex]]}', which was not spoken",
                        SpokenOwners = spokenOwners,
                    };

                case AlignmentOpKind.Substitute:
                {
                    var c = candidateOwners[op.HypothesisIndex];
                    var s = spokenOwners[op.ReferenceIndex];

                    // The door: only a timed word carries a confidence, and only one the
                    // recogniser doubted may be replaced.
                    var confidence = timedWords?[s].Confidence;
                    if (confidence is not { } doubted || doubted >= lowConfidenceThreshold)
                    {
                        return new Judgement
                        {
                            Refusal = $"the rewrite changed '{spokenWords[s]}' to '{candidateWords[c]}', and the recogniser was not in doubt about it",
                            SpokenOwners = spokenOwners,
                        };
                    }

                    (mapped[c] ??= []).Add(s);
                    kept[s] = true;
                    replaced[c] = true;
                    replacements.Add(new TidyReplacement(s, spokenWords[s], candidateWords[c], doubted));
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unknown alignment operation {op.Kind}.");
            }
        }

        return new Judgement
        {
            CandidateWords = candidateWords,
            Mapped = mapped,
            Replaced = replaced,
            Kept = kept,
            SpokenOwners = spokenOwners,
            Replacements = replacements,
        };
    }

    private static TidyOutcome Refused(TranscriptSegment spoken, string why) => new()
    {
        Accepted = false,
        Segment = spoken,
        Refusal = why,
    };

    private static List<TidyPieceOutcome> RefuseAll(TidyUnit unit, string why) =>
        unit.Pieces.Select(piece => new TidyPieceOutcome { Piece = piece, Accepted = false, Refusal = why }).ToList();

    private static string[] SplitWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The harness's normalisation applied to each word on its own, with every token knowing the
    /// word it came from. A word can be several tokens (a hyphenation) or none (a filler, a dash).
    /// </summary>
    private static string[] Tokenise(string[] words, out int[] owners)
    {
        var tokens = new List<string>(words.Length);
        var owning = new List<int>(words.Length);

        for (var i = 0; i < words.Length; i++)
        {
            foreach (var token in TranscriptNormalizer.WordErrorRateTokens(words[i], keepFillers: false))
            {
                tokens.Add(token);
                owning.Add(i);
            }
        }

        owners = [.. owning];
        return [.. tokens];
    }

    private static bool OwnsAToken(int[] owners, int word)
    {
        foreach (var owner in owners)
        {
            if (owner == word)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One timed word per rewrite word, each on the span of the spoken words it came from. A
    /// rewrite word that maps to nothing borrows the nearest mapped neighbour's span, earlier
    /// first, so that the words still reproduce the text and every one of them has a time that
    /// the recogniser reported.
    /// </summary>
    private static IReadOnlyList<TranscriptWord> MapWords(
        IReadOnlyList<TranscriptWord> spoken, string[] candidateWords, List<int>[] mapped, bool[] replaced)
    {
        var result = new TranscriptWord[candidateWords.Length];

        for (var c = 0; c < candidateWords.Length; c++)
        {
            if (mapped[c] is not { Count: > 0 } sources)
            {
                continue;
            }

            var first = spoken[sources[0]];
            var start = first.Start;
            var end = first.End;
            var confidence = first.Confidence;
            var spokenText = new List<string>(sources.Count);

            foreach (var s in sources)
            {
                var word = spoken[s];
                if (word.Start < start) start = word.Start;
                if (word.End > end) end = word.End;
                if (word.Confidence is { } wc && (confidence is not { } best || wc < best)) confidence = wc;
                spokenText.Add(word.Text.Trim());
            }

            result[c] = new TranscriptWord
            {
                Text = candidateWords[c],
                Start = start,
                End = end,
                Confidence = confidence,
                Speaker = first.Speaker,
                ReplacedFrom = replaced[c] ? string.Join(' ', spokenText) : null,
            };
        }

        // Unmapped words borrow a neighbour's span, earlier first.
        for (var c = 0; c < result.Length; c++)
        {
            if (result[c] is not null)
            {
                continue;
            }

            TranscriptWord? neighbour = null;
            for (var k = c - 1; k >= 0 && neighbour is null; k--) neighbour = result[k];
            for (var k = c + 1; k < result.Length && neighbour is null; k++) neighbour = mapped[k] is { Count: > 0 } ? result[k] : null;

            if (neighbour is null)
            {
                // Nothing in the rewrite maps to anything the recogniser timed — every word is
                // invisible to the normaliser. No timings then, rather than invented ones.
                return [];
            }

            result[c] = new TranscriptWord
            {
                Text = candidateWords[c],
                Start = neighbour.Start,
                End = neighbour.End,
                Confidence = neighbour.Confidence,
                Speaker = neighbour.Speaker,
            };
        }

        return result;
    }
}
