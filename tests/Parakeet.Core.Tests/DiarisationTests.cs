using System.Globalization;
using System.Text.Json;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Formatting;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

/// <summary>
/// The scorer against pyannote.metrics. <c>tests/fixtures/diarisation/scorer/expected.json</c> holds
/// what pyannote.metrics 4.1 computed for every fixture pair — written by
/// <c>scripts/validate-der.py</c>, never by hand — and this asserts the C# scorer reproduces every
/// figure. Until this passed, the scorer was wrong by definition; if it stops passing, it is wrong
/// again until shown otherwise.
/// </summary>
public class DiarisationScorerValidationTests
{
    private static readonly string Fixtures = FindFixtures();

    private static string FindFixtures()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "tests", "fixtures", "diarisation", "scorer");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("tests/fixtures/diarisation/scorer was not found above the test binary.");
    }

    public static IEnumerable<object[]> Cases()
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "expected.json")));
        foreach (var property in expected.RootElement.GetProperty("cases").EnumerateObject())
        {
            yield return [property.Name];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheScorerReproducesWhatPyannoteMetricsComputed(string name)
    {
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "expected.json")));
        var blocks = expected.RootElement.GetProperty("cases").GetProperty(name);
        var conventions = expected.RootElement.GetProperty("conventions");
        var collar = TimeSpan.FromSeconds(conventions.GetProperty("headline").GetProperty("collarSeconds").GetDouble());

        var reference = RttmFile.Parse(File.ReadAllText(Path.Combine(Fixtures, $"{name}.ref.rttm"))).Turns;
        var hypothesis = RttmFile.Parse(File.ReadAllText(Path.Combine(Fixtures, $"{name}.hyp.rttm"))).Turns;

        var headline = DiarisationErrorRate.Score(reference, hypothesis, new DiarisationScoringOptions { Collar = collar });
        var strict = DiarisationErrorRate.Score(reference, hypothesis, new DiarisationScoringOptions { Collar = TimeSpan.Zero });
        var skipped = DiarisationErrorRate.Score(reference, hypothesis, new DiarisationScoringOptions { Collar = collar, SkipOverlap = true });

        AssertBlock(blocks.GetProperty("headline"), headline.Overall, $"{name} headline");
        AssertBlock(blocks.GetProperty("strict"), strict.Overall, $"{name} strict");
        AssertBlock(blocks.GetProperty("overlapRegions"), headline.OverlapRegions, $"{name} overlap regions");
        AssertBlock(blocks.GetProperty("skipOverlap"), skipped.Overall, $"{name} skip-overlap");
    }

    private static void AssertBlock(JsonElement want, DiarisationErrorComponents got, string what)
    {
        // A microsecond: pyannote.core's own segment precision, and far below anything printed.
        const double Tolerance = 1e-6;
        Assert.True(Math.Abs(want.GetProperty("referenceSpeechSeconds").GetDouble() - got.ReferenceSpeech) <= Tolerance, $"{what}: reference speech {got.ReferenceSpeech} vs {want.GetProperty("referenceSpeechSeconds")}");
        Assert.True(Math.Abs(want.GetProperty("missedSeconds").GetDouble() - got.Missed) <= Tolerance, $"{what}: missed {got.Missed} vs {want.GetProperty("missedSeconds")}");
        Assert.True(Math.Abs(want.GetProperty("falseAlarmSeconds").GetDouble() - got.FalseAlarm) <= Tolerance, $"{what}: false alarm {got.FalseAlarm} vs {want.GetProperty("falseAlarmSeconds")}");
        Assert.True(Math.Abs(want.GetProperty("confusionSeconds").GetDouble() - got.Confusion) <= Tolerance, $"{what}: confusion {got.Confusion} vs {want.GetProperty("confusionSeconds")}");

        var rate = want.GetProperty("rate");
        if (rate.ValueKind == JsonValueKind.Null)
        {
            Assert.True(double.IsNaN(got.Rate), $"{what}: expected an undefined rate, got {got.Rate}");
        }
        else
        {
            Assert.True(Math.Abs(rate.GetDouble() - got.Rate) <= 1e-8, $"{what}: rate {got.Rate} vs {rate.GetDouble()}");
        }
    }

    [Fact]
    public void TheFixtureSetCoversWhatItClaimsTo()
    {
        // The validation is only as strong as the pairs it runs over. These are the shapes the
        // scorer has branches for; a fixture set that lost one would validate less than it says.
        var names = Cases().Select(c => (string)c[0]).ToList();
        Assert.Contains("crosstalk", names);
        Assert.Contains("self-overlap", names);
        Assert.Contains("over-clustered", names);
        Assert.Contains("three-speakers-merged-to-two", names);
        Assert.Contains("hypothesis-outside-extent", names);
        Assert.Contains("long-jittered-conversation", names);
        // The pair whose optimal mapping differs before and after the collar is cut out: it pins
        // that the mapping is found on the collared intervals, as pyannote finds it.
        Assert.Contains("mapping-tipped-by-collar", names);
        Assert.True(names.Count >= 10);
    }
}

