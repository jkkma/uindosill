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
        Assert.Equal(new[] { "17", "percent" }, TranscriptNormalizer.WordErrorRateTokens("17%", keepFillers: false));
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

    [Theory]
    [InlineData("eighty-seven online accounts", "87 online accounts")]
    [InlineData("two hundred and fifty two cents a share", "252 cents a share")]
    [InlineData("three point two million", "3.2 million")]
    [InlineData("zero point five", "0.5")]
    [InlineData("one thousand and one nights", "1001 nights")]
    [InlineData("two thousand twenty one", "2021")]
    [InlineData("twelve billion", "12000000000")]
    [InlineData("a hundred percent", "a hundred percent")]
    [InlineData("seventeen percent", "17 percent")]
    [InlineData("17%", "17 percent")]
    [InlineData("2021 and 30 June", "2021 and 30 june")]
    public void WerTokensTurnNumberWordsIntoDigitsOnBothSides(string text, string expected)
    {
        var tokens = TranscriptNormalizer.WordErrorRateTokens(text, keepFillers: false);

        Assert.Equal(expected, string.Join(' ', tokens));
    }

    [Theory]
    [InlineData("two and three", "2 and 3")]                 // "and" between small numbers is a conjunction
    [InlineData("twenty twenty one", "20 21")]               // a year said in pairs is not fused
    [InlineData("nineteen eighty four", "19 84")]
    [InlineData("the point is moot", "the point is moot")]   // "point" outside a number
    [InlineData("and then one more", "and then 1 more")]
    [InlineData("five six seven", "5 6 7")]                  // digits read out are separate numbers
    [InlineData("hundred", "hundred")]
    [InlineData("hundreds of millions", "hundreds of millions")]
    [InlineData("one point five million dollars", "1.5 million dollars")]
    public void WerTokensLeaveWhatIsNotANumberAlone(string text, string expected)
    {
        var tokens = TranscriptNormalizer.WordErrorRateTokens(text, keepFillers: false);

        Assert.Equal(expected, string.Join(' ', tokens));
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

    [Fact]
    public void CerTokensAreOneCharacterEachWhereTheWordRuleFindsTwoTokensInAWholeSentence()
    {
        const string sentence = "これは日本語の文です。単語の切れ目はありません。";

        // The reason the character metric exists. The word rule splits only where the sentence
        // punctuates, so a whole Japanese sentence is a denominator of two.
        Assert.Equal(2, TranscriptNormalizer.WordErrorRateTokens(sentence, keepFillers: false).Length);

        var characters = TranscriptNormalizer.CharacterErrorRateTokens(sentence, keepPunctuation: false);
        Assert.Equal(22, characters.Length);
        Assert.All(characters, token => Assert.Single(token.EnumerateRunes()));
    }

    [Fact]
    public void CerTokensKeepANonBmpKanjiThatTheWordRuleDeletesAndSplitsAround()
    {
        var text = "彼を" + char.ConvertFromUtf32(0x20B9F) + "った";

        // char.IsLetterOrDigit is false on both surrogates, so the word rule loses the kanji and
        // cuts the word in two. 𠮟 and 𠮷 are surname characters.
        Assert.Equal(new[] { "彼を", "った" }, TranscriptNormalizer.WordErrorRateTokens(text, keepFillers: false));

        var characters = TranscriptNormalizer.CharacterErrorRateTokens(text, keepPunctuation: false);
        Assert.Equal(5, characters.Length);
        Assert.Equal(char.ConvertFromUtf32(0x20B9F), characters[2]);
    }

    [Fact]
    public void CerTokensFoldWidthAndCompatibilityFormsThatTheWordRuleLeavesDifferent()
    {
        Assert.Equal(
            TranscriptNormalizer.CharacterErrorRateTokens("123", keepPunctuation: false),
            TranscriptNormalizer.CharacterErrorRateTokens("１２３", keepPunctuation: false));
        Assert.Equal(
            TranscriptNormalizer.CharacterErrorRateTokens("カタカナ", keepPunctuation: false),
            TranscriptNormalizer.CharacterErrorRateTokens("ｶﾀｶﾅ", keepPunctuation: false));

        // The word rule folds neither, which is why a score from one is not a score from the other.
        Assert.NotEqual(
            TranscriptNormalizer.WordErrorRateTokens("123", keepFillers: false),
            TranscriptNormalizer.WordErrorRateTokens("１２３", keepFillers: false));
    }

    [Fact]
    public void CerTokensDropWhitespaceSoASpacedHypothesisScoresAsAnUnspacedOne()
    {
        Assert.Equal(
            TranscriptNormalizer.CharacterErrorRateTokens("日本語の文", keepPunctuation: false),
            TranscriptNormalizer.CharacterErrorRateTokens(" 日本 語 の 文 ", keepPunctuation: false));
    }

    [Fact]
    public void CerTokensDropJapanesePunctuationUnlessAsked()
    {
        const string sentence = "また、北側に行くなら。";

        Assert.DoesNotContain("、", TranscriptNormalizer.CharacterErrorRateTokens(sentence, keepPunctuation: false));
        Assert.Contains("、", TranscriptNormalizer.CharacterErrorRateTokens(sentence, keepPunctuation: true));
        Assert.Contains("。", TranscriptNormalizer.CharacterErrorRateTokens(sentence, keepPunctuation: true));
    }

    [Fact]
    public void CerTokensStripAsciiAnnotationsBeforeNormalisingSoFullWidthParenthesesKeepTheirWords()
    {
        // NFKC turns （） into ASCII parentheses. Normalising first would read this as a
        // transcriber's annotation and delete 神社, which is a word the speaker said.
        Assert.Equal(
            new[] { "聖", "域", "神", "社", "を" },
            TranscriptNormalizer.CharacterErrorRateTokens("聖域（神社）を", keepPunctuation: false));

        // An ASCII-bracketed annotation is still removed, as it is for the word rule.
        Assert.Equal(
            new[] { "聖", "域", "を" },
            TranscriptNormalizer.CharacterErrorRateTokens("聖域[inaudible]を", keepPunctuation: false));
    }

    [Fact]
    public void CerTokensDoNotDependOnTheCurrentCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // Turkish lower-cases I to a dotless i; the invariant fold must win.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            Assert.Equal(new[] { "i", "s" }, TranscriptNormalizer.CharacterErrorRateTokens("IS", keepPunctuation: false));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void CerTokensRejectNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => TranscriptNormalizer.CharacterErrorRateTokens(null!, keepPunctuation: false));
    }
}

