using Parakeet.Core.Text;

namespace Parakeet.Core.Tests;

public class WordAlignmentTests
{
    private static string[] Words(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The plain full matrix, kept independent of the implementation under test so a bug in the
    /// divide-and-conquer cannot hide behind the same bug in the oracle.
    /// </summary>
    private static int BruteForceDistance(string[] a, string[] b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>
    /// Every structural property an edit script must have: replaying it against the reference
    /// yields the hypothesis, each side's indexes appear once each and in order, and the edit
    /// count is the distance.
    /// </summary>
    private static void AssertValidAlignment(string[] reference, string[] hypothesis, IReadOnlyList<AlignmentOp> ops)
    {
        var rebuilt = new List<string>();
        var nextReference = 0;
        var nextHypothesis = 0;

        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case AlignmentOpKind.Match:
                    Assert.Equal(nextReference++, op.ReferenceIndex);
                    Assert.Equal(nextHypothesis++, op.HypothesisIndex);
                    Assert.Equal(reference[op.ReferenceIndex], hypothesis[op.HypothesisIndex]);
                    rebuilt.Add(hypothesis[op.HypothesisIndex]);
                    break;
                case AlignmentOpKind.Substitute:
                    Assert.Equal(nextReference++, op.ReferenceIndex);
                    Assert.Equal(nextHypothesis++, op.HypothesisIndex);
                    Assert.NotEqual(reference[op.ReferenceIndex], hypothesis[op.HypothesisIndex]);
                    rebuilt.Add(hypothesis[op.HypothesisIndex]);
                    break;
                case AlignmentOpKind.Delete:
                    Assert.Equal(nextReference++, op.ReferenceIndex);
                    Assert.Equal(-1, op.HypothesisIndex);
                    break;
                case AlignmentOpKind.Insert:
                    Assert.Equal(-1, op.ReferenceIndex);
                    Assert.Equal(nextHypothesis++, op.HypothesisIndex);
                    rebuilt.Add(hypothesis[op.HypothesisIndex]);
                    break;
            }
        }

        Assert.Equal(reference.Length, nextReference);
        Assert.Equal(hypothesis.Length, nextHypothesis);
        Assert.Equal(hypothesis, rebuilt);

        var expected = BruteForceDistance(reference, hypothesis);
        Assert.Equal(expected, WordAlignment.Summarize(ops).Edits);
        Assert.Equal(expected, WordAlignment.Distance(reference, hypothesis));
    }

    [Fact]
    public void IdenticalSequencesAreAllMatches()
    {
        var words = Words("the quick brown fox");
        var ops = WordAlignment.Align(words, words);

        Assert.Equal(0, WordAlignment.Distance(words, words));
        Assert.All(ops, op => Assert.Equal(AlignmentOpKind.Match, op.Kind));
        Assert.Equal(4, ops.Count);
    }

    [Fact]
    public void EmptyAgainstAnythingIsAllInsertsOrAllDeletes()
    {
        var words = Words("a b c");

        var inserts = WordAlignment.Align(Array.Empty<string>(), words);
        Assert.Equal(3, inserts.Count);
        Assert.All(inserts, op => Assert.Equal(AlignmentOpKind.Insert, op.Kind));
        Assert.Equal(3, WordAlignment.Distance(Array.Empty<string>(), words));

        var deletes = WordAlignment.Align(words, Array.Empty<string>());
        Assert.Equal(3, deletes.Count);
        Assert.All(deletes, op => Assert.Equal(AlignmentOpKind.Delete, op.Kind));
        Assert.Equal(3, WordAlignment.Distance(words, Array.Empty<string>()));

        Assert.Empty(WordAlignment.Align(Array.Empty<string>(), Array.Empty<string>()));
    }