public class DiarisationErrorRateTests
{
    private static SpeakerTurn Turn(double start, double end, string speaker) =>
        new() { Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end), Speaker = speaker };

    private static DiarisationScoringOptions Collar(double seconds) =>
        new() { Collar = TimeSpan.FromSeconds(seconds) };

    [Fact]
    public void AHandComputedPairComesOutExactly()
    {
        // Reference A [0,10], B [8,20]; hypothesis x [0,9], y [9,20]; collar 0.25 (0.125 either side).
        // A loses 0.125 at 0, 0.125 at 10 and 0.25 around B's start at 8 → 9.5; B loses 0.125 at 8,
        // 0.125 at 20 and 0.25 around A's end at 10 → 11.5; total 21. In the overlap [8,10] minus
        // collars, 1.75 s, the reference has two speakers and the hypothesis one → missed 1.75.
        var score = DiarisationErrorRate.Score(
            [Turn(0, 10, "A"), Turn(8, 20, "B")],
            [Turn(0, 9, "x"), Turn(9, 20, "y")],
            Collar(0.25));

        Assert.Equal(21.0, score.Overall.ReferenceSpeech, 9);
        Assert.Equal(1.75, score.Overall.Missed, 9);
        Assert.Equal(0.0, score.Overall.FalseAlarm, 9);
        Assert.Equal(0.0, score.Overall.Confusion, 9);
        Assert.Equal(19.25, score.Overall.Correct, 9);
        Assert.Equal(1.75 / 21.0, score.Overall.Rate, 9);
        Assert.Equal("A", score.Mapping["x"]);
        Assert.Equal("B", score.Mapping["y"]);

        // The overlap-region breakdown is where the whole error lives.
        Assert.Equal(3.5, score.OverlapRegions.ReferenceSpeech, 9);
        Assert.Equal(1.75, score.OverlapRegions.Missed, 9);
        Assert.Equal(0.5, score.OverlapRegions.Rate, 9);
    }

    [Fact]
    public void TheOverlapBreakdownIsAdditiveWithTheRestOfTheFile()
    {
        var reference = new[] { Turn(0, 8, "A"), Turn(3, 12, "B"), Turn(10, 20, "A"), Turn(15, 26, "B") };
        var hypothesis = new[] { Turn(0, 5.5, "s0"), Turn(5.5, 11, "s1"), Turn(11, 17.5, "s0"), Turn(17.5, 26, "s1") };

        var score = DiarisationErrorRate.Score(reference, hypothesis, Collar(0.25));

        Assert.True(score.OverlapRegions.ReferenceSpeech < score.Overall.ReferenceSpeech);
        Assert.True(score.OverlapRegions.Missed <= score.Overall.Missed + 1e-9);
        Assert.True(score.OverlapRegions.Confusion <= score.Overall.Confusion + 1e-9);
        Assert.True(score.OverlapRegions.FalseAlarm <= score.Overall.FalseAlarm + 1e-9);
    }

    [Fact]
    public void RelabellingTheHypothesisChangesNothing()
    {
        var reference = new[] { Turn(0, 5, "host_a"), Turn(5, 9, "host_b"), Turn(9, 14, "host_a") };
        var hypothesis = new[] { Turn(0, 5, "SPEAKER_07"), Turn(5, 9, "SPEAKER_03"), Turn(9, 14, "SPEAKER_07") };

        var score = DiarisationErrorRate.Score(reference, hypothesis, Collar(0.25));

        Assert.Equal(0.0, score.Overall.Rate, 9);
        Assert.Equal("host_a", score.Mapping["SPEAKER_07"]);
        Assert.Equal("host_b", score.Mapping["SPEAKER_03"]);
    }

    [Fact]
    public void GreedyMappingWouldGetThisWrongAndExhaustiveSearchDoesNot()
    {
        // Hypothesis 'p' overlaps A most, but B has nobody else: greedy takes p→A (6 s) and leaves
        // B with q (0 s) for 6 s correct; the optimum is q→A (4 s) and p→B (5 s) for 9 s. DER must
        // reflect the optimum — that is what makes it DER rather than a greedy score.
        var reference = new[] { Turn(0, 10, "A"), Turn(10, 20, "B") };
        var hypothesis = new[] { Turn(4, 15, "p"), Turn(0, 4, "q") };
        var matrix = new double[2, 2];
        matrix[0, 0] = 6; matrix[0, 1] = 4;   // A vs p, q
        matrix[1, 0] = 5; matrix[1, 1] = 0;   // B vs p, q

        var mapping = DiarisationErrorRate.OptimalMapping(matrix);

        Assert.Equal(1, mapping[0]); // p → B
        Assert.Equal(0, mapping[1]); // q → A

        var score = DiarisationErrorRate.Score(reference, hypothesis, Collar(0));

        Assert.Equal("B", score.Mapping["p"]);
        Assert.Equal("A", score.Mapping["q"]);
        Assert.Equal(20.0, score.Overall.ReferenceSpeech, 9);
        Assert.Equal(6.0, score.Overall.Confusion, 9);   // A's [4,10] under p→B
        Assert.Equal(5.0, score.Overall.Missed, 9);      // B's [15,20], nobody
        Assert.Equal(11.0 / 20.0, score.Overall.Rate, 9); // greedy would have said 14/20
    }

    [Fact]
    public void TheExhaustiveMappingMatchesBruteForceOverEveryPermutation()
    {
        var random = new Random(4242);
        for (var trial = 0; trial < 200; trial++)
        {
            var rows = random.Next(1, 6);
            var columns = random.Next(1, 7);
            var matrix = new double[rows, columns];
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    matrix[r, c] = random.Next(0, 4) == 0 ? 0 : Math.Round(random.NextDouble() * 10, 2);
                }
            }

            var mapping = DiarisationErrorRate.OptimalMapping(matrix);
            var found = 0.0;
            var seen = new HashSet<int>();
            for (var c = 0; c < columns; c++)
            {
                if (mapping[c] is { } r)
                {
                    Assert.True(seen.Add(r), "a reference label was used twice");
                    found += matrix[r, c];
                }
            }

            Assert.Equal(BestByPermutation(matrix), found, 9);
        }
    }

    private static double BestByPermutation(double[,] matrix)
    {
        var rows = matrix.GetLength(0);
        var columns = matrix.GetLength(1);
        var best = 0.0;
        var used = new bool[columns];

        void Recurse(int r, double sum)
        {
            if (r == rows)
            {
                best = Math.Max(best, sum);
                return;
            }

            Recurse(r + 1, sum);
            for (var c = 0; c < columns; c++)
            {
                if (!used[c])
                {
                    used[c] = true;
                    Recurse(r + 1, sum + matrix[r, c]);
                    used[c] = false;
                }
            }
        }

        Recurse(0, 0);
        return best;
    }

    [Fact]
    public void AnEmptyHypothesisMissesEverything()
    {
        var score = DiarisationErrorRate.Score([Turn(0, 10, "A"), Turn(10, 20, "B")], [], Collar(0));

        Assert.Equal(20.0, score.Overall.ReferenceSpeech, 9);
        Assert.Equal(20.0, score.Overall.Missed, 9);
        Assert.Equal(1.0, score.Overall.Rate, 9);
        Assert.Empty(score.Mapping);
    }

    [Fact]
    public void AnEmptyReferenceHasNoRateAndEveryHypothesisSecondIsAFalseAlarm()
    {
        var score = DiarisationErrorRate.Score([], [Turn(0, 10, "x")], Collar(0.25));

        Assert.True(double.IsNaN(score.Overall.Rate));
        Assert.Equal(10.0, score.Overall.FalseAlarm, 9);
        Assert.Contains(score.Warnings, w => w.Contains("no speech", StringComparison.Ordinal));
    }

    [Fact]
    public void BothEmptyScoresNothingAndSaysSo()
    {
        var score = DiarisationErrorRate.Score([], [], Collar(0.25));

        Assert.Equal(0.0, score.Overall.ReferenceSpeech);
        Assert.True(double.IsNaN(score.Overall.Rate));
        Assert.Contains(score.Warnings, w => w.Contains("empty", StringComparison.Ordinal));
    }

    [Fact]
    public void SelfOverlapIsCountedTwiceAndWarnedAbout()
    {
        // As pyannote.metrics counts it — pinned by the self-overlap fixture as well.
        var score = DiarisationErrorRate.Score(
            [Turn(0, 10, "A"), Turn(5, 15, "A")], [Turn(0, 15, "x")], Collar(0));

        Assert.Equal(20.0, score.Overall.ReferenceSpeech, 9);
        Assert.Equal(5.0, score.Overall.Missed, 9);
        Assert.Contains(score.Warnings, w => w.Contains("overlaps itself", StringComparison.Ordinal) && w.Contains("5 s", StringComparison.Ordinal));
        // Same-label overlap is not an overlap region: nobody else was talking.
        Assert.Equal(0.0, score.OverlapRegions.ReferenceSpeech);
    }

    [Fact]
    public void SkipOverlapRemovesTheOverlapRegionsFromTheScore()
    {
        var reference = new[] { Turn(0, 10, "A"), Turn(8, 20, "B") };
        var hypothesis = new[] { Turn(0, 9, "x"), Turn(9, 20, "y") };

        var scored = DiarisationErrorRate.Score(reference, hypothesis, new DiarisationScoringOptions { Collar = TimeSpan.Zero, SkipOverlap = true });

        // 22 s of reference speech, 4 of it (2 s × 2 speakers) in the overlap [8,10] — gone.
        Assert.Equal(18.0, scored.Overall.ReferenceSpeech, 9);
        Assert.Equal(0.0, scored.Overall.Rate, 9);
        Assert.Equal(0.0, scored.OverlapRegions.ReferenceSpeech, 9);
    }

    [Fact]
    public void SkipOverlapAlsoRemovesASpeakerOverlappingThemselves()
    {
        // pyannote's skip_overlap extrudes every pairwise overlap of reference turns, labels
        // ignored; its get_overlap — which the breakdown uses — skips same-label pairs. Two rules,
        // and the scorer copies each where pyannote uses it. Pinned here and by the self-overlap
        // fixture's skipOverlap block.
        var reference = new[] { Turn(0, 10, "A"), Turn(5, 15, "A"), Turn(15, 20, "B") };
        var hypothesis = new[] { Turn(0, 15, "x"), Turn(15, 20, "y") };

        var scored = DiarisationErrorRate.Score(reference, hypothesis, new DiarisationScoringOptions { Collar = TimeSpan.Zero, SkipOverlap = true });

        // [5,10] is cut, so A contributes 10 s (not 20 counted twice) and B 5 s.
        Assert.Equal(15.0, scored.Overall.ReferenceSpeech, 9);
        Assert.Equal(0.0, scored.Overall.Rate, 9);
        // And the breakdown is still over distinct speakers, so it sees nothing here.
        Assert.Equal(0.0, scored.OverlapRegions.ReferenceSpeech, 9);
    }

    [Fact]
    public void ZeroLengthTurnsContributeNothingAndAddNoCollar()
    {
        var with = DiarisationErrorRate.Score([Turn(0, 5, "A"), Turn(5, 5, "B"), Turn(5, 10, "A")], [Turn(0, 10, "x")], Collar(0.25));
        var without = DiarisationErrorRate.Score([Turn(0, 5, "A"), Turn(5, 10, "A")], [Turn(0, 10, "x")], Collar(0.25));

        Assert.Equal(without.Overall.ReferenceSpeech, with.Overall.ReferenceSpeech, 9);
        Assert.Equal(without.Overall.Rate, with.Overall.Rate, 9);
    }

    [Fact]
    public void AggregateSumsComponentsRatherThanAveragingRates()
    {
        var a = new DiarisationErrorComponents { ReferenceSpeech = 10, Missed = 5, FalseAlarm = 0, Confusion = 0 };
        var b = new DiarisationErrorComponents { ReferenceSpeech = 90, Missed = 0, FalseAlarm = 0, Confusion = 0 };

        var total = DiarisationErrorRate.Aggregate([a, b]);

        Assert.Equal(100.0, total.ReferenceSpeech);
        Assert.Equal(0.05, total.Rate, 9);   // not the 0.25 a mean of rates would give
    }

    [Fact]
    public void TheConventionIsSpelledOut()
    {
        var text = new DiarisationScoringOptions().Describe();

        Assert.Contains("collar 0.25 s", text, StringComparison.Ordinal);
        Assert.Contains("0.125 s either side", text, StringComparison.Ordinal);
        Assert.Contains("overlap included", text, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidTurnsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => DiarisationErrorRate.Score([Turn(5, 4, "A")], [], Collar(0)));
        Assert.Throws<ArgumentException>(() => DiarisationErrorRate.Score([Turn(0, 4, " ")], [], Collar(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiarisationErrorRate.Score([], [], Collar(-1)));
    }
}

public class RttmAndLabelTests
{
    [Fact]
    public void RttmRoundTripsWithThreeDecimalsAndLfEndings()
    {
        var turns = new[]
        {
            new SpeakerTurn { Start = TimeSpan.FromSeconds(1.2345), End = TimeSpan.FromSeconds(4), Speaker = "host_a" },
            new SpeakerTurn { Start = TimeSpan.FromSeconds(3.5), End = TimeSpan.FromSeconds(9.0005), Speaker = "host_b" },
        };

        var text = RttmFile.Write(turns, "stretch-01");

        Assert.Equal("SPEAKER stretch-01 1 1.235 2.766 <NA> <NA> host_a <NA> <NA>\nSPEAKER stretch-01 1 3.500 5.501 <NA> <NA> host_b <NA> <NA>\n", text);

        var parsed = RttmFile.Parse(text);
        Assert.Equal(["stretch-01"], parsed.FileIds);
        Assert.Equal(2, parsed.Turns.Count);
        Assert.Equal("host_a", parsed.Turns[0].Speaker);
        Assert.Equal(1.235, parsed.Turns[0].Start.TotalSeconds, 6);
        Assert.Equal(4.001, parsed.Turns[0].End.TotalSeconds, 6);
    }

    [Fact]
    public void RttmReaderToleratesNineFieldsCommentsAndOtherRecordTypes()
    {
        var parsed = RttmFile.Parse(
            ";; a comment\n" +
            "SPKR-INFO f 1 <NA> <NA> <NA> unknown host_a <NA>\n" +
            "SPEAKER f 1 0.5 2 <NA> <NA> host_a <NA>\r\n" +
            "\n" +
            "SPEAKER   f 1   2.5  1.0 <NA> <NA> host_b <NA> <NA>\n");

        Assert.Equal(2, parsed.Turns.Count);
        Assert.Equal(1, parsed.SkippedLines);
        Assert.Equal(TimeSpan.FromSeconds(3.5), parsed.Turns[1].End);
    }

    [Fact]
    public void RttmReaderStripsAByteOrderMarkRatherThanSkippingTheFirstTurn()
    {
        // Files arrive from other tools and plenty of them write UTF-8 with a mark. U+FEFF is
        // not whitespace to Trim, so unstripped it would make field one of line one a record
        // type the reader does not know -- and its tolerance for those is to skip the line,
        // which would turn a mark into one silently missing turn rather than an error.
        var parsed = RttmFile.Parse(
            "\uFEFF" +
            "SPEAKER stretch 1 0.000 2.000 <NA> <NA> host_a <NA> <NA>\n" +
            "SPEAKER stretch 1 2.000 3.000 <NA> <NA> host_b <NA> <NA>\n");

        Assert.Equal(2, parsed.Turns.Count);
        Assert.Equal(0, parsed.SkippedLines);
        Assert.Equal(["stretch"], parsed.FileIds);
        Assert.Equal("host_a", parsed.Turns[0].Speaker);
        Assert.Equal(TimeSpan.Zero, parsed.Turns[0].Start);
    }

    [Theory]
    [InlineData("SPEAKER f 1 0.5 2 <NA> <NA>\n")]                        // too few fields
    [InlineData("SPEAKER f 1 abc 2 <NA> <NA> host_a <NA> <NA>\n")]        // onset not a number
    [InlineData("SPEAKER f 1 0.5 -2 <NA> <NA> host_a <NA> <NA>\n")]       // negative duration
    [InlineData("SPEAKER f 1 0.5 2 <NA> <NA> <NA> <NA> <NA>\n")]          // no speaker
    public void MalformedRttmIsRefusedNotGuessed(string content)
    {
        Assert.Throws<FormatException>(() => RttmFile.Parse(content));
    }

    [Fact]
    public void RttmWriterRefusesWhitespaceInLabelsAndIds()
    {
        var turn = new SpeakerTurn { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Speaker = "host a" };
        Assert.Throws<ArgumentException>(() => RttmFile.Write([turn], "f"));
        Assert.Throws<ArgumentException>(() => RttmFile.Write([turn with { Speaker = "host_a" }], "my file"));
    }

    [Fact]
    public void AudacityLabelsBecomeTurnsWithTheTextAsTheSpeaker()
    {
        // Audacity 3.x export: six decimals, a spectral line after each label, tracks merged in time order.
        const string Export =
            "0.500000\t6.200000\tHost A\n" +
            "\\\t-1.000000\t-1.000000\n" +
            "6.000000\t9.800000\tHost B\n" +
            "\\\t-1.000000\t-1.000000\n" +
            "9.900000\t9.900000\tHost A\n" +           // a point label — a click, not speech
            "9.900000\t10.300000\tHost A\n" +
            "10.100000\t18.400000\tHost B\n";

        var labels = AudacityLabels.Parse(Export);

        Assert.Equal(5, labels.LabelCount);
        Assert.Equal(1, labels.PointLabelsDropped);
        Assert.Equal(0, labels.LabelsMerged);
        Assert.Equal(4, labels.Turns.Count);
        Assert.Equal(["Host_A", "Host_B"], SpeakerTurns.Speakers(labels.Turns));
        Assert.Equal(TimeSpan.FromSeconds(6.2), labels.Turns[0].End);
    }

    [Fact]
    public void AudacitySameSpeakerOverlapsAndBridgedGapsMerge()
    {
        const string Export =
            "0.0\t5.0\tA\n" +
            "4.5\t7.0\tA\n" +      // overlaps the one before: always merged
            "7.3\t9.0\tA\n" +      // 0.3 s gap: merged only when bridged
            "9.0\t12.0\tB\n";

        var unbridged = AudacityLabels.Parse(Export);
        Assert.Equal(3, unbridged.Turns.Count);
        Assert.Equal(1, unbridged.LabelsMerged);

        var bridged = AudacityLabels.Parse(Export, TimeSpan.FromSeconds(0.5));
        Assert.Equal(2, bridged.Turns.Count);
        Assert.Equal(2, bridged.LabelsMerged);
        Assert.Equal(TimeSpan.FromSeconds(9), bridged.Turns.First(t => t.Speaker == "A").End);
    }

    [Theory]
    [InlineData("0.5\t2.0\t\n")]          // no speaker
    [InlineData("0.5\t2.0\n")]            // no third field
    [InlineData("2.0\t0.5\tA\n")]         // ends before it starts
    [InlineData("x\t0.5\tA\n")]           // not a number
    public void MalformedAudacityLabelsAreRefused(string content)
    {
        Assert.Throws<FormatException>(() => AudacityLabels.Parse(content));
    }

    [Fact]
    public void RenamingByFirstAppearanceIsDeterministicAndOrderedByTime()
    {
        var turns = new[]
        {
            new SpeakerTurn { Start = TimeSpan.FromSeconds(4), End = TimeSpan.FromSeconds(6), Speaker = "SPEAKER_01" },
            new SpeakerTurn { Start = TimeSpan.FromSeconds(0), End = TimeSpan.FromSeconds(3), Speaker = "SPEAKER_07" },
            new SpeakerTurn { Start = TimeSpan.FromSeconds(6), End = TimeSpan.FromSeconds(8), Speaker = "SPEAKER_07" },
        };

        var renamed = SpeakerTurns.RenameByFirstAppearance(turns);

        Assert.Equal("Speaker 2", renamed[0].Speaker);   // SPEAKER_01 was heard second
        Assert.Equal("Speaker 1", renamed[1].Speaker);
        Assert.Equal("Speaker 1", renamed[2].Speaker);
    }
}

public class SpeakerAssignmentTests
{
    private static TranscriptWord Word(string text, double start, double end) =>
        new() { Text = text, Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end) };

    private static SpeakerTurn Turn(double start, double end, string speaker) =>
        new() { Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end), Speaker = speaker };

    [Fact]
    public void ASegmentIsCutWhereTheSpeakerChangesAndEveryWordIsAttributed()
    {
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(6),
            Text = "hello there yes indeed",
            Words = [Word("hello", 0.2, 0.6), Word("there", 0.7, 1.1), Word("yes", 3.1, 3.4), Word("indeed", 3.5, 4.0)],
            SourceSegmentIndex = 3,
        };
        var turns = new[] { Turn(0, 3, "Speaker 1"), Turn(3, 6, "Speaker 2") };

        var result = SpeakerAssignment.Apply([segment], turns);

        Assert.Equal(2, result.Count);
        Assert.Equal("Speaker 1", result[0].Speaker);
        Assert.Equal("hello there", result[0].Text);
        Assert.Equal(TimeSpan.Zero, result[0].Start);              // the first piece keeps the segment's start
        Assert.Equal(TimeSpan.FromSeconds(1.1), result[0].End);    // and ends with its last word
        Assert.Equal("Speaker 2", result[1].Speaker);
        Assert.Equal("yes indeed", result[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(3.1), result[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(6), result[1].End);      // the last piece keeps the segment's end
        Assert.All(result, s => Assert.Equal(3, s.SourceSegmentIndex));
        Assert.All(result.SelectMany(s => s.Words), w => Assert.NotNull(w.Speaker));
    }

    [Fact]
    public void AWordJustOutsideEveryTurnStillGetsTheNearestOneWithinTheTolerance()
    {
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(3),
            Text = "early late",
            Words = [Word("early", 0.0, 0.3), Word("late", 2.6, 2.9)],
        };

        var result = SpeakerAssignment.Apply([segment], [Turn(0.4, 2.0, "A")]);

        // Cut between the attributed word and the unattributed one: nobody's words do not sit
        // under somebody's name.
        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].Speaker);
        Assert.Equal("A", result[0].Words[0].Speaker);   // 0.1 s before the turn
        Assert.Null(result[1].Speaker);
        Assert.Null(result[1].Words[0].Speaker);         // 0.6 s after it: past the 0.5 s tolerance
    }

    [Fact]
    public void InCrosstalkTheWordGoesToWhoeverOverlapsItMost()
    {
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(4),
            Text = "shared",
            Words = [Word("shared", 1.0, 2.0)],
        };

        var result = SpeakerAssignment.Apply([segment], [Turn(0, 1.3, "A"), Turn(1.2, 4, "B")]);

        Assert.Equal("B", result[0].Words[0].Speaker);   // B covers 0.8 s of the word, A 0.3 s
    }

    [Fact]
    public void AtAHandoffTheOverlappedWordsGoToTheSpeakerWhoCarriesOn()
    {
        // A finishes while B is already under way. Both turns contain every word of "also you can",
        // so the overlap ties on each and the tie-break alone decides: the turn that ends later is
        // B's, who is still talking afterwards, so the name changes where the crosstalk starts.
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(12),
            Text = "and generalizing also you can",
            Words =
            [
                Word("and", 1.0, 1.4), Word("generalizing", 1.5, 2.4),
                Word("also", 6.2, 6.6), Word("you", 6.7, 6.9), Word("can", 7.0, 7.4),
            ],
        };

        var result = SpeakerAssignment.Apply([segment], [Turn(0, 8, "A"), Turn(6, 14, "B")]);

        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].Speaker);
        Assert.Equal("and generalizing", result[0].Text);
        Assert.Equal("B", result[1].Speaker);
        Assert.Equal("also you can", result[1].Text);
    }

    [Fact]
    public void AZeroLengthWordInsideTheCrosstalkTakesTheSameTieBreakAsItsNeighbours()
    {
        // The decoder collapses an end-before-start word to zero length. Inside two turns it
        // overlaps neither, so until 2026-08-22 it fell to the nearest-turn rule with a negative
        // gap for both — the more negative being the turn that started earlier — and went to A
        // where every word around it went to B: three pieces, B | A | B, one word under the
        // other name. Containment is overlap for a point, and the later-ending turn wins the tie.
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(12),
            Text = "also uh you",
            Words = [Word("also", 6.2, 6.6), Word("uh", 7.0, 7.0), Word("you", 7.2, 7.4)],
        };

        var result = SpeakerAssignment.Apply([segment], [Turn(0, 8, "A"), Turn(6, 14, "B")]);

        var piece = Assert.Single(result);
        Assert.Equal("B", piece.Speaker);
        Assert.Equal("also uh you", piece.Text);
        Assert.All(piece.Words, w => Assert.Equal("B", w.Speaker));
    }

    [Fact]
    public void ABackChannelDoesNotTakeTheWordsOfTheSpeakerItInterrupts()
    {
        // The other shape that reaches the same tie, and the one it must not break: B's "yeah"
        // lands inside A's turn, and A both starts earlier and ends later, so A keeps every word.
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(12),
            Text = "one two three",
            Words = [Word("one", 1.0, 1.4), Word("two", 6.2, 6.6), Word("three", 9.0, 9.4)],
        };

        var result = SpeakerAssignment.Apply([segment], [Turn(0, 11, "A"), Turn(6, 7, "B")]);

        Assert.Single(result);
        Assert.Equal("A", result[0].Speaker);
        Assert.All(result[0].Words, w => Assert.Equal("A", w.Speaker));
    }

    [Fact]
    public void ASegmentWhoseWordsDoNotReproduceItsTextIsNotCut()
    {
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(6),
            Text = "Hello there — yes, indeed.",
            Words = [Word("hello", 0.2, 0.6), Word("there", 0.7, 1.1), Word("yes", 3.1, 3.4), Word("indeed", 3.5, 4.0)],
        };

        var result = SpeakerAssignment.Apply([segment], [Turn(0, 3, "A"), Turn(3, 6, "B")]);

        Assert.Single(result);
        Assert.Equal("Hello there — yes, indeed.", result[0].Text);
        Assert.Equal("A", result[0].Words[0].Speaker);
        Assert.Equal("B", result[0].Words[3].Speaker);
        // Words are 0.8 s of A and 0.8 s of B; the tie goes to the label that sorts first.
        Assert.Equal("A", result[0].Speaker);
    }

    [Fact]
    public void ASegmentWithoutWordsTakesWhoeverTalksMostDuringIt()
    {
        var segment = new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "no timings" };

        var result = SpeakerAssignment.Apply([segment], [Turn(0, 3, "A"), Turn(3, 10, "B")]);

        Assert.Single(result);
        Assert.Equal("B", result[0].Speaker);
    }

    [Fact]
    public void WhoTalksMostIsSummedOverTheirTurnsNotTheirLongestOne()
    {
        // A real diariser cuts a speaker's turns at every pause, so the speaker with the most
        // speech is often not the one with the single longest turn. B's 4 s turn is the longest;
        // A speaks for 6 s across two.
        var segment = new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "no timings" };

        var result = SpeakerAssignment.Apply([segment], [Turn(0, 3, "A"), Turn(3, 7, "B"), Turn(7, 10, "A")]);

        Assert.Equal("A", result[0].Speaker);
    }

    [Fact]
    public void NoTurnsMeansNothingChanges()
    {
        var segment = new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "x", Words = [Word("x", 0, 1)] };

        var result = SpeakerAssignment.Apply([segment], []);

        Assert.Same(segment, result[0]);
        Assert.Null(result[0].Speaker);
    }
}