public class CharacterErrorRateTests
{
    private static string[] Characters(string text) =>
        TranscriptNormalizer.CharacterErrorRateTokens(text, keepPunctuation: false);

    [Fact]
    public void CountsSubstitutionsDeletionsAndInsertionsAgainstTheReferenceLength()
    {
        var reference = Characters("あいうえお");

        var substituted = CharacterErrorRate.Score(reference, Characters("あいうえか"));
        Assert.Equal(5, substituted.ReferenceCharacters);
        Assert.Equal(5, substituted.HypothesisCharacters);
        Assert.Equal(1, substituted.Substitutions);
        Assert.Equal(0, substituted.Deletions);
        Assert.Equal(0, substituted.Insertions);
        Assert.Equal(1.0 / 5.0, substituted.Rate, 10);

        Assert.Equal(1, CharacterErrorRate.Score(reference, Characters("あいうえ")).Deletions);
        Assert.Equal(1, CharacterErrorRate.Score(reference, Characters("あいうえおか")).Insertions);
    }

    [Fact]
    public void TheProbesRealSubstitutionCostsTwoCharactersRatherThanAWholeSentence()
    {
        // 反転 -> 判定, from the Japanese probe on 2026-09-04. The word metric scores this as one
        // wrong token out of one; the character metric sees two wrong characters out of 32.
        var result = CharacterErrorRate.Score(
            Characters("ロスビー数が小さいほど磁気反転に関して星の活性が低下するわけです。"),
            Characters("ロスビー数が小さいほど磁気判定に関して星の活性が低下するわけです。"));

        Assert.Equal(32, result.ReferenceCharacters);
        Assert.Equal(2, result.Substitutions);
        Assert.Equal(0, result.Deletions);
        Assert.Equal(0, result.Insertions);
        Assert.Equal(2.0 / 32.0, result.Rate, 10);
    }

    [Fact]
    public void APerfectHypothesisScoresZero()
    {
        var characters = Characters("群島や湖では、必ずしもヨットは必要ありません。");
        Assert.Equal(0.0, CharacterErrorRate.Score(characters, characters).Rate);
    }

    [Fact]
    public void AnEmptyReferenceHasNoRate()
    {
        var result = CharacterErrorRate.Score(Array.Empty<string>(), Characters("なにか"));

        Assert.Equal(3, result.Insertions);
        Assert.True(double.IsNaN(result.Rate));
    }