    /// <summary>
    /// The shape that defeated a total-count guard: one word dropped, one word added, equal
    /// totals. Index alignment calls five of six words different; the truth is two edits.
    /// </summary>
    [Fact]
    public void OffsettingInsertionAndDeletionCountAsTwoEditsNotAsEveryWordBetween()
    {
        var reference = Words("a b c d e f");
        var hypothesis = Words("a c d e f g");

        var ops = WordAlignment.Align(reference, hypothesis);
        var summary = WordAlignment.Summarize(ops);

        Assert.Equal(2, summary.Edits);
        Assert.Equal(1, summary.Deletions);
        Assert.Equal(1, summary.Insertions);
        Assert.Equal(0, summary.Substitutions);
        Assert.Equal(5, summary.Matches);
        AssertValidAlignment(reference, hypothesis, ops);
    }

    [Fact]
    public void ASubstitutionIsOneEditNotADeletionBesideAnInsertion()
    {
        var reference = Words("i was like that");
        var hypothesis = Words("it was like that");

        var summary = WordAlignment.Summarize(WordAlignment.Align(reference, hypothesis));

        Assert.Equal(1, summary.Substitutions);
        Assert.Equal(1, summary.Edits);
    }

    [Fact]
    public void ComparisonIsOrdinalAndCaseSensitive()
    {
        Assert.Equal(1, WordAlignment.Distance(Words("Uh right"), Words("uh right")));
        Assert.Equal(0, WordAlignment.Distance(Words("uh right"), Words("uh right")));
    }

    [Fact]
    public void TheEditScriptIsDeterministic()
    {
        var reference = Words("a b a b a b a b");
        var hypothesis = Words("b a b a b a b a");

        var first = WordAlignment.Align(reference, hypothesis);
        var second = WordAlignment.Align(reference, hypothesis);

        Assert.Equal(first, second);
        AssertValidAlignment(reference, hypothesis, first);
    }

    [Fact]
    public void TheRecursivePathAgreesWithTheMatrixOnEveryTinyLimit()
    {
        var reference = Words("the cat sat on the mat and then the dog sat on the cat");
        var hypothesis = Words("a cat sat on a mat then the dog sat down on the cat too");

        var direct = WordAlignment.Align(reference, hypothesis);
        AssertValidAlignment(reference, hypothesis, direct);

        // Forcing the cut-over down to one cell drives the recursion all the way to single-row
        // sub-problems, which is the path a real three-hour comparison takes.
        foreach (var limit in new[] { 1, 2, 3, 5, 8, 13 })
        {
            var recursive = WordAlignment.Align(reference, hypothesis, limit);
            AssertValidAlignment(reference, hypothesis, recursive);
        }
    }