public class SpeakerLabellingPipelineTests
{
    [Fact]
    public async Task TheFakeLabellerReadsTheAudioAndTakesTurnsDeterministically()
    {
        var audio = new ArrayAudioSource(TestAudio.Build((10, true)));
        await using var labeller = new FakeSpeakerLabeller();

        var turns = await labeller.LabelAsync(audio, SpeakerLabellingOptions.Default);

        Assert.Equal(160_000, labeller.SamplesRead);
        Assert.Equal(3, turns.Count);                              // 4 s, 4 s, 2 s
        Assert.Equal(["SPEAKER_00", "SPEAKER_01"], SpeakerTurns.Speakers(turns));
        Assert.Equal(TimeSpan.FromSeconds(8), turns[2].Start);
        Assert.Equal(TimeSpan.FromSeconds(10), turns[2].End);

        var again = await labeller.LabelAsync(new ArrayAudioSource(TestAudio.Build((10, true))), SpeakerLabellingOptions.Default);
        Assert.Equal(turns, again);
    }

    [Fact]
    public async Task TheFakeLabellerHonoursASpeakerCountAndAnOverlap()
    {
        var audio = new ArrayAudioSource(TestAudio.Build((12, true)));
        await using var labeller = new FakeSpeakerLabeller(new FakeSpeakerLabellerOptions { Overlap = TimeSpan.FromSeconds(0.5) });

        var turns = await labeller.LabelAsync(audio, new SpeakerLabellingOptions { SpeakerCount = 3 });

        Assert.Equal(["SPEAKER_00", "SPEAKER_01", "SPEAKER_02"], SpeakerTurns.Speakers(turns));
        Assert.Equal(TimeSpan.FromSeconds(4.5), turns[0].End);     // runs half a second into the next
    }