    [Fact]
    public void TheRateCanExceedOne()
    {
        var result = CharacterErrorRate.Score(Characters("あ"), Characters("かきく"));

        Assert.Equal(1, result.Substitutions);
        Assert.Equal(2, result.Insertions);
        Assert.Equal(3.0, result.Rate, 10);
    }

    [Fact]
    public void AggregateSumsCountsRatherThanAveragingRates()
    {
        var longFile = new CharacterErrorRateResult
        {
            ReferenceCharacters = 900, HypothesisCharacters = 900, Substitutions = 90, Deletions = 0, Insertions = 0,
        };
        var shortFile = new CharacterErrorRateResult
        {
            ReferenceCharacters = 100, HypothesisCharacters = 100, Substitutions = 50, Deletions = 0, Insertions = 0,
        };

        var corpus = CharacterErrorRate.Aggregate(new[] { longFile, shortFile });

        Assert.Equal(1000, corpus.ReferenceCharacters);
        Assert.Equal(140, corpus.Errors);
        // 0.14, not the 0.30 a mean of the two rates would give.
        Assert.Equal(0.14, corpus.Rate, 10);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => CharacterErrorRate.Score(null!, Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => CharacterErrorRate.Score(Array.Empty<string>(), null!));
        Assert.Throws<ArgumentNullException>(() => CharacterErrorRate.Aggregate(null!));
        Assert.Throws<ArgumentException>(
            () => CharacterErrorRate.Aggregate(new CharacterErrorRateResult[] { null! }));
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

/// <summary>
/// The German compound-number rewrite that goes in front of the translator.
/// </summary>
/// <remarks>
/// Two halves, and the second is the one that decides whether this may ship. The first is that it
/// gets the arithmetic right on the case it exists for. The second — the larger half of these tests
/// — is everything it must <b>not</b> touch, because it runs without knowing the source language
/// and a rewrite of an ordinary word would put invented digits into somebody's transcript.
/// </remarks>
public class GermanNumberWordsTests
{
    [Theory]
    // The measured failure this exists for: 1929, as a German speaker says it.
    [InlineData("neunzehnhundertneunundzwanzig", 1929)]
    [InlineData("einundzwanzig", 21)]
    [InlineData("neunundneunzig", 99)]
    [InlineData("zweihundert", 200)]
    [InlineData("zweihundertfünfzig", 250)]
    [InlineData("zweihundertzweiundfünfzig", 252)]
    [InlineData("einhundert", 100)]
    [InlineData("hunderttausend", 100_000)]
    [InlineData("zweitausend", 2000)]
    [InlineData("zweitausendvierundzwanzig", 2024)]
    [InlineData("neunzehnhundert", 1900)]
    [InlineData("dreitausendsiebenhundertneunundfünfzig", 3759)]
    // Umlauts as the recogniser might spell them either way.
    [InlineData("zweihundertfuenfzig", 250)]
    [InlineData("dreissigtausend", 30_000)]
    [InlineData("dreißigtausend", 30_000)]
    // Case is the recogniser's business, not the parser's.
    [InlineData("Neunzehnhundertneunundzwanzig", 1929)]
    public void ACompoundParsesToItsValue(string token, long expected)
    {
        Assert.True(GermanNumberWords.TryParseCompound(token, out var value), token);
        Assert.Equal(expected, value);
    }

    [Theory]
    // Single number words. These translate perfectly well and rewriting them would change text the
    // gate was scored on for no measured benefit — the two-word floor is what keeps them out.
    [InlineData("zwei")]
    [InlineData("zwanzig")]
    [InlineData("neunzehn")]
    [InlineData("hundert")]
    [InlineData("tausend")]
    [InlineData("eins")]
    [InlineData("zwölf")]
    // Ordinary German words that begin with a number word. The whole token has to parse, so each of
    // these fails on its remainder rather than being half-converted.
    [InlineData("Achtung")]
    [InlineData("Dreieck")]
    [InlineData("Zweifel")]
    [InlineData("dreißigjährige")]
    [InlineData("Neunzehntel")]
    [InlineData("hundertprozentig")]
    [InlineData("Siebensachen")]
    [InlineData("einundzwanzigsten")]
    [InlineData("Tausende")]
    // The indefinite article, which is one of the commonest words in the language.
    [InlineData("ein")]
    [InlineData("eine")]
    [InlineData("einer")]
    [InlineData("einem")]
    [InlineData("einen")]
    // And words from the other languages this many-to-one translator sees, since nothing tells it
    // which one it is reading.
    [InlineData("veintiuno")]
    [InlineData("negentien")]
    [InlineData("undertaking")]
    [InlineData("understand")]
    [InlineData("underneath")]
    public void AnythingThatIsNotAWholeCompoundIsLeftAlone(string token)
    {
        Assert.False(GermanNumberWords.TryParseCompound(token, out _), token);
    }

    [Fact]
    public void TheRewriteKeepsEverythingAroundIt()
    {
        Assert.Equal(
            "Ralf Dahrendorf wurde 1929 in Hamburg geboren.",
            GermanNumberWords.ToDigits("Ralf Dahrendorf wurde neunzehnhundertneunundzwanzig in Hamburg geboren."));

        // Punctuation, hyphens and repeated separators all survive untouched.
        Assert.Equal(
            "Im Jahr 1929, also 21 Jahre  später —  200 Meter.",
            GermanNumberWords.ToDigits(
                "Im Jahr neunzehnhundertneunundzwanzig, also einundzwanzig Jahre  später —  zweihundert Meter."));
    }

    [Fact]
    public void TextWithNothingToRewriteComesBackTheSameInstance()
    {
        // Not merely equal. The shipping path calls this on every segment of every transcript in
        // every language, and the claim that it is a no-op on 23 of the 24 should cost nothing.
        const string Text = "Esto parece tener sentido, ya que en la Tierra no se percibe su movimiento.";
        Assert.Same(Text, GermanNumberWords.ToDigits(Text));
    }

    [Fact]
    public void AHyphenatedCompoundIsTwoTokensAndBothAreRewritten()
    {
        // Hyphens end a token, so "neunzehnhundert-neunundzwanzig" is two compounds rather than one
        // number. Both are numbers and both are rewritten; nothing here tries to rejoin them, which
        // would be a guess about what the speaker meant.
        Assert.Equal("1900-29", GermanNumberWords.ToDigits("neunzehnhundert-neunundzwanzig"));
    }

    /// <summary>
    /// The check that decided whether this may run in the shipping path at all, kept re-runnable
    /// rather than performed once and written down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The translation gate was scored on FLEURS <c>raw_transcription</c> — written prose, where
    /// numbers are already digits. If this rewrite changes any of that text, then the sentences the
    /// shipping path sends the translator are no longer the sentences the published chrF++ figures
    /// describe, and every one of those figures would have to be re-earned. So it must be a **no-op
    /// on written text**, across all 24 source languages and not only German, because it runs
    /// without being told which language it is reading.
    /// </para>
    /// <para>
    /// Opt-in, like the translation checkpoint tests, because it reads a corpus this repository
    /// does not carry. Point <c>UINDOSILL_FLEURS_DIR</c> at the <c>data/</c> directory of a
    /// <c>google/fleurs</c> snapshot — the one <c>scripts/measure-translation.py</c> leaves in the
    /// Hugging Face cache — and it scores every <c>test.tsv</c> under it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ItChangesNothingInFleursWrittenText()
    {
        var directory = Environment.GetEnvironmentVariable("UINDOSILL_FLEURS_DIR");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory),
            "Set UINDOSILL_FLEURS_DIR to the data/ directory of a google/fleurs snapshot. The gate " +
            "figures were scored on that text, so a rewrite that touches it invalidates them.");

        var offenders = new List<string>();
        var sentences = 0;
        var configs = 0;

        foreach (var tsv in Directory.EnumerateFiles(directory!, "test.tsv", SearchOption.AllDirectories))
        {
            configs++;
            var config = Path.GetFileName(Path.GetDirectoryName(tsv)) ?? "?";
            foreach (var line in File.ReadLines(tsv))
            {
                var fields = line.Split('\t');
                if (fields.Length < 3)
                {
                    continue;
                }

                var raw = fields[2].Trim();
                if (raw.Length == 0)
                {
                    continue;
                }

                sentences++;
                var rewritten = GermanNumberWords.ToDigits(raw);
                if (!ReferenceEquals(rewritten, raw))
                {
                    offenders.Add($"{config} {fields[0]}: {raw}\n            -> {rewritten}");
                }
            }
        }

        Assert.True(configs > 0, $"no test.tsv found under {directory}");
        Assert.True(sentences > 0, $"no sentences read from {directory}");

        // Every offender is listed rather than only the count. If this ever fails, the question is
        // which word it caught and whether the grammar or the corpus is wrong, and a bare number
        // answers neither.
        Assert.True(
            offenders.Count == 0,
            $"the rewrite is not a no-op on written text: {offenders.Count} of {sentences} sentences in " +
            $"{configs} configs changed. The gate figures describe the unrewritten text.\n  " +
            string.Join("\n  ", offenders.Take(40)));
    }
}
