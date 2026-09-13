using Parakeet.Core.Answers;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class TranscriptSummaryBuilderTests
{
    private static TranscriptDocument Transcript(params string[] text) => new()
    {
        Segments = text.Select((words, i) => new TranscriptSegment
        {
            Start = TimeSpan.FromSeconds(i * 10),
            End = TimeSpan.FromSeconds((i + 1) * 10),
            Text = words,
        }).ToArray(),
        AudioDuration = TimeSpan.FromSeconds(text.Length * 10),
    };

    private static IReadOnlyList<TranscriptWindow> Windows(TranscriptDocument document) =>
        Enumerable.Range(1, document.Segments.Count)
            .Select(i => TranscriptWindowBuilder.FromRun(document, i, i)).ToArray();

    [Fact]
    public void PartitionKeepsEveryWindowOnceAndChargesIdsAndNewlines()
    {
        var cover = Windows(Transcript("alpha", "beta", "gamma", "delta"));
        var batches = TranscriptSummaryBuilder.Partition(cover, budgetChars: 21);

        Assert.Equal(3, batches.Count);
        Assert.Equal(2, batches[0].Count);
        Assert.Equal(cover, batches.SelectMany(b => b));
        foreach (var batch in batches)
        {
            Assert.InRange(batch.Sum(w => $"[{w.CitationId}] {w.Text}\n".Length), 1, 21);
        }

        Assert.Same(cover[0], batches[0][0]);
        Assert.Same(cover[^1], batches[^1][^1]);
    }

    [Fact]
    public void CompleteCoverIncludesSpeechWhoseTimesAreOutOfSegmentOrder()
    {
        var document = Transcript("first", "later", "middle") with
        {
            Segments =
            [
                new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "first" },
                new() { Start = TimeSpan.FromSeconds(180), End = TimeSpan.FromSeconds(190), Text = "later" },
                new() { Start = TimeSpan.FromSeconds(70), End = TimeSpan.FromSeconds(80), Text = "middle" },
            ],
            AudioDuration = TimeSpan.FromSeconds(190),
        };
        var cover = TranscriptWindowBuilder.Build(document, TranscriptWindowOptions.Cover);
        var batches = TranscriptSummaryBuilder.Partition(cover, budgetChars: 20);

        Assert.Equal(cover, batches.SelectMany(b => b));
        Assert.Equal(new[] { 1, 2, 3 }, batches.SelectMany(b => b).Select(w => w.FirstSegment).Order());
    }

    [Fact]
    public void EmptyPlanningProducesNoPasses()
    {
        Assert.Empty(TranscriptSummaryBuilder.Partition([]));
        Assert.Empty(TranscriptSummaryBuilder.GroupNotes([]));
    }

    [Fact]
    public void OversizeSourceWindowFailsWithoutTruncation()
    {
        var cover = Windows(Transcript("alpha"));
        var error = Assert.Throws<InvalidOperationException>(() =>
            TranscriptSummaryBuilder.Partition(cover, budgetChars: 10));

        Assert.Contains("S1", error.Message);
        Assert.Equal("alpha", cover[0].Text);
    }

    [Fact]
    public void ExcessivePassCountFailsExplicitly()
    {
        var cover = Windows(Transcript(Enumerable.Repeat(new string('x', 20),
            TranscriptSummaryBuilder.MaximumBatches + 1).ToArray()));

        Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.Partition(cover, 30));
    }

    [Fact]
    public void InvalidBudgetFailsBeforePlanning()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptSummaryBuilder.Partition([], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptSummaryBuilder.GroupNotes([], 0));
    }

    [Fact]
    public void NoteRetainsEveryCitedClaimAndOnlyTheCitedOriginalRuns()
    {
        var document = Transcript("a gorgeous forest", "wet looking models", "a later topic");
        var note = TranscriptSummaryBuilder.CreateNote(
            "They discuss a trailer [S1-S2]\n- Forest: it looks attractive «a gorgeous forest» [S1]\n- Models: they look wet [S2]",
            document, [TranscriptWindowBuilder.FromRun(document, 1, 3)]);

        Assert.Equal(3, note.Text.Split('\n').Length);
        Assert.Contains("“a gorgeous forest” [S1]", note.Text);
        Assert.Equal(1, note.Text.Split("a gorgeous forest", StringSplitOptions.None).Length - 1);
        Assert.Equal(new[] { "S1-S2", "S1", "S2" }, note.Evidence.Select(w => w.CitationId));
        Assert.All(note.Evidence, window => Assert.Equal(
            TranscriptWindowBuilder.FromRun(document, window.FirstSegment, window.LastSegment), window));
        Assert.True(CitationValidator.Validate(AnswerParser.Parse(note.Text), document).AllCitationsPass);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOT_IN_TRANSCRIPT")]
    [InlineData("A fluent but uncited summary")]
    [InlineData("- A guess [?]")]
    [InlineData("- [S1]")]
    [InlineData("- Wrong id [S99]")]
    [InlineData("- Reversed [S2-S1]")]
    [InlineData("- Malformed [Sbad] [S1]")]
    [InlineData("- Unclosed [S1] [S2")]
    [InlineData("- Wrong quote «invented words» [S1]")]
    [InlineData("- Unclosed quote «alpha [S1]")]
    [InlineData("- Multiple quotes «alpha» and «beta» [S1-S2]")]
    public void MalformedOrUnusableSummaryCannotBePromoted(string raw)
    {
        var document = Transcript("alpha", "beta");
        Assert.Throws<InvalidOperationException>(() =>
            TranscriptSummaryBuilder.CreateNote(raw, document, Windows(document)));
    }

    [Fact]
    public void MixedUncitedClaimFailsRatherThanSilentlyDroppingSectionContent()
    {
        var document = Transcript("alpha");
        Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.CreateNote(
            "- First: alpha [S1]\n- Second: speculation [?]", document, Windows(document)));
    }

    [Fact]
    public void CitationOfRealButUnshownSegmentFails()
    {
        var document = Transcript("alpha", "beta", "gamma");
        var error = Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.CreateNote(
            "- Hidden topic [S2]", document, [Windows(document)[0], Windows(document)[2]]));

        Assert.Contains("not shown", error.Message);
    }

    [Fact]
    public void CitationCannotBridgeAnUnshownGap()
    {
        var document = Transcript("alpha", "beta", "gamma");
        Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.CreateNote(
            "- All topics [S1-S3]", document, [Windows(document)[0], Windows(document)[2]]));
    }

    [Fact]
    public void CitationCanSpanAdjacentShownWindows()
    {
        var document = Transcript("alpha", "beta");
        var note = TranscriptSummaryBuilder.CreateNote("- Both topics [S1-S2]", document, Windows(document));

        Assert.Equal("S1-S2", Assert.Single(note.Evidence).CitationId);
    }

    [Fact]
    public void QuoteNeedOnlyMatchOneOfItsMultipleCitations()
    {
        var document = Transcript("alpha", "beta");
        var note = TranscriptSummaryBuilder.CreateNote(
            "- Both topics, including «alpha» [S1, S2]", document, Windows(document));

        Assert.Equal(2, note.Evidence.Count);
    }

    [Fact]
    public void ModifiedEvidenceCannotStandInForOriginalTranscriptText()
    {
        var document = Transcript("alpha");
        var forged = Windows(document)[0] with { Text = "invented evidence" };
        Assert.Throws<ArgumentException>(() => TranscriptSummaryBuilder.CreateNote(
            "- Topic [S1]", document, [forged]));
    }

    [Fact]
    public void VerifiedInlineQuoteStaysInItsSentenceOnce()
    {
        var document = Transcript("the models look wet");
        var note = TranscriptSummaryBuilder.CreateNote(
            "- Models: they said the models «look wet» throughout the trailer [S1]", document, Windows(document));

        Assert.Equal("- Models: they said the models “look wet” throughout the trailer [S1]", note.Text);
        Assert.Equal(1, note.Text.Split("look wet", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void FinalSummaryAllowsUncitedLeadAndUnverifiedProse()
    {
        var document = Transcript("alpha");
        var answer = AnswerParser.Parse(
            "This recording covers several topics.\n- Alpha [S1]\n- An unverified claim [?]\n- Another uncited statement",
            allowLead: true);

        TranscriptSummaryBuilder.ValidateFinalAnswer(answer, document, Windows(document));
        Assert.True(answer.Lead!.IsUncited);
        Assert.True(answer.Bullets[^1].IsUncited);
    }

    [Fact]
    public void FinalSummaryAllowsValidOriginalCitationAndMatchingQuote()
    {
        var document = Transcript("alpha words", "beta words");
        var answer = AnswerParser.Parse("- Both topics, including «beta words» [S1, S2]");

        TranscriptSummaryBuilder.ValidateFinalAnswer(answer, document, Windows(document));
    }

    [Theory]
    [InlineData("- Hidden topic [S2]")]
    [InlineData("- Hidden span [S1-S3]")]
    public void FinalSummaryCannotCiteAnExistingButUnshownSource(string raw)
    {
        var document = Transcript("alpha", "beta", "gamma");
        Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.ValidateFinalAnswer(
            AnswerParser.Parse(raw), document, [Windows(document)[0], Windows(document)[2]]));
    }

    [Theory]
    [InlineData("- Missing source [S99]")]
    [InlineData("- Reversed source [S2-S1]")]
    [InlineData("- Wrong quotation «not what they said» [S1]")]
    public void FinalSummaryRejectsInvalidSourceCitationsAndQuotes(string raw)
    {
        var document = Transcript("alpha", "beta");
        Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.ValidateFinalAnswer(
            AnswerParser.Parse(raw), document, Windows(document)));
    }

    [Fact]
    public void FinalSummaryRejectsCitationsBeyondTheKnownRecordingDuration()
    {
        var document = Transcript("alpha", "beta") with { AudioDuration = TimeSpan.FromSeconds(10) };
        Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.ValidateFinalAnswer(
            AnswerParser.Parse("- Too late [S2]"), document, Windows(document)));
    }

    [Fact]
    public void EmptyEvidenceCannotAnchorNotes()
    {
        Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.CreateNote(
            "- Topic [S1]", Transcript("alpha"), []));
    }

    [Fact]
    public void OversizeNotesFailWithoutDiscardingClaims()
    {
        var document = Transcript("alpha");
        Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.CreateNote(
            "- This is a long but cited claim [S1]", document, Windows(document), budgetChars: 15));
    }

    [Fact]
    public void ReductionCannotInventAnIdAbsentFromItsInputNotes()
    {
        var document = Transcript("alpha", "beta");
        var map = TranscriptSummaryBuilder.CreateNote("- Alpha [S1]", document, Windows(document));
        var group = Assert.Single(TranscriptSummaryBuilder.GroupNotes([map]));

        Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.CreateNote(
            "- Beta [S2]", document, group.Evidence));
    }

    [Fact]
    public void GroupingKeepsEveryNoteInOrderAndPreservesSourceGaps()
    {
        var document = Transcript("alpha", "beta", "gamma");
        var first = TranscriptSummaryBuilder.CreateNote("- Alpha [S1]", document, Windows(document));
        var last = TranscriptSummaryBuilder.CreateNote("- Gamma [S3]", document, Windows(document));
        var budget = first.Text.Length + last.Text.Length + 2;
        var groups = TranscriptSummaryBuilder.GroupNotes([first, last, first], budget);

        Assert.Equal(2, groups.Count);
        Assert.Equal(first.Text + "\n\n" + last.Text, groups[0].Text);
        Assert.Equal(first.Text, groups[1].Text);
        Assert.Equal(new[] { "S1", "S3" }, groups[0].Evidence.Select(w => w.CitationId));
        Assert.All(groups, group => Assert.True(group.Text.Length <= budget));
    }

    [Fact]
    public void OversizeSingleNoteCannotBeSilentlyTruncatedForReduction()
    {
        var document = Transcript("alpha");
        var note = TranscriptSummaryBuilder.CreateNote("- Alpha [S1]", document, Windows(document));
        Assert.Throws<InvalidOperationException>(() => TranscriptSummaryBuilder.GroupNotes([note], 5));
    }

    [Fact]
    public void IntermediateReductionMustShrinkAndStopAtTheRoundLimit()
    {
        var evidence = Windows(Transcript("alpha"));
        var before = new TranscriptSummaryNote("longer notes", evidence);
        var after = new TranscriptSummaryNote("short", evidence);
        TranscriptSummaryBuilder.EnsureReductionProgress([before], [after], round: 1);

        Assert.Throws<InvalidOperationException>(() =>
            TranscriptSummaryBuilder.EnsureReductionProgress([before], [before], round: 1));
        Assert.Throws<InvalidOperationException>(() =>
            TranscriptSummaryBuilder.EnsureReductionProgress([before], [], round: 1));
        Assert.Throws<InvalidOperationException>(() =>
            TranscriptSummaryBuilder.EnsureReductionProgress([before], [after], round: 7));
    }

    [Fact]
    public async Task FakeSummaryPassSupportsMultipleBulletsWithoutItsUncitedDisplayFixture()
    {
        var document = Transcript("alpha words", "beta words");
        var windows = Windows(document);
        await using var engine = new FakeAnswerEngine();
        await engine.LoadAsync();
        var chunks = new List<string>();
        await foreach (var chunk in engine.AskAsync(new AskRequest
        {
            Question = "Summarize this section",
            Transcript = document,
            Evidence = windows,
            Mode = AnswerMode.MapReduce,
            SummaryStage = SummaryStage.Section,
        }))
        {
            chunks.Add(chunk);
        }

        var note = TranscriptSummaryBuilder.CreateNote(string.Concat(chunks), document, windows);
        Assert.Equal(3, note.Text.Split('\n').Length);
        Assert.DoesNotContain("[?]", note.Text);
        Assert.Equal(2, note.Evidence.Count);
    }
}