    /// <summary>
    /// A fake that says it cannot be told a count is not told one, and the fold is what honours it.
    /// </summary>
    /// <remarks>
    /// The shipping labeller's arrangement, which is why the fake has to be able to take it: the
    /// model estimates the count, over-segments on a long recording, and the caller's number is
    /// applied afterwards by merging. A stand-in that quietly honoured the count either way would
    /// leave the fold with nothing to fold, so the repair the product depends on would pass its
    /// tests by never running.
    /// </remarks>
    [Fact]
    public async Task AFakeThatCannotBeToldACountLeavesItToTheFold()
    {
        var samples = TestAudio.Build((16, true));
        await using var labeller = new FakeSpeakerLabeller(new FakeSpeakerLabellerOptions
        {
            SpeakerCount = 4,
            Overlap = TimeSpan.FromSeconds(0.5),
            SupportsFixedSpeakerCount = false,
        });

        Assert.False(labeller.Capabilities.SupportsFixedSpeakerCount);

        // Asked for two and it still produces four: the count did not reach it, which is the point.
        var raw = await labeller.LabelAsync(
            new ArrayAudioSource(samples), new SpeakerLabellingOptions { SpeakerCount = 2 });
        Assert.Equal(4, SpeakerTurns.Speakers(raw).Count);

        // Through the driver, the same count comes out honoured — and says which labels it merged.
        await using var engine = new FakeTranscriptionEngine();
        var document = await TranscriptionRunner.RunAsync(
            engine, new ArrayAudioSource(samples), sourceName: "call.wav");

        var labelled = await SpeakerLabelling.LabelAsync(
            document,
            labeller,
            new ArrayAudioSource(samples),
            new SpeakerLabellingOptions { SpeakerCount = 2 });

        Assert.Equal(["Speaker 1", "Speaker 2"], SpeakerTurns.Speakers(labelled.SpeakerTurns));

        // On the document, not into a collection the caller passed in: this is what a saved
        // transcript records, and the command line and the window print it from here too.
        Assert.Equal(2, labelled.RequestedSpeakerCount);
        Assert.Equal(2, labelled.SpeakerFolds.Count);
        Assert.All(
            labelled.SpeakerFolds,
            fold => Assert.Contains("they talked over each other for", fold.Describe(), StringComparison.Ordinal));

        // The fold names the labeller's own cluster ids on both sides, which is a different
        // vocabulary to the display names above — the merge happened before the rename, and the
        // dropped label has no display name because it did not survive to earn one.
        Assert.All(labelled.SpeakerFolds, fold => Assert.StartsWith("SPEAKER_", fold.Dropped, StringComparison.Ordinal));
        Assert.All(labelled.SpeakerFolds, fold => Assert.StartsWith("SPEAKER_", fold.Kept, StringComparison.Ordinal));

        // And out the other end, through the formatter a saved transcript actually goes through.
        // The two halves are covered apart from each other — the fold on the document here, the
        // serialisation in SpeakerFormattingTests — and this is the join between them, which is
        // where the reported failure lived: the fold happened, and the archived file showed no
        // trace of it. Asserted on a document that a labeller produced rather than one built by
        // hand, because a hand-built document cannot demonstrate that the pass fills the field.
        using var json = JsonDocument.Parse(TranscriptFormats.Json.Format(labelled));
        var written = json.RootElement.GetProperty("speakerFolds");

        Assert.Equal(2, written.GetArrayLength());
        Assert.Equal(2, json.RootElement.GetProperty("requestedSpeakerCount").GetInt32());
        Assert.All(
            written.EnumerateArray(),
            fold =>
            {
                Assert.StartsWith("SPEAKER_", fold.GetProperty("from").GetString(), StringComparison.Ordinal);
                Assert.StartsWith("SPEAKER_", fold.GetProperty("into").GetString(), StringComparison.Ordinal);
                Assert.True(fold.GetProperty("overlapSec").GetDouble() >= 0);
            });
    }

