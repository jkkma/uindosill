using Parakeet.Core.Tidying;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

/// <summary>
/// The unit one request carries (docs/PHASES.md, *Decided 2026-09-02, late evening*): how the
/// shaper cuts the stream, how the contract judges a unit and cuts it back to its pieces, and
/// how the stage lands one outcome per segment whatever the unit.
/// </summary>
public class TidyUnitTests
{
    /// <summary>One word per second from <paramref name="startSec"/>; the text is the words joined.</summary>
    private static TranscriptSegment Timed(int index, double startSec, string text, float confidence = 0.9f)
    {
        var tokens = text.Split(' ');
        var words = new List<TranscriptWord>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            words.Add(new TranscriptWord
            {
                Text = tokens[i],
                Start = TimeSpan.FromSeconds(startSec + i),
                End = TimeSpan.FromSeconds(startSec + i + 1),
                Confidence = confidence,
            });
        }

        return new TranscriptSegment
        {
            Start = TimeSpan.FromSeconds(startSec),
            End = TimeSpan.FromSeconds(startSec + tokens.Length),
            Text = text,
            Words = words,
            SourceSegmentIndex = index,
        };
    }

    private static TidyPiece Whole(int index, TranscriptSegment segment) => new(index, segment, 0, segment.Words.Count, 0);

    [Fact]
    public void TheSegmentKindSendsEachSegmentAloneAndAnUnknownKindIsRefused()
    {
        var shaper = new TidyUnitShaper(TidyUnitKind.Segment);

        var first = shaper.Push(0, Timed(0, 0, "one two"), out var pieces);
        Assert.Equal(1, pieces);
        var unit = Assert.Single(first);
        Assert.True(unit.IsSingleWholeSegment);
        Assert.Equal("one two", unit.Composite.Text);

        Assert.Single(shaper.Push(1, Timed(1, 2, "three four"), out _));
        Assert.Empty(shaper.Flush());

        Assert.Throws<ArgumentOutOfRangeException>(() => new TidyOptions { Unit = (TidyUnitKind)9 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TidyOptions { Shape = (TidyShape)9 }.Validate());
    }

    [Fact]
    public void AJoinedRunClosesAtFifteenSecondsOfSpeechAndTheTailIsFlushed()
    {
        var shaper = new TidyUnitShaper(TidyUnitKind.JoinedRun);
        var segments = Enumerable.Range(0, 5).Select(i => Timed(i, i * 6, $"s{i} a b c d e")).ToList();

        Assert.Empty(shaper.Push(0, segments[0], out var pieces));
        Assert.Equal(1, pieces);
        Assert.Empty(shaper.Push(1, segments[1], out _));

        // 18 s of speech at the third segment: the run closes with all three.
        var unit = Assert.Single(shaper.Push(2, segments[2], out _));
        Assert.Equal(3, unit.Pieces.Count);
        Assert.Equal(TimeSpan.FromSeconds(18), unit.Speech);
        Assert.Equal(TimeSpan.Zero, unit.Composite.Start);
        Assert.Equal(TimeSpan.FromSeconds(18), unit.Composite.End);
        Assert.Equal("s0 a b c d e s1 a b c d e s2 a b c d e", unit.Composite.Text);
        Assert.True(unit.Composite.WordsReproduceText());
        Assert.Equal(18, unit.WordCount);

        Assert.Empty(shaper.Push(3, segments[3], out _));
        Assert.Empty(shaper.Push(4, segments[4], out _));
        var tail = Assert.Single(shaper.Flush());
        Assert.Equal(2, tail.Pieces.Count);
        Assert.Equal([3, 4], tail.Pieces.Select(p => p.Index));
        Assert.Empty(shaper.Flush());
    }

    [Fact]
    public void ASentenceRunIsCutInsideASegmentAndJoinedAcrossOne()
    {
        var shaper = new TidyUnitShaper(TidyUnitKind.SentenceRun);

        // "well." before "Then" ends a sentence; "the" before "numbers" does not.
        var first = shaper.Push(0, Timed(0, 0, "we did well. Then the"), out var pieces);
        Assert.Equal(2, pieces);
        var unit = Assert.Single(first);
        Assert.Equal("we did well.", unit.Composite.Text);
        Assert.False(unit.IsSingleWholeSegment);
        Assert.Equal(TimeSpan.Zero, unit.Composite.Start);
        Assert.Equal(TimeSpan.FromSeconds(3), unit.Composite.End);

        var second = shaper.Push(1, Timed(1, 5, "numbers came in. And"), out pieces);
        Assert.Equal(2, pieces);
        unit = Assert.Single(second);
        Assert.Equal("Then the numbers came in.", unit.Composite.Text);
        Assert.Equal(2, unit.Pieces.Count);
        Assert.Equal((0, 3, 2), (unit.Pieces[0].Index, unit.Pieces[0].WordStart, unit.Pieces[0].WordCount));
        Assert.Equal((1, 0, 3), (unit.Pieces[1].Index, unit.Pieces[1].WordStart, unit.Pieces[1].WordCount));
        Assert.Equal(TimeSpan.FromSeconds(3), unit.Composite.Start);
        Assert.Equal(TimeSpan.FromSeconds(8), unit.Composite.End);
        Assert.Equal(TimeSpan.FromSeconds(5), unit.Speech);

        var tail = Assert.Single(shaper.Flush());
        Assert.Equal("And", tail.Composite.Text);
        Assert.Equal(1, tail.Pieces[0].Ordinal);
    }

    [Fact]
    public void ASentenceRunWaitsForTheNextSegmentToSayWhetherTheLastWordEndedOne()
    {
        var shaper = new TidyUnitShaper(TidyUnitKind.SentenceRun);

        Assert.Empty(shaper.Push(0, Timed(0, 0, "we did well."), out var pieces));
        Assert.Equal(1, pieces);

        // "Then" opens a sentence, so the one before it closes on arrival.
        var unit = Assert.Single(shaper.Push(1, Timed(1, 4, "Then we rested."), out pieces));
        Assert.Equal(1, pieces);
        Assert.Equal("we did well.", unit.Composite.Text);
        Assert.True(unit.IsSingleWholeSegment);

        var tail = Assert.Single(shaper.Flush());
        Assert.Equal("Then we rested.", tail.Composite.Text);
    }

    [Fact]
    public void ASentenceRunClosesOnTheCapWhenNoSentenceEnds()
    {
        var shaper = new TidyUnitShaper(TidyUnitKind.SentenceRun);
        var text = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"w{i}"));

        var unit = Assert.Single(shaper.Push(0, Timed(0, 0, text), out var pieces));
        Assert.Equal(2, pieces);
        Assert.Equal(30, unit.WordCount);
        Assert.Equal(TimeSpan.FromSeconds(30), unit.Speech);

        var tail = Assert.Single(shaper.Flush());
        Assert.Equal(10, tail.WordCount);
        Assert.Equal(30, tail.Pieces[0].WordStart);
    }

    [Fact]
    public void AnUntimedSegmentTravelsAloneAndClosesTheOpenRun()
    {
        var shaper = new TidyUnitShaper(TidyUnitKind.JoinedRun);
        var timed = Timed(0, 0, "a b c d e f");
        var untimed = new TranscriptSegment
        {
            Start = TimeSpan.FromSeconds(6),
            End = TimeSpan.FromSeconds(8),
            Text = "hello there",
            SourceSegmentIndex = 1,
        };

        Assert.Empty(shaper.Push(0, timed, out _));
        var units = shaper.Push(1, untimed, out var pieces);

        Assert.Equal(1, pieces);
        Assert.Equal(2, units.Count);
        Assert.Same(timed, units[0].Composite);
        Assert.Same(untimed, units[1].Composite);
        Assert.Equal(2, units[1].WordCount);
        Assert.Empty(shaper.Flush());
    }

    [Fact]
    public void AUnitOfTwoSegmentsIsJudgedAsOneLineAndCutBackToEach()
    {
        var first = Timed(0, 0, "um so the cat");
        var second = Timed(1, 4, "sat sat on the mat");
        var unit = TidyUnit.Of([Whole(0, first), Whole(1, second)]);

        var outcomes = TidyContract.Apply(unit, "So the cat sat on the mat.", 0.45f);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Accepted, o.Refusal));

        Assert.Equal("So the cat", outcomes[0].Text);
        Assert.Equal(3, outcomes[0].Words.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), outcomes[0].Words[0].Start);
        Assert.Equal(0, outcomes[0].DeletedWords);

        Assert.Equal("sat on the mat.", outcomes[1].Text);
        Assert.Equal(4, outcomes[1].Words.Count);
        Assert.All(outcomes[1].Words, w => Assert.InRange(w.Start, second.Start, second.End));
        Assert.Equal(1, outcomes[1].DeletedWords);
        Assert.True(TranscriptSegment.WordsReproduceText(outcomes[1].Words, outcomes[1].Text));
    }

    [Fact]
    public void AnInsertionRefusesEveryPieceOfTheUnit()
    {
        var unit = TidyUnit.Of([Whole(0, Timed(0, 0, "um so the cat")), Whole(1, Timed(1, 4, "sat on the mat"))]);

        var outcomes = TidyContract.Apply(unit, "So the cat sat on the big mat.", 0.45f);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o =>
        {
            Assert.False(o.Accepted);
            Assert.Contains("added 'big'", o.Refusal, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void APieceThatTidiesToNothingInsideANonEmptyRunRefusesTheUnit()
    {
        // The failure this ceiling was added for, from a real call: the run as a whole came back
        // with words, so the empty-rewrite guard — which reads the composite — passed, while one
        // line inside it went entirely. Accepted, the line left the transcript empty and the
        // window's line list drops an empty segment, so it vanished with nothing refused.
        var unit = TidyUnit.Of(
        [
            Whole(0, Timed(0, 0, "the cat sat on the mat")),
            Whole(1, Timed(1, 6, "other thing that just happened")),
        ]);

        var outcomes = TidyContract.Apply(unit, "The cat sat on the mat.", 0.45f);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o =>
        {
            Assert.False(o.Accepted);
            Assert.Contains("of the line's 5 spoken words", o.Refusal, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AClauseLiftedOutOfOnePieceRefusesTheUnitThoughTheRunKeepsMostOfItsWords()
    {
        // The other half of the same call: a clause taken out of the middle of one line. The
        // fraction alone does not catch it — the unit keeps most of its words, and so does the
        // line — but five spoken words in a row are gone, which no stutter does.
        var unit = TidyUnit.Of(
        [
            Whole(0, Timed(0, 0, "basically said we anyone who passes away we own everything")),
            Whole(1, Timed(1, 11, "on that account")),
        ]);

        var outcomes = TidyContract.Apply(unit, "Basically said we own everything on that account.", 0.45f);

        Assert.All(outcomes, o =>
        {
            Assert.False(o.Accepted);
            Assert.Contains("5 spoken words in a row", o.Refusal, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AScatteredStutterInsideARunIsStillTidiedAwayUnderBothCeilings()
    {
        // The ceilings must not cost the tidy what it is for. Four separate repetitions go, no
        // run reaches five and no line loses half of itself, so the unit is accepted.
        var unit = TidyUnit.Of(
        [
            Whole(0, Timed(0, 0, "i i think that the the design")),
            Whole(1, Timed(1, 7, "forcing you to to show the the character")),
        ]);

        var outcomes = TidyContract.Apply(unit, "I think that the design forcing you to show the character.", 0.45f);

        Assert.All(outcomes, o => Assert.True(o.Accepted, o.Refusal));
        Assert.Equal("I think that the design", outcomes[0].Text);
        Assert.Equal("forcing you to show the character.", outcomes[1].Text);
    }

    [Fact]
    public void AHyphenationAcrossTheJoinRefusesTheUnit()
    {
        // "micro" ends one segment and "behaviors" opens the next; the normaliser splits the
        // hyphenation into both, on either side of the join. (A number would not do here: number
        // words are rendered as digits before the hyphen rule sees them.)
        var unit = TidyUnit.Of([Whole(0, Timed(0, 0, "we saw micro")), Whole(1, Timed(1, 3, "behaviors matter"))]);

        var outcomes = TidyContract.Apply(unit, "We saw micro-behaviors matter.", 0.45f);

        Assert.All(outcomes, o =>
        {
            Assert.False(o.Accepted);
            Assert.True(o.Refusal?.Contains("across a line break", StringComparison.Ordinal) == true, o.Refusal);
        });
    }

    [Fact]
    public void AWordThroughTheDoorIsIndexedIntoItsOwnSegment()
    {
        var first = Timed(0, 0, "a b c");
        var second = Timed(1, 3, "d ex f");
        second = second with { Words = second.Words.Select((w, i) => i == 1 ? w with { Confidence = 0.2f } : w).ToList() };
        var unit = TidyUnit.Of([Whole(0, first), Whole(1, second)]);

        var outcomes = TidyContract.Apply(unit, "A b c d ee f", 0.45f);

        Assert.All(outcomes, o => Assert.True(o.Accepted, o.Refusal));
        Assert.Empty(outcomes[0].Replacements);
        var replacement = Assert.Single(outcomes[1].Replacements);
        Assert.Equal((1, "ex", "ee"), (replacement.SpokenWordIndex, replacement.Spoken, replacement.Replacement));
        Assert.Equal("ex", outcomes[1].Words[1].ReplacedFrom);
        Assert.Equal("A b c", outcomes[0].Text);
    }

    [Fact]
    public async Task TheStageLandsOneOutcomePerSegmentUnderJoinedRunsAndTracesEveryRequest()
    {
        await using var tidier = new FakeTranscriptTidier();
        var landed = new List<int>();
        var gate = new Lock();
        await using var stage = new TidyStage(
            tidier,
            new TidyOptions { Unit = TidyUnitKind.JoinedRun, Concurrency = 1 },
            (index, _) =>
            {
                lock (gate)
                {
                    landed.Add(index);
                }
            });

        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(i, stage.Enqueue(Timed(i, i * 6, $"um line {i} {i} is here")));
        }

        var outcomes = await stage.CompleteAsync();

        Assert.Equal(7, outcomes.Count);
        Assert.Equal(0, stage.Pending);
        Assert.All(outcomes, o => Assert.True(o.Accepted, o.Refusal));
        Assert.All(outcomes, o => Assert.True(o.Segment.WordsReproduceText()));

        // Three requests for seven segments: two runs of three and the flushed tail.
        Assert.Equal(3, stage.Units);
        Assert.Equal(3, tidier.Lines.Count);
        Assert.Equal("Line 0 is here", outcomes[0].Segment.Text);
        Assert.Equal("line 1 is here", outcomes[1].Segment.Text);
        Assert.Equal(
            string.Join(' ', tidier.Lines.Select(tidier.Tidy)),
            string.Join(' ', outcomes.Select(o => o.Segment.Text)));

        Assert.Equal(Enumerable.Range(0, 7), landed.Order());

        var trace = stage.Trace;
        Assert.Equal(3, trace.Count);
        Assert.Equal(7, trace.Sum(t => t.Pieces));
        Assert.Equal(42, trace.Sum(t => t.Words));
        Assert.All(trace, t =>
        {
            Assert.True(t.Accepted);
            Assert.True(t.EnqueuedAt <= t.StartedAt, "queued before sent");
            Assert.True(t.StartedAt <= t.LandedAt, "sent before landed");
        });
        Assert.Equal([0, 1, 2], trace.Select(t => t.Ordinal).Order());
    }

    [Fact]
    public async Task ARefusedUnitKeepsEveryMemberAsSpoken()
    {
        await using var tidier = new FakeTranscriptTidier(new FakeTidierOptions { Insert = "Well" });
        await using var stage = new TidyStage(tidier, new TidyOptions { Unit = TidyUnitKind.JoinedRun });

        var segments = Enumerable.Range(0, 3).Select(i => Timed(i, i * 6, $"line {i} a b c d")).ToList();
        foreach (var segment in segments)
        {
            stage.Enqueue(segment);
        }

        var outcomes = await stage.CompleteAsync();

        Assert.Equal(1, stage.Units);
        Assert.Equal(3, outcomes.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.False(outcomes[i].Accepted);
            Assert.Contains("added 'Well'", outcomes[i].Refusal, StringComparison.Ordinal);
            Assert.Same(segments[i], outcomes[i].Segment);
        }

        Assert.False(Assert.Single(stage.Trace).Accepted);
    }

    [Fact]
    public async Task SentenceRunsLandEachSegmentOnceItsLastPieceHas()
    {
        await using var tidier = new FakeTranscriptTidier();
        var landed = new List<int>();
        var gate = new Lock();
        await using var stage = new TidyStage(
            tidier,
            new TidyOptions { Unit = TidyUnitKind.SentenceRun, Concurrency = 1 },
            (index, _) =>
            {
                lock (gate)
                {
                    landed.Add(index);
                }
            });

        stage.Enqueue(Timed(0, 0, "we did well. Then the"));
        stage.Enqueue(Timed(1, 5, "numbers came in. And"));
        stage.Enqueue(Timed(2, 9, "then we rested."));

        var outcomes = await stage.CompleteAsync();

        Assert.Equal(3, stage.Units);
        Assert.Equal(["we did well.", "Then the numbers came in.", "And then we rested."], tidier.Lines);
        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Accepted, o.Refusal));
        Assert.Equal("We did well. Then the", outcomes[0].Segment.Text);
        Assert.Equal("numbers came in. And", outcomes[1].Segment.Text);
        Assert.Equal("then we rested.", outcomes[2].Segment.Text);
        Assert.All(outcomes, o => Assert.True(o.Segment.WordsReproduceText()));
        Assert.Equal([0, 1, 2], landed.Order());
        Assert.Equal(0, stage.Pending);
    }
}
