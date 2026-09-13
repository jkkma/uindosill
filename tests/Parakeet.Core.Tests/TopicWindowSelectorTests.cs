using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class TopicWindowSelectorTests
{
    [Fact]
    public void ADiscussionKeepsItsPronounOnlyElaborationAndBoundaryTurn()
    {
        // The subject is named once, followed by distinct points with no repeated keywords.
        // Other topics repeatedly say "remake" and use the question's conversational words.
        var text = Enumerable.Repeat("What did they say about the other remake?", 40).ToArray();
        text[0] = "The Silverfall remake trailer looks gorgeous.";
        text[1] = "Its surfaces are strangely moist.";
        text[2] = "His clothes have too many extraneous details.";
        text[3] = "They balance nostalgia with new visual ideas.";
        text[4] = "Their marketing focuses on lighting instead of useful information.";
        text[5] = "That is why the presentation tells us so little.";
        var cover = Cover(text);

        var selected = TopicWindowSelector.Select(cover,
            "what did they say about the Silverfall remake?", 32_000, maxWindows: 8);

        Assert.Equal(Enumerable.Range(1, 6), selected.Select(window => window.FirstSegment));
        Assert.All(selected, window => Assert.Same(cover[window.FirstSegment - 1], window));
    }

    [Fact]
    public void AOneWindowAllowanceKeepsTheDirectAnswerInsteadOfThePassageOpening()
    {
        var cover = Cover("Welcome.", "An unrelated story.", "A lengthy tangent.",
            "The Laurel cutoff is twenty dollars.", "More explanation.");

        var selected = TopicWindowSelector.Select(cover, "what is the Laurel cutoff?", 32_000, maxWindows: 1);

        Assert.Same(cover[3], Assert.Single(selected));
    }

    [Fact]
    public void TheHardCharacterAllowanceIncludesEveryCitationAndSeparator()
    {
        var cover = Cover("Laurel cutoff is twenty.", new string('x', 90), "Later explanation.");
        var budget = Cost(cover[0]) + Cost(cover[1]);

        var selected = TopicWindowSelector.Select(cover, "Laurel cutoff", budget);

        Assert.Equal(2, selected.Count);
        Assert.Equal(budget, selected.Sum(Cost));
        var error = Assert.Throws<InvalidOperationException>(() =>
            TopicWindowSelector.Select(cover, "Laurel cutoff", Cost(cover[0]) - 1));
        Assert.Contains("Matching transcript evidence", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => TopicWindowSelector.Select(cover, "Laurel cutoff", 0));
    }

    [Fact]
    public void AWindowThatDoesNotFitCannotBeSkippedToCreateAGapInThePassage()
    {
        var cover = Cover("Laurel cutoff.", new string('x', 2_000), "This also matters.");

        var selected = TopicWindowSelector.Select(cover, "Laurel cutoff", 100);

        Assert.Same(cover[0], Assert.Single(selected));
    }

    [Fact]
    public void NeighbouringPassagesKeepEachOriginalWindowOnlyOnceInTimeOrder()
    {
        var text = Enumerable.Repeat("A supporting explanation.", 12).ToArray();
        text[1] = "Laurel has an announcement.";
        text[6] = "Laurel has another announcement.";
        var cover = Cover(text);
        var duplicatedAndReversed = cover.Reverse().Concat(cover).ToArray();

        var selected = TopicWindowSelector.Select(duplicatedAndReversed, "Laurel", 32_000, maxWindows: 20);

        Assert.Equal(Enumerable.Range(1, 11), selected.Select(window => window.FirstSegment));
        Assert.Equal(selected.Count, selected.Select(window => window.CitationId).Distinct().Count());
    }

    [Fact]
    public void LiteralQuestionsMadeOnlyOfQuestionWordsStillFindEvidence()
    {
        var cover = Cover("The word they is a pronoun.", "An unrelated explanation.");

        var selected = TopicWindowSelector.Select(cover, "they", 32_000, maxWindows: 1);

        Assert.Same(cover[0], Assert.Single(selected));
    }

    [Fact]
    public void NonEnglishQuestionsRetainTheirTerms()
    {
        var cover = Cover("讨论别的内容。", "银河计划成功。", "后来提供了更多细节。");

        var selected = TopicWindowSelector.Select(cover, "银河计划成功", 32_000, maxWindows: 1);

        Assert.Same(cover[1], Assert.Single(selected));
    }

    [Fact]
    public void AbsentTermsAndEmptyInputsDoNotProduceUnrelatedEvidence()
    {
        var cover = Cover("Laurel explained the cutoff.");

        Assert.Empty(TopicWindowSelector.Select(cover, "silverfall", 32_000));
        Assert.Empty(TopicWindowSelector.Select(cover, "...", 32_000));
        Assert.Empty(TopicWindowSelector.Select([], "Laurel", 32_000));
    }

    [Fact]
    public void ContextDoesNotJumpAcrossLongSilenceEvenWithinTheSamePassage()
    {
        var cover = new[]
        {
            new TranscriptWindow
            {
                FirstSegment = 1, LastSegment = 1,
                Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(30), Text = "Laurel cutoff.",
            },
            new TranscriptWindow
            {
                FirstSegment = 2, LastSegment = 2,
                Start = TimeSpan.FromMinutes(4), End = TimeSpan.FromMinutes(5), Text = "An unrelated later topic.",
            },
        };

        Assert.Same(cover[0], Assert.Single(TopicWindowSelector.Select(cover, "Laurel cutoff", 32_000)));
    }

    [Fact]
    public void EquallyRankedPassagesAreDeterministicAndRespectTheWindowCap()
    {
        var cover = Cover(Enumerable.Repeat("Laurel cutoff.", 30).ToArray());

        var first = TopicWindowSelector.Select(cover, "Laurel cutoff", 32_000, maxWindows: 8);
        var second = TopicWindowSelector.Select(cover, "Laurel cutoff", 32_000, maxWindows: 8);

        Assert.Equal(8, first.Count);
        Assert.Equal(first, second);
        Assert.Equal(1, first[0].FirstSegment);
    }

    [Fact]
    public void OutOfTimeOrderSegmentsDoNotTurnAnUnseenIdGapIntoACitation()
    {
        var document = new TranscriptDocument
        {
            Segments =
            [
                new() { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(20), Text = "Laurel cutoff." },
                new() { Start = TimeSpan.FromMinutes(10), End = TimeSpan.FromMinutes(11), Text = "A different topic." },
                new() { Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(40), Text = "The explanation continues." },
            ],
            AudioDuration = TimeSpan.FromMinutes(11),
        };
        var cover = TranscriptWindowBuilder.Build(document, TranscriptWindowOptions.Cover);

        var selected = TopicWindowSelector.Select(cover, "Laurel cutoff", 32_000);

        Assert.Equal(new[] { "S1", "S3" }, selected.Select(window => window.CitationId));
        Assert.All(selected, window => Assert.Equal(window,
            TranscriptWindowBuilder.FromRun(document, window.FirstSegment, window.LastSegment)));
    }

    private static int Cost(TranscriptWindow window) => window.Text.Length + window.CitationId.Length + 4;

    private static IReadOnlyList<TranscriptWindow> Cover(params string[] text)
    {
        var document = new TranscriptDocument
        {
            Segments = text.Select((line, index) => new TranscriptSegment
            {
                Start = TimeSpan.FromMinutes(index),
                End = TimeSpan.FromMinutes(index + 1),
                Text = line,
            }).ToArray(),
            AudioDuration = TimeSpan.FromMinutes(text.Length),
        };
        return TranscriptWindowBuilder.Build(document, TranscriptWindowOptions.Cover);
    }
}