    /// <summary>
    /// A count that merged nothing is still recorded, and that is the case the field exists for.
    /// </summary>
    /// <remarks>
    /// The failure this was written against: an archived run made with <c>--speaker-count 2</c>
    /// whose model had already returned two labels folds nothing, and with only the fold list to go
    /// on it is byte-for-byte indistinguishable from a run where no count was given. Those are
    /// different transcripts to judge — one had its label set constrained to a number a human
    /// supplied — so the count travels whether or not it changed the labels.
    /// </remarks>
    [Fact]
    public async Task ACountThatFoldsNothingIsStillRecorded()
    {
        var samples = TestAudio.Build((12, true));
        await using var labeller = new FakeSpeakerLabeller(new FakeSpeakerLabellerOptions { SpeakerCount = 2 });
        await using var engine = new FakeTranscriptionEngine();
        var document = await TranscriptionRunner.RunAsync(
            engine, new ArrayAudioSource(samples), sourceName: "call.wav");

        var counted = await SpeakerLabelling.LabelAsync(
            document, labeller, new ArrayAudioSource(samples), new SpeakerLabellingOptions { SpeakerCount = 2 });

        Assert.Equal(2, counted.RequestedSpeakerCount);
        Assert.Empty(counted.SpeakerFolds);

        // And a run given no count at all says so, rather than reporting the number the labeller
        // happened to find as though somebody had asked for it.
        var uncounted = await SpeakerLabelling.LabelAsync(
            document, labeller, new ArrayAudioSource(samples));

        Assert.Null(uncounted.RequestedSpeakerCount);
        Assert.Empty(uncounted.SpeakerFolds);
    }

    /// <summary>
    /// The fake advertises a cap and then respects it, so the sentence a caller owes at the ceiling
    /// is testable without the real model.
    /// </summary>
    [Fact]
    public async Task AFakeWithACapNeverProducesMoreLabelsThanItAdvertises()
    {
        await using var labeller = new FakeSpeakerLabeller(new FakeSpeakerLabellerOptions
        {
            SpeakerCount = 6,
            TurnLength = TimeSpan.FromSeconds(1),
            MaxSpeakers = 4,
        });

        var turns = await labeller.LabelAsync(
            new ArrayAudioSource(TestAudio.Build((12, true))), SpeakerLabellingOptions.Default);

        Assert.Equal(4, SpeakerTurns.Speakers(turns).Count);
        Assert.Equal(4, labeller.Capabilities.MaxSpeakers);
        Assert.NotNull(SpeakerLabelling.DescribeUnreachableCount(labeller.Capabilities, 7));
    }

    [Fact]
    public async Task LabellingADocumentNamesEveryoneAndCarriesProvenance()
    {
        var samples = TestAudio.Build((0.4, false), (9, true), (0.4, false));
        await using var engine = new FakeTranscriptionEngine();
        var document = await TranscriptionRunner.RunAsync(engine, new ArrayAudioSource(samples), sourceName: "call.wav");
        Assert.False(document.HasSpeakers);

        // A backend the fake does not default to, so that the assertion below distinguishes a
        // provenance read off the loaded labeller from one written into the document by habit.
        await using var labeller = new FakeSpeakerLabeller(new FakeSpeakerLabellerOptions { Backend = ComputeBackend.WebGpu });
        var labelled = await SpeakerLabelling.LabelAsync(document, labeller, new ArrayAudioSource(samples));

        Assert.True(labelled.HasSpeakers);
        Assert.Equal("fake-speakers", labelled.SpeakerModelId);
        Assert.Equal(ComputeBackend.WebGpu, labelled.SpeakerBackend);

        // The ASR provenance is not overwritten by the labeller's, and the two differ here on
        // purpose: one document, transcribed on one provider and labelled on another, which is the
        // arrangement the shipping application actually produces — parakeet.cpp picks cuda or
        // vulkan while the diariser resolves webgpu inside the sidecar.
        Assert.Equal(ComputeBackend.Cpu, labelled.Backend);
        Assert.NotEmpty(labelled.SpeakerTurns);
        Assert.Equal(["Speaker 1", "Speaker 2"], SpeakerTurns.Speakers(labelled.SpeakerTurns));
        Assert.All(labelled.Segments, s => Assert.NotNull(s.Speaker));
        Assert.All(labelled.Segments.SelectMany(s => s.Words), w => Assert.NotNull(w.Speaker));
        Assert.Contains(labelled.Segments, s => s.Speaker == "Speaker 2");
        Assert.All(labelled.Segments, s => Assert.All(s.Words, w => Assert.Equal(s.Speaker, w.Speaker)));

        // The words survived the cut: same words, same order, same times.
        Assert.Equal(
            document.Segments.SelectMany(s => s.Words).Select(w => (w.Text, w.Start)),
            labelled.Segments.SelectMany(s => s.Words).Select(w => (w.Text, w.Start)));
    }

    [Fact]
    public async Task RawLabelsCanBeKeptForScoring()
    {
        var samples = TestAudio.Build((5, true));
        await using var engine = new FakeTranscriptionEngine();
        var document = await TranscriptionRunner.RunAsync(engine, new ArrayAudioSource(samples));

        await using var labeller = new FakeSpeakerLabeller();
        var labelled = await SpeakerLabelling.LabelAsync(
            document, labeller, new ArrayAudioSource(samples), new SpeakerLabellingOptions { DisplayNameFormat = null });

        Assert.Equal("SPEAKER_00", labelled.SpeakerTurns[0].Speaker);
    }

    private static SpeakerLabellerCapabilities Capped(int? max) => new()
    {
        EngineName = "pyannote-torch",
        ModelId = "pyannote-speaker-diarization-community-1",
        SupportsFixedSpeakerCount = false,
        MaxSpeakers = max,
    };