    [Fact]
    public void RandomSequencesMatchTheBruteForceOracleOnBothPaths()
    {
        var random = new Random(2026);
        var alphabet = new[] { "a", "b", "c", "d" };

        for (var round = 0; round < 200; round++)
        {
            var reference = Enumerable.Range(0, random.Next(0, 24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
            var hypothesis = Enumerable.Range(0, random.Next(0, 24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();

            AssertValidAlignment(reference, hypothesis, WordAlignment.Align(reference, hypothesis));
            AssertValidAlignment(reference, hypothesis, WordAlignment.Align(reference, hypothesis, smallLimit: 1));
            AssertValidAlignment(reference, hypothesis, WordAlignment.Align(reference, hypothesis, smallLimit: 4));
        }
    }

    [Fact]
    public void LongNearIdenticalSequencesAlignInReasonableTime()
    {
        // 30,000 tokens a side is the three-hour case. Only the count of edits is asserted, because
        // that is what a comparison quotes; the point of the test is that it finishes.
        var random = new Random(7);
        var reference = Enumerable.Range(0, 30_000).Select(i => "w" + (i % 977)).ToArray();
        var hypothesis = reference.ToList();
        for (var k = 0; k < 300; k++)
        {
            var at = random.Next(hypothesis.Count);
            switch (k % 3)
            {
                case 0: hypothesis[at] = "x" + k; break;
                case 1: hypothesis.RemoveAt(at); break;
                default: hypothesis.Insert(at, "y" + k); break;
            }
        }

        var distance = WordAlignment.Distance(reference, hypothesis);
        var ops = WordAlignment.Align(reference, hypothesis);

        Assert.InRange(distance, 1, 300);
        Assert.Equal(distance, WordAlignment.Summarize(ops).Edits);
    }

    [Fact]
    public void NullTokensAreRejected()
    {
        Assert.Throws<ArgumentException>(() => WordAlignment.Distance(new string[] { "a", null! }, Words("a")));
    }
}

public class TranscriptNormalizerTests
{
    [Fact]
    public void AlphanumericTokenIsLowerCaseLettersAndDigitsOnly()
    {
        Assert.Equal("hello", TranscriptNormalizer.AlphanumericToken("Hello,"));
        Assert.Equal("dont", TranscriptNormalizer.AlphanumericToken("don't"));
        Assert.Equal("selfinfluenced", TranscriptNormalizer.AlphanumericToken("self-influenced."));
        Assert.Equal("", TranscriptNormalizer.AlphanumericToken("—"));
        Assert.Equal("2022", TranscriptNormalizer.AlphanumericToken("2022."));
    }

    [Fact]
    public void AlphanumericTokensDropsWhatNormalisesToNothing()
    {
        var tokens = TranscriptNormalizer.AlphanumericTokens(new[] { "Right,", "—", "uh", "..." });

        Assert.Equal(new[] { "right", "uh" }, tokens);
    }

    [Fact]
    public void WerTokensLowerCaseAndStripPunctuation()
    {
        var tokens = TranscriptNormalizer.WordErrorRateTokens("Good morning, and welcome!", keepFillers: false);

        Assert.Equal(new[] { "good", "morning", "and", "welcome" }, tokens);
    }

    [Fact]
    public void WerTokensKeepApostrophesInsideWordsAndUnifyTheirShapes()
    {
        Assert.Equal(new[] { "don't", "o'clock" }, TranscriptNormalizer.WordErrorRateTokens("Don’t o'clock", keepFillers: false));
        Assert.Equal(new[] { "rock", "n", "roll" }, TranscriptNormalizer.WordErrorRateTokens("rock 'n' roll", keepFillers: false));
    }

    [Fact]
    public void WerTokensSplitOnHyphensSoBothSpellingsAgree()
    {
        var joined = TranscriptNormalizer.WordErrorRateTokens("year-over-year", keepFillers: false);
        var spaced = TranscriptNormalizer.WordErrorRateTokens("year over year", keepFillers: false);

        Assert.Equal(spaced, joined);
        Assert.Equal(new[] { "year", "over", "year" }, joined);
    }

    [Fact]
    public void WerTokensKeepDecimalPointsAndDropThousandsSeparators()
    {
        Assert.Equal(new[] { "3.2", "million" }, TranscriptNormalizer.WordErrorRateTokens("$3.2 million", keepFillers: false));
        Assert.Equal(new[] { "1000" }, TranscriptNormalizer.WordErrorRateTokens("1,000", keepFillers: false));
        Assert.Equal(new[] { "17" }, TranscriptNormalizer.WordErrorRateTokens("17%", keepFillers: false));
        Assert.Equal(new[] { "end", "next" }, TranscriptNormalizer.WordErrorRateTokens("end. Next", keepFillers: false));
    }

    [Fact]
    public void WerTokensRemoveBracketedAnnotationsButNotAnUnclosedBracket()
    {
        Assert.Equal(
            new[] { "we", "saw", "growth" },
            TranscriptNormalizer.WordErrorRateTokens("we [inaudible] saw <crosstalk> growth (laughs)", keepFillers: false));

        Assert.Equal(
            new[] { "a", "b", "c" },
            TranscriptNormalizer.WordErrorRateTokens("a [b c", keepFillers: false));
    }

    [Fact]
    public void WerTokensDropFillersUnlessAsked()
    {
        var text = "Um, so, uh, we think, hmm, yes";

        Assert.Equal(new[] { "so", "we", "think", "yes" }, TranscriptNormalizer.WordErrorRateTokens(text, keepFillers: false));
        Assert.Equal(new[] { "um", "so", "uh", "we", "think", "hmm", "yes" }, TranscriptNormalizer.WordErrorRateTokens(text, keepFillers: true));
    }

    [Fact]
    public void WerTokensDoNotDependOnTheCurrentCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal(new[] { "istanbul", "in" }, TranscriptNormalizer.WordErrorRateTokens("ISTANBUL IN", keepFillers: false));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}

public class WordErrorRateTests
{
    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void CountsSubstitutionsDeletionsAndInsertionsAgainstTheReferenceLength()
    {
        var reference = Words("the cat sat on the mat");

        // Each case has exactly one cheapest alignment, so the split is forced, not a tie-break.
        var substitutedAndDeleted = WordErrorRate.Score(reference, Words("the cat sit on mat"));
        Assert.Equal(6, substitutedAndDeleted.ReferenceWords);
        Assert.Equal(5, substitutedAndDeleted.HypothesisWords);
        Assert.Equal(1, substitutedAndDeleted.Substitutions);
        Assert.Equal(1, substitutedAndDeleted.Deletions);
        Assert.Equal(0, substitutedAndDeleted.Insertions);
        Assert.Equal(2, substitutedAndDeleted.Errors);
        Assert.Equal(2.0 / 6.0, substitutedAndDeleted.Rate, 10);

        var inserted = WordErrorRate.Score(reference, Words("the cat sat on the mat today"));
        Assert.Equal(1, inserted.Insertions);
        Assert.Equal(1, inserted.Errors);
        Assert.Equal(1.0 / 6.0, inserted.Rate, 10);
    }

    [Fact]
    public void WhereTwoAlignmentsCostTheSameTheRateIsTheSameAndOnlyTheSplitCanDiffer()
    {
        // "the mat" -> "mat today" is either delete + match + insert or two substitutions; both
        // cost two, the aligner picks substitutions (diagonal first), and the rate is the same.
        var result = WordErrorRate.Score(Words("the cat sat on the mat"), Words("the cat sit on mat today"));

        Assert.Equal(3, result.Errors);
        Assert.Equal(0.5, result.Rate, 10);
    }

    [Fact]
    public void APerfectHypothesisScoresZero()
    {
        var words = Words("nothing to see here");
        Assert.Equal(0.0, WordErrorRate.Score(words, words).Rate);
    }

    [Fact]
    public void AnEmptyReferenceHasNoRate()
    {
        var result = WordErrorRate.Score(Array.Empty<string>(), Words("anything at all"));

        Assert.Equal(3, result.Insertions);
        Assert.True(double.IsNaN(result.Rate));
    }

    [Fact]
    public void TheRateCanExceedOne()
    {
        var result = WordErrorRate.Score(Words("one"), Words("two three four"));

        Assert.Equal(1, result.Substitutions);
        Assert.Equal(2, result.Insertions);
        Assert.Equal(3.0, result.Rate, 10);
    }

    [Fact]
    public void AggregateSumsCountsRatherThanAveragingRates()
    {
        var longFile = new WordErrorRateResult { ReferenceWords = 900, HypothesisWords = 900, Substitutions = 90, Deletions = 0, Insertions = 0 };
        var shortFile = new WordErrorRateResult { ReferenceWords = 100, HypothesisWords = 100, Substitutions = 50, Deletions = 0, Insertions = 0 };

        var corpus = WordErrorRate.Aggregate(new[] { longFile, shortFile });

        Assert.Equal(1000, corpus.ReferenceWords);
        Assert.Equal(140, corpus.Errors);
        // 0.14, not the 0.30 a mean of the two rates would give.
        Assert.Equal(0.14, corpus.Rate, 10);
    }
}