    [Fact]
    public void ACountAboveTheCapIsWarnedAboutBeforeTheRun()
    {
        // The whole point of this message is that it names the user's own number and says it was
        // never on offer. "The value is ignored" does not: a reader takes it to mean the labeller
        // will work the count out for itself, which is exactly what it cannot do past four.
        var warning = SpeakerLabelling.DescribeUnreachableCount(Capped(4), 7);

        Assert.NotNull(warning);
        Assert.Contains("7 speakers were asked for", warning, StringComparison.Ordinal);
        Assert.Contains("at most 4", warning, StringComparison.Ordinal);
        Assert.Contains("never reachable", warning, StringComparison.Ordinal);
        Assert.Contains("pyannote-speaker-diarization-community-1", warning, StringComparison.Ordinal);

        // And it warns rather than refuses, which is a product decision and belongs in the text:
        // somebody who knows they will get four still wants the run.
        Assert.Contains("Continuing", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountAtOrUnderTheCapIsNotWarnedAbout()
    {
        // Four is reachable, so there is nothing to say. A warning that fires on the ordinary case
        // is a warning nobody reads on the case that matters.
        Assert.Null(SpeakerLabelling.DescribeUnreachableCount(Capped(4), 4));
        Assert.Null(SpeakerLabelling.DescribeUnreachableCount(Capped(4), 1));
    }

    [Fact]
    public void WithNoCapOrNoCountThereIsNothingToWarnAbout()
    {
        Assert.Null(SpeakerLabelling.DescribeUnreachableCount(Capped(null), 99));   // the fake has none
        Assert.Null(SpeakerLabelling.DescribeUnreachableCount(Capped(4), null));    // nobody asked
    }

    [Fact]
    public void TheWarningNamesTheEngineWhenTheModelHasNoId()
    {
        // A labeller loaded from a path rather than the catalogue has no model id, and a sentence
        // reading "and can tell apart at most 4" with a hole where the name goes is worse than one
        // naming the engine.
        var warning = SpeakerLabelling.DescribeUnreachableCount(
            Capped(4) with { ModelId = null }, 6);

        Assert.NotNull(warning);
        Assert.Contains("pyannote-torch", warning, StringComparison.Ordinal);
    }

    private static SpeakerTurn T(double start, double end, string speaker) =>
        new() { Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end), Speaker = speaker };

    [Fact]
    public void FoldingIsANoOpWhenTheModelWasAlreadyWithinTheCap()
    {
        // The property that makes this safe to ship against a passed gate. On all 18 AMI dev
        // meetings the model returns four labels and the cap is four, so nothing merges and the DER
        // cannot move. A repair that fires when it is not needed would have to re-earn that gate.
        SpeakerTurn[] turns = [T(0, 5, "A"), T(5, 10, "B"), T(10, 15, "A")];

        Assert.Same(turns, SpeakerTurns.FoldDownTo(turns, 2));
        Assert.Same(turns, SpeakerTurns.FoldDownTo(turns, 3));
    }

    [Fact]
    public void TheLabelsThatNeverTalkOverEachOtherAreTheOnesMerged()
    {
        // The measured failure's shape: one person's identity drifts to a second label partway
        // through, so the two are complementary in time and never simultaneous, while the genuine
        // second speaker collides with both constantly.
        SpeakerTurn[] turns =
        [
            T(0, 10, "drifted-early"), T(5, 12, "the-other-host"),
            T(20, 30, "drifted-early"), T(25, 33, "the-other-host"),
            T(60, 70, "drifted-late"),  T(65, 72, "the-other-host"),
            T(80, 90, "drifted-late"),  T(85, 93, "the-other-host"),
        ];

        var folded = SpeakerTurns.FoldDownTo(turns, 2);
        var speakers = SpeakerTurns.Speakers(folded);

        Assert.Equal(2, speakers.Count);
        Assert.Contains("the-other-host", speakers);

        // The two halves of the drifted speaker are now one, and the host is untouched.
        var driftedSeconds = folded.Where(t => t.Speaker != "the-other-host").Sum(t => t.Duration.TotalSeconds);
        Assert.Equal(40, driftedSeconds, 3);
    }

    [Fact]
    public void TheSurvivingLabelIsTheOneMostOfTheWordsAlreadyHad()
    {
        // Merging renames one label to the other. Picking the larger means the transcript keeps the
        // name most of its speech already carried, rather than renaming an hour of one host to a
        // label that held ninety seconds.
        SpeakerTurn[] turns =
        [
            T(0, 100, "major"), T(200, 210, "minor"), T(50, 60, "someone-else"),
        ];

        var folded = SpeakerTurns.FoldDownTo(turns, 2);

        Assert.Contains("major", SpeakerTurns.Speakers(folded));
        Assert.DoesNotContain("minor", SpeakerTurns.Speakers(folded));
    }

    [Fact]
    public void FoldingKeepsEverySecondOfSpeechAndInventsNone()
    {
        // A merge must not lose or gain speech: the same audio is being relabelled, not re-cut.
        // Overlapping turns of what is now one speaker are merged, so the total can only fall by
        // exactly the overlap the two labels shared — which for a real drift pair is zero.
        SpeakerTurn[] turns =
        [
            T(0, 10, "a"), T(20, 30, "b"), T(40, 50, "c"), T(60, 70, "a"),
        ];

        var folded = SpeakerTurns.FoldDownTo(turns, 2);

        Assert.Equal(40, folded.Sum(t => t.Duration.TotalSeconds), 3);
        Assert.Equal(2, SpeakerTurns.Speakers(folded).Count);
    }

    [Fact]
    public void FoldingIsDeterministic()
    {
        // Two labels can tie on collision — trivially, when several never overlap at all — and a
        // fold that picked differently between runs would make a transcript irreproducible.
        SpeakerTurn[] turns =
        [
            T(0, 10, "a"), T(20, 30, "b"), T(40, 50, "c"), T(60, 70, "d"),
        ];

        var first = SpeakerTurns.Speakers(SpeakerTurns.FoldDownTo(turns, 2));
        var second = SpeakerTurns.Speakers(SpeakerTurns.FoldDownTo(turns, 2));

        Assert.Equal(first, second);
        Assert.Equal(2, first.Count);
    }

    [Fact]
    public void EachMergeIsReportedWithTheEvidenceForIt()
    {
        // The overlap seconds are the merge's own evidence and a caller owes them to the user. Near
        // zero is the signature this repair exists for; a large number means the pair really did
        // converse, the merge still happens because the count wins, and nobody should be told it
        // was well founded.
        SpeakerTurn[] drifted =
        [
            T(0, 10, "early"), T(60, 70, "late"), T(5, 12, "host"), T(65, 72, "host"),
        ];

        SpeakerTurns.FoldDownTo(drifted, 2, out var merges);
        var reported = Assert.Single(merges);

        // Which side is which, which the sentence alone could not pin: the pair tie on speech, so
        // the survivor is the one that appeared first, and 'late' is the label that goes away.
        Assert.Equal("late", reported.Dropped);
        Assert.Equal("early", reported.Kept);
        Assert.Equal(0, reported.OverlapSeconds);
        Assert.Contains("'early'", reported.Describe(), StringComparison.Ordinal);
        Assert.Contains("0.0 s", reported.Describe(), StringComparison.Ordinal);

        // The numbers are carried as numbers rather than only inside the sentence, because a saved
        // transcript is queried rather than read: "which archived runs merged a pair that overlapped
        // for more than a minute" is not a question a formatted string answers.
        Assert.NotNull(reported.RunnerUpSeconds);
        Assert.True(reported.RunnerUpSeconds > 0);

        // And when the least-colliding pair is not actually complementary, the number says so
        // rather than the message pretending the merge was clean.
        SpeakerTurn[] conversing =
        [
            T(0, 10, "a"), T(5, 15, "b"), T(20, 30, "a"), T(25, 35, "b"), T(50, 55, "c"),
        ];

        SpeakerTurns.FoldDownTo(conversing, 2, out var loud);
        Assert.NotEmpty(loud);
        Assert.All(loud, m => Assert.Contains("talked over each other for", m.Describe(), StringComparison.Ordinal));

        // No fold, nothing reported.
        SpeakerTurns.FoldDownTo(drifted, 3, out var none);
        Assert.Empty(none);
    }

    /// <summary>
    /// The margin is rendered from the two seconds figures, including when there is no runner-up.
    /// </summary>
    /// <remarks>
    /// Folding two labels into one leaves nothing to compare the merge with, and the sentence has
    /// to say that rather than divide by a number it does not have. The ratio is not stored beside
    /// the seconds for the same reason no derived figure is: it is recomputable from both, and a
    /// stored copy is a second version of one fact.
    /// </remarks>
    [Fact]
    public void AfterAFoldTheSurvivorsOverlapIsCountedOnceRatherThanOncePerTurn()
    {
        // a[0,10] b[5,15] c[7,12], folded to one. The first merge takes (a, c), the least-colliding
        // pair, and relabels c as a — which leaves a[0,10] and a[7,12] overlapping each other, and
        // both overlapping b over [7,10]. Summed per turn, the second merge's evidence read 10 s of
        // a-against-b where the union is 7 s; until 2026-08-22 the relabelled turns were coalesced
        // only after the last fold.
        SpeakerTurn[] turns = [T(0, 10, "a"), T(5, 15, "b"), T(7, 12, "c")];

        var folded = SpeakerTurns.FoldDownTo(turns, 1, out var merges);

        Assert.Equal(2, merges.Count);
        Assert.Equal("c", merges[0].Dropped);
        Assert.Equal(3, merges[0].OverlapSeconds, 6);
        Assert.Equal(7, merges[1].OverlapSeconds, 6);
        Assert.Single(SpeakerTurns.Speakers(folded));
    }

    [Fact]
    public void AMergeWithNoOtherPairSaysSoRatherThanQuotingAMargin()
    {
        SpeakerTurn[] two = [T(0, 10, "a"), T(20, 30, "b")];

        SpeakerTurns.FoldDownTo(two, 1, out var merges);
        var only = Assert.Single(merges);

        Assert.Null(only.RunnerUpSeconds);
        Assert.Contains("no other pair to compare it with", only.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("x more", only.Describe(), StringComparison.Ordinal);

        // With a runner-up and a non-zero overlap, the ratio is what the sentence leads on — the
        // absolute alone says nothing about whether the fold had a real choice to make.
        SpeakerTurn[] three = [T(0, 10, "a"), T(5, 15, "b"), T(30, 40, "c"), T(31, 39, "a")];

        SpeakerTurns.FoldDownTo(three, 2, out var withMargin);
        Assert.All(withMargin, m => Assert.Contains("the next-closest pair overlapped", m.Describe(), StringComparison.Ordinal));
    }

    [Fact]
    public void ACountAboveWhatWasFoundChangesNothing()
    {
        // Folding only ever reduces. Asking for seven when the labeller found four cannot conjure
        // three more, which is what the cap warning says out loud before the run.
        SpeakerTurn[] turns = [T(0, 5, "A"), T(5, 10, "B")];

        Assert.Same(turns, SpeakerTurns.FoldDownTo(turns, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeakerTurns.FoldDownTo(turns, 0));
    }

    [Fact]
    public void ARecordingPastWhereTheEvidenceStopsIsWarnedAbout()
    {
        var capped = Capped(4) with { ReliableUpTo = TimeSpan.FromHours(1) };

        // Inside the bound, nothing to say.
        Assert.Null(SpeakerLabelling.DescribeDurationRisk(capped, TimeSpan.FromMinutes(32)));
        Assert.Null(SpeakerLabelling.DescribeDurationRisk(capped, TimeSpan.FromHours(1)));

        // Past it, the sentence warns without alarming: the labels are a guess, and the words are
        // untouched by whatever the labels do.
        var warning = SpeakerLabelling.DescribeDurationRisk(capped, TimeSpan.FromMinutes(175));
        Assert.NotNull(warning);
        Assert.Contains("175 minutes", warning, StringComparison.Ordinal);
        Assert.Contains("treat the names as a guess", warning, StringComparison.Ordinal);
        Assert.Contains("the words are unaffected", warning, StringComparison.Ordinal);

        // No bound measured, or no duration known, is silence rather than a guess.
        Assert.Null(SpeakerLabelling.DescribeDurationRisk(Capped(4), TimeSpan.FromHours(5)));
        Assert.Null(SpeakerLabelling.DescribeDurationRisk(capped, null));
    }

    [Fact]
    public void TheTwoLimitsAreDifferentShapesAndSayDifferentThings()
    {
        // The cap is architectural — in the model's geometry, the same on every file, knowable
        // without running anything. The duration bound is empirical — where the scoring stopped.
        // A seven-speaker request on a three-hour file is owed both sentences, and conflating them
        // would let a caller report one and believe it had reported the other.
        var capabilities = Capped(4) with { ReliableUpTo = TimeSpan.FromHours(1) };

        var cap = SpeakerLabelling.DescribeUnreachableCount(capabilities, 7);
        var length = SpeakerLabelling.DescribeDurationRisk(capabilities, TimeSpan.FromHours(3));

        Assert.NotNull(cap);
        Assert.NotNull(length);
        Assert.NotEqual(cap, length);
        Assert.Contains("never reachable", cap, StringComparison.Ordinal);
        Assert.DoesNotContain("never reachable", length, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBeforeAndAfterMessagesSayDifferentThings()
    {
        // DescribeLimit reports what happened; DescribeUnreachableCount reports what cannot happen.
        // If they ever collapse into the same sentence, the before-the-run half has stopped earning
        // its place — "4 speakers were labelled" is a fact about the recording and reads as one.
        var labeller = new FakeSpeakerLabeller();
        var document = new TranscriptDocument
        {
            SourceName = "meeting.wav",
            Segments = [],
            SpeakerTurns =
            [
                new SpeakerTurn { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Speaker = "SPEAKER_00" },
            ],
        };

        // The fake has no cap, so the after-the-run half stays silent on it.
        Assert.Null(SpeakerLabelling.DescribeLimit(labeller, document));

        var before = SpeakerLabelling.DescribeUnreachableCount(Capped(4), 7);
        Assert.NotNull(before);
        Assert.DoesNotContain("were labelled", before, StringComparison.Ordinal);
    }
}

public class SpeakerFormattingTests
{
    private static TranscriptWord Word(string text, double start, double end, string? speaker) =>
        new() { Text = text, Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end), Speaker = speaker };

    private static TranscriptDocument Labelled() => new()
    {
        SourceName = "two hosts.mp3",
        SpeakerModelId = "fake-speakers",
        SpeakerBackend = ComputeBackend.WebGpu,

        // Two turns from a labeller that found three: the arrangement --speaker-count exists for,
        // where one voice drifted onto a second label and the user's number merged it back. The
        // fold's labels are the labeller's cluster ids, which the turns above no longer carry —
        // renaming runs after the merge, and a label that was merged away never earns a name.
        RequestedSpeakerCount = 2,
        SpeakerFolds =
        [
            new SpeakerFold
            {
                Dropped = "SPEAKER_02",
                Kept = "SPEAKER_00",
                OverlapSeconds = 0.4,
                RunnerUpSeconds = 57.6,
            },
        ],
        SpeakerTurns =
        [
            new SpeakerTurn { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2.6), Speaker = "Speaker 1" },
            new SpeakerTurn { Start = TimeSpan.FromSeconds(2.6), End = TimeSpan.FromSeconds(8), Speaker = "Speaker 2" },
        ],
        Segments =
        [
            new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(0.5),
                End = TimeSpan.FromSeconds(2.6),
                Text = "first thing we should do",
                Speaker = "Speaker 1",
                Words =
                [
                    Word("first", 0.5, 0.9, "Speaker 1"), Word("thing", 0.9, 1.4, "Speaker 1"), Word("we", 1.4, 1.7, "Speaker 1"),
                    Word("should", 1.7, 2.2, "Speaker 1"), Word("do", 2.2, 2.6, "Speaker 1"),
                ],
            },
            new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(5),
                End = TimeSpan.FromSeconds(8),
                Text = "and then the second",
                Speaker = "Speaker 2",
            },
        ],
    };

    [Fact]
    public void PlainTextAndMarkdownNameTheSpeaker()
    {
        var text = TranscriptFormats.PlainText.Format(Labelled());
        Assert.Contains("[00:00:00] Speaker 1: first thing we should do\n", text, StringComparison.Ordinal);
        Assert.Contains("[00:00:05] Speaker 2: and then the second\n", text, StringComparison.Ordinal);

        var markdown = TranscriptFormats.Markdown.Format(Labelled());
        Assert.Contains("**[00:00:00]** **Speaker 1:** first thing we should do", markdown, StringComparison.Ordinal);
        Assert.Contains("| Speaker labels | fake-speakers |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Speaker backend | webgpu |", markdown, StringComparison.Ordinal);

        // The count a human supplied and what honouring it did to the labels — the same sentence
        // the command line prints, from the one place that builds it.
        Assert.Contains("| Speaker count requested | 2 |", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "| Speaker folds | 'SPEAKER_02' into 'SPEAKER_00' (they talked over each other for 0.4 s; "
            + "the next-closest pair overlapped 57.6 s, 144.0x more) |",
            markdown,
            StringComparison.Ordinal);
    }

    [Fact]
    public void JsonCarriesSpeakersOnSegmentsWordsAndAsTurns()
    {
        using var json = JsonDocument.Parse(TranscriptFormats.Json.Format(Labelled()));
        var root = json.RootElement;

        Assert.Equal("fake-speakers", root.GetProperty("speakerModel").GetString());
        Assert.Equal("webgpu", root.GetProperty("speakerBackend").GetString());
        Assert.Equal("Speaker 1", root.GetProperty("segments")[0].GetProperty("speaker").GetString());
        Assert.Equal("Speaker 1", root.GetProperty("segments")[0].GetProperty("words")[0].GetProperty("speaker").GetString());
        Assert.Equal("Speaker 2", root.GetProperty("segments")[1].GetProperty("speaker").GetString());
        var turns = root.GetProperty("speakerTurns");
        Assert.Equal(2, turns.GetArrayLength());
        Assert.Equal(2.6, turns[1].GetProperty("start").GetDouble());
    }

    /// <summary>
    /// The requested count and the merges it forced travel in the JSON provenance.
    /// </summary>
    /// <remarks>
    /// Written against a real gap: on <c>two-hosts-new-episode.2026-08-20-csharp-diariser.json</c>,
    /// archived from a run made with <c>--speaker-count 2</c>, nothing in the file recorded either
    /// the count or the merge it forced. The fold was printed to the terminal and lost with it, so
    /// a transcript whose labels had been edited after the model was done looked exactly like one
    /// that had not — and the numbers a reader would judge the edit by existed nowhere.
    /// </remarks>
    [Fact]
    public void JsonRecordsTheRequestedCountAndEveryFoldItForced()
    {
        using var json = JsonDocument.Parse(TranscriptFormats.Json.Format(Labelled()));
        var root = json.RootElement;

        Assert.Equal(2, root.GetProperty("requestedSpeakerCount").GetInt32());

        var folds = root.GetProperty("speakerFolds");
        var fold = Assert.Single(folds.EnumerateArray());
        Assert.Equal("SPEAKER_02", fold.GetProperty("from").GetString());
        Assert.Equal("SPEAKER_00", fold.GetProperty("into").GetString());

        // Numbers, not the sentence: an archived run is queried, and "which of these merged a pair
        // that overlapped for more than a minute" is not a question prose answers.
        Assert.Equal(0.4, fold.GetProperty("overlapSec").GetDouble());
        Assert.Equal(57.6, fold.GetProperty("runnerUpSec").GetDouble());
    }

    /// <summary>
    /// A count honoured without merging anything is still recorded, and is what tells the two runs
    /// apart.
    /// </summary>
    [Fact]
    public void JsonKeepsTheRequestedCountEvenWhenNothingWasFolded()
    {
        var unfolded = TranscriptFormats.Json.Format(Labelled() with { SpeakerFolds = [] });
        using var json = JsonDocument.Parse(unfolded);

        Assert.Equal(2, json.RootElement.GetProperty("requestedSpeakerCount").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("speakerFolds", out _));

        // And a labelled run nobody gave a count to carries neither, rather than reporting the
        // number the labeller happened to find as though it had been asked for.
        var uncounted = TranscriptFormats.Json.Format(
            Labelled() with { RequestedSpeakerCount = null, SpeakerFolds = [] });
        using var without = JsonDocument.Parse(uncounted);

        Assert.False(without.RootElement.TryGetProperty("requestedSpeakerCount", out _));
        Assert.False(without.RootElement.TryGetProperty("speakerFolds", out _));
    }

    /// <summary>
    /// A merge with nothing to compare it against says so in JSON as well as in the sentence.
    /// </summary>
    /// <remarks>
    /// Null rather than an absent key: a reader deciding whether a fold was well founded needs to
    /// tell "there was no other pair" from "this field was not written", and those are the same
    /// thing to a consumer that only checks for presence.
    /// </remarks>
    [Fact]
    public void JsonWritesANullMarginWhenThereWasNoOtherPair()
    {
        var lonely = Labelled() with
        {
            RequestedSpeakerCount = 1,
            SpeakerFolds =
            [
                new SpeakerFold { Dropped = "SPEAKER_01", Kept = "SPEAKER_00", OverlapSeconds = 12.25 },
            ],
        };

        using var json = JsonDocument.Parse(TranscriptFormats.Json.Format(lonely));
        var fold = Assert.Single(json.RootElement.GetProperty("speakerFolds").EnumerateArray());

        Assert.Equal(12.25, fold.GetProperty("overlapSec").GetDouble());
        Assert.Equal(JsonValueKind.Null, fold.GetProperty("runnerUpSec").ValueKind);
    }

    [Fact]
    public void AnUnlabelledDocumentSerialisesWithoutAnySpeakerField()
    {
        var unlabelled = Labelled() with
        {
            SpeakerModelId = null,
            SpeakerBackend = null,
            RequestedSpeakerCount = null,
            SpeakerFolds = [],
            SpeakerTurns = [],
            Segments = [.. Labelled().Segments.Select(s => s with { Speaker = null, Words = [.. s.Words.Select(w => w with { Speaker = null })] })],
        };

        var json = TranscriptFormats.Json.Format(unlabelled);

        Assert.DoesNotContain("speaker", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Speaker", TranscriptFormats.PlainText.Format(unlabelled), StringComparison.Ordinal);
        Assert.DoesNotContain("Speaker", TranscriptFormats.Srt.Format(unlabelled), StringComparison.Ordinal);
        Assert.Equal(string.Empty, TranscriptFormats.Rttm.Format(unlabelled));
    }

    [Fact]
    public void SubtitlesPrefixTheFirstLineOfEachCueOnce()
    {
        var srt = TranscriptFormats.Srt.Format(Labelled());
        Assert.Contains("00:00:00,500 --> 00:00:02,600\nSpeaker 1: first thing we should do\n", srt, StringComparison.Ordinal);
        Assert.Contains("\nSpeaker 2: and then the second\n", srt, StringComparison.Ordinal);

        var vtt = TranscriptFormats.Vtt.Format(Labelled());
        Assert.Contains("00:00:00.500 --> 00:00:02.600\nSpeaker 1: first thing we should do\n", vtt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWordTimedVttKeepsItsInvariantWithSpeakers()
    {
        var timed = TranscriptFormats.WordTimedVtt.Format(Labelled());
        var plain = TranscriptFormats.Vtt.Format(Labelled());

        Assert.Contains("Speaker 1: <c>first</c><00:00:00.900> <c>thing</c>", timed, StringComparison.Ordinal);
        Assert.Equal(plain, System.Text.RegularExpressions.Regex.Replace(timed, "<[^>]*>", string.Empty));
    }

    [Fact]
    public void RttmWritesTheTurnsWithTheSourceStemAsFileIdAndNoWhitespaceInLabels()
    {
        var rttm = TranscriptFormats.Rttm.Format(Labelled());

        // "two hosts.mp3" → file id two_hosts; "Speaker 1" → Speaker_1: RTTM splits on whitespace.
        Assert.Equal(
            "SPEAKER two_hosts 1 0.000 2.600 <NA> <NA> Speaker_1 <NA> <NA>\n" +
            "SPEAKER two_hosts 1 2.600 5.400 <NA> <NA> Speaker_2 <NA> <NA>\n",
            rttm);
        Assert.Equal("rttm", TranscriptFormats.Rttm.Id);
        Assert.Contains(TranscriptFormats.Rttm, TranscriptFormats.All);
    }

    [Fact]
    public void ACueNeverSpansASpeakerChange()
    {
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(4),
            Text = "yes no yes no",
            Words = [Word("yes", 0, 0.5, "A"), Word("no", 0.6, 1.0, "B"), Word("yes", 1.1, 1.5, "A"), Word("no", 1.6, 2.0, "B")],
        };

        var cues = SubtitleCueBuilder.Build([segment]);

        Assert.Equal(4, cues.Count);
        Assert.Equal(["A", "B", "A", "B"], cues.Select(c => c.Speaker));
        Assert.All(cues, c => Assert.Single(c.Words));
    }

    [Fact]
    public void TheTailRebalanceDoesNotMoveWordsAcrossASpeakerChange()
    {
        // A long run by A followed by one short word by B: the widow rule would normally pull words
        // into the last cue. It must not, because the last cue is somebody else's.
        var words = new List<TranscriptWord>();
        var t = 0.0;
        for (var i = 0; i < 20; i++)
        {
            words.Add(Word("something", t, t + 0.3, "A"));
            t += 0.35;
        }

        words.Add(Word("ok", t, t + 0.2, "B"));

        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(t + 1),
            Text = string.Join(' ', words.Select(w => w.Text)),
            Words = words,
        };

        var cues = SubtitleCueBuilder.Build([segment]);

        Assert.Equal("B", cues[^1].Speaker);
        Assert.Single(cues[^1].Words);
        Assert.All(cues.Take(cues.Count - 1), c => Assert.Equal("A", c.Speaker));
    }

    [Fact]
    public void TheWidowRuleStillAppliesWithinEachSpeakersRun()
    {
        // A long run by A, then one short word by B, then a long run by A with a widow at its end.
        // The last run's widow is rebalanced even though it is not the segment's last cue, and
        // nothing is traded across either speaker change.
        var words = new List<TranscriptWord>();
        var t = 0.0;

        void Say(string speaker, int count)
        {
            for (var i = 0; i < count; i++)
            {
                words.Add(Word("something", t, t + 0.3, speaker));
                t += 0.35;
            }
        }

        Say("A", 20);
        words.Add(Word("ok", t, t + 0.2, "B"));
        t += 0.25;
        Say("A", 12);

        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(t + 1),
            Text = string.Join(' ', words.Select(w => w.Text)),
            Words = words,
        };

        var cues = SubtitleCueBuilder.Build([segment]);

        Assert.All(cues, c => Assert.Single(c.Lines.Count == 0 ? [string.Empty] : new[] { c.Speaker! }));
        Assert.Equal("B", cues.Single(c => c.Speaker == "B").Speaker);
        // Every A cue after the B one carries more than a stranded word.
        var trailing = cues.SkipWhile(c => c.Speaker != "B").Skip(1).ToList();
        Assert.NotEmpty(trailing);
        Assert.All(trailing, c => Assert.True(
            c.Words.Sum(w => w.Text.Trim().Length + 1) - 1 >= SubtitleOptions.Default.MinTailCharacters,
            $"cue '{c.Text}' is a widow the rebalance should have absorbed"));
    }

    [Fact]
    public void ThePrefixIsChargedAgainstTheCuesCapacity()
    {
        var options = SubtitleOptions.Default;
        Assert.Equal(options.Capacity, options.CapacityFor(null));
        Assert.Equal(options.Capacity - "Speaker 1: ".Length, options.CapacityFor("Speaker 1"));
        Assert.Equal(options.MaxLineLength, options.CapacityFor(new string('x', 200)));   // never below one line
    }
}
