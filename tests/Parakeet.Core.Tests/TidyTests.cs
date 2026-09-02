using Parakeet.Core.Formatting;
using Parakeet.Core.Jobs;
using Parakeet.Core.Tidying;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

/// <summary>
/// The delete-only contract every tidied line is held to (docs/PHASES.md, decided 2026-09-01),
/// and the stage that runs it beside the recogniser.
/// </summary>
public class TidyContractTests
{
    /// <summary>A timed segment: one word per second, each with a confidence.</summary>
    private static TranscriptSegment Spoken(string text, params float[] confidences)
    {
        var tokens = text.Split(' ');
        var words = new List<TranscriptWord>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            words.Add(new TranscriptWord
            {
                Text = tokens[i],
                Start = TimeSpan.FromSeconds(i),
                End = TimeSpan.FromSeconds(i + 1),
                Confidence = i < confidences.Length ? confidences[i] : 0.9f,
            });
        }

        return new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(tokens.Length),
            Text = text,
            Words = words,
            SourceSegmentIndex = 3,
            Speaker = "Speaker 1",
        };
    }

    [Fact]
    public void DeletionsAreAcceptedAndEveryKeptWordKeepsTheTimeItWasSpokenAt()
    {
        var spoken = Spoken("um so the the cat sat sat on the mat");

        var outcome = TidyContract.Apply(spoken, "So the cat sat on the mat.", 0.45f);

        Assert.True(outcome.Accepted, outcome.Refusal);
        Assert.Equal("So the cat sat on the mat.", outcome.Segment.Text);
        Assert.True(outcome.Segment.WordsReproduceText());

        // The timeline, the source index and the speaker are the spoken segment's.
        Assert.Equal(spoken.Start, outcome.Segment.Start);
        Assert.Equal(spoken.End, outcome.Segment.End);
        Assert.Equal(3, outcome.Segment.SourceSegmentIndex);
        Assert.Equal("Speaker 1", outcome.Segment.Speaker);

        // "So" came from "so" at second 1, "the" from the second "the" — the alignment keeps the
        // later of two equal words when the earlier is what was dropped — "cat" from second 4.
        var words = outcome.Segment.Words;
        Assert.Equal(7, words.Count);
        Assert.Equal("So", words[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), words[0].Start);
        Assert.Equal("cat", words[2].Text);
        Assert.Equal(TimeSpan.FromSeconds(4), words[2].Start);
        Assert.Equal("mat.", words[6].Text);
        Assert.Equal(TimeSpan.FromSeconds(9), words[6].Start);
        Assert.Equal(TimeSpan.FromSeconds(10), words[6].End);

        // No word is a replacement, and the deletions are the visible ones: "the", "sat" — the
        // filler "um" is invisible to the normaliser and is not counted.
        Assert.All(words, w => Assert.Null(w.ReplacedFrom));
        Assert.Empty(outcome.Replacements);
        Assert.Equal(2, outcome.DeletedWords);
    }

    [Fact]
    public void PunctuationAndCasingChangesOnKeptWordsPass()
    {
        var spoken = Spoken("its a new level");

        var outcome = TidyContract.Apply(spoken, "It's a new level.", 0.45f);

        // "it's" and "its" both normalise to tokens the harness scores alike? They do not —
        // the apostrophe is kept between letters — so this is a substitution and is refused.
        Assert.False(outcome.Accepted);
        Assert.Same(spoken, outcome.Segment);
        Assert.Contains("changed 'its' to 'It's'", outcome.Refusal, StringComparison.Ordinal);

        // Casing and terminal punctuation alone pass.
        var cased = TidyContract.Apply(spoken, "Its a new level.", 0.45f);
        Assert.True(cased.Accepted, cased.Refusal);
        Assert.Equal("Its a new level.", cased.Segment.Text);
    }

    [Fact]
    public void AHyphenationSpansTheTwoSpokenWordsItJoins()
    {
        var spoken = Spoken("micro behaviors matter");

        var outcome = TidyContract.Apply(spoken, "Micro-behaviors matter.", 0.45f);

        Assert.True(outcome.Accepted, outcome.Refusal);
        var words = outcome.Segment.Words;
        Assert.Equal(2, words.Count);
        Assert.Equal("Micro-behaviors", words[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(0), words[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2), words[0].End);
        Assert.Equal(0, outcome.DeletedWords);
    }

    [Fact]
    public void AnInsertedWordIsRefusedAndTheSpokenLineIsKept()
    {
        var spoken = Spoken("gonna have to worry about it");

        var outcome = TidyContract.Apply(spoken, "Going to have to worry about it.", 0.45f);

        Assert.False(outcome.Accepted);
        Assert.Same(spoken, outcome.Segment);
        Assert.NotNull(outcome.Refusal);
    }

    [Fact]
    public void ASubstitutionIsRefusedWhenTheRecogniserWasSureAndAcceptedThroughTheDoorWhenItWasNot()
    {
        // "objec" at 0.30: below the threshold, so the model's "object" may replace it.
        var doubted = Spoken("the objec is heavy", 0.9f, 0.30f, 0.9f, 0.9f);
        var accepted = TidyContract.Apply(doubted, "The object is heavy.", 0.45f);

        Assert.True(accepted.Accepted, accepted.Refusal);
        var replacement = Assert.Single(accepted.Replacements);
        Assert.Equal(1, replacement.SpokenWordIndex);
        Assert.Equal("objec", replacement.Spoken);
        Assert.Equal("object", replacement.Replacement);
        Assert.Equal(0.30f, replacement.Confidence);

        // The replacement keeps the spoken word's span and says what it replaced; nothing else does.
        var word = accepted.Segment.Words[1];
        Assert.Equal("object", word.Text);
        Assert.Equal("objec", word.ReplacedFrom);
        Assert.True(word.IsReplacement);
        Assert.Equal(TimeSpan.FromSeconds(1), word.Start);
        Assert.Equal(TimeSpan.FromSeconds(2), word.End);
        Assert.Single(accepted.Segment.Words, w => w.IsReplacement);

        // The same word at 0.80: the recogniser was sure, and the line is kept as spoken.
        var sure = Spoken("the objec is heavy", 0.9f, 0.80f, 0.9f, 0.9f);
        var refused = TidyContract.Apply(sure, "The object is heavy.", 0.45f);
        Assert.False(refused.Accepted);
        Assert.Same(sure, refused.Segment);

        // At the threshold itself the door is shut: "below", not "at or below".
        var edge = Spoken("the objec is heavy", 0.9f, 0.45f, 0.9f, 0.9f);
        Assert.False(TidyContract.Apply(edge, "The object is heavy.", 0.45f).Accepted);

        // And a threshold of zero shuts it for every word — the contract without the door.
        Assert.False(TidyContract.Apply(doubted, "The object is heavy.", 0f).Accepted);
    }

    [Fact]
    public void AWordWithoutAConfidenceNeverGoesThroughTheDoor()
    {
        var spoken = Spoken("the objec is heavy") with
        {
            Words = Spoken("the objec is heavy").Words.Select(w => w with { Confidence = null }).ToList(),
        };

        Assert.False(TidyContract.Apply(spoken, "The object is heavy.", 0.45f).Accepted);
    }

    [Fact]
    public void AnEmptyRewriteIsRefusedForALineWithWordsAndAcceptedForALineOfFiller()
    {
        var words = Spoken("we should go");
        var refused = TidyContract.Apply(words, string.Empty, 0.45f);
        Assert.False(refused.Accepted);
        Assert.Contains("came back empty", refused.Refusal, StringComparison.Ordinal);

        // Fifteen of the two hundred measured lines were only "Um" or "Uh" and came back empty,
        // which the record calls right.
        var filler = Spoken("Um uh");
        var accepted = TidyContract.Apply(filler, string.Empty, 0.45f);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Segment.IsEmpty);
        Assert.Empty(accepted.Segment.Words);
    }

    [Fact]
    public void ASegmentWithoutVerifiedWordTimingsGetsATidyWithNone()
    {
        // No words at all: the text is what is compared, and the tidy carries no timings — the
        // rule the English pane set, that where a line has no verified word timings there is no
        // mark and nothing is guessed.
        var untimed = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(3),
            Text = "um so we we go",
        };

        var outcome = TidyContract.Apply(untimed, "So we go.", 0.45f);
        Assert.True(outcome.Accepted, outcome.Refusal);
        Assert.Equal("So we go.", outcome.Segment.Text);
        Assert.Empty(outcome.Segment.Words);

        // And with no confidence to read, the door is shut.
        Assert.False(TidyContract.Apply(untimed, "So we went.", 0.45f).Accepted);

        // Words that do not reproduce the text are the same case: the text is the authority.
        var mismatched = Spoken("so we go") with { Text = "um so we we go" };
        var fromText = TidyContract.Apply(mismatched, "So we go.", 0.45f);
        Assert.True(fromText.Accepted, fromText.Refusal);
        Assert.Empty(fromText.Segment.Words);
    }

    [Fact]
    public void ARewriteWordTheNormaliserCannotSeeBorrowsANeighboursTime()
    {
        // A dash the model put between two kept words is not a word anybody said; it takes the
        // time of the word before it so the words still spell the text and none is untimed.
        var spoken = Spoken("we went home");

        var outcome = TidyContract.Apply(spoken, "We went — home.", 0.45f);

        Assert.True(outcome.Accepted, outcome.Refusal);
        Assert.True(outcome.Segment.WordsReproduceText());
        var dash = outcome.Segment.Words[2];
        Assert.Equal("—", dash.Text);
        Assert.Equal(TimeSpan.FromSeconds(1), dash.Start);
        Assert.Null(dash.ReplacedFrom);
    }

    [Fact]
    public void AnOutOfRangeThresholdIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TidyContract.Apply(Spoken("a b"), "a", 1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TidyOptions { LowConfidenceThreshold = -0.1f }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TidyOptions { Concurrency = 0 }.Validate());
    }
}

public class TidyStageTests
{
    private static TranscriptSegment Segment(int i, string text) => new()
    {
        Start = TimeSpan.FromSeconds(i * 2),
        End = TimeSpan.FromSeconds((i * 2) + 2),
        Text = text,
        SourceSegmentIndex = i,
    };

    [Fact]
    public async Task OutcomesComeBackInEnqueueOrderWhateverOrderTheModelFinishesThemIn()
    {
        await using var tidier = new FakeTranscriptTidier(new FakeTidierOptions { PerLineDelay = TimeSpan.FromMilliseconds(5) });
        var landed = new List<int>();
        var gate = new Lock();

        await using var stage = new TidyStage(
            tidier,
            new TidyOptions { Concurrency = 4 },
            (index, _) =>
            {
                lock (gate)
                {
                    landed.Add(index);
                }
            });

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(i, stage.Enqueue(Segment(i, $"um line {i} {i}")));
        }

        var outcomes = await stage.CompleteAsync();

        Assert.Equal(20, outcomes.Count);
        Assert.Equal(0, stage.Pending);
        for (var i = 0; i < 20; i++)
        {
            Assert.True(outcomes[i].Accepted, outcomes[i].Refusal);
            Assert.Equal($"Line {i}", outcomes[i].Segment.Text);
            Assert.Equal(i, outcomes[i].Segment.SourceSegmentIndex);
        }

        // Every index landed exactly once, through the callback.
        Assert.Equal(Enumerable.Range(0, 20), landed.Order());
        Assert.Equal(20, tidier.Lines.Count);
    }

    [Fact]
    public async Task AnEmptySegmentPassesThroughWithoutARequest()
    {
        await using var tidier = new FakeTranscriptTidier();
        await using var stage = new TidyStage(tidier);

        stage.Enqueue(Segment(0, string.Empty));
        stage.Enqueue(Segment(1, "uh hello"));
        var outcomes = await stage.CompleteAsync();

        Assert.True(outcomes[0].Segment.IsEmpty);
        Assert.Equal("Hello", outcomes[1].Segment.Text);
        Assert.Single(tidier.Lines);
    }

    [Fact]
    public async Task AFailureOnOneLineSurfacesFromCompleteAndNothingMoreCanBeEnqueued()
    {
        await using var tidier = new FakeTranscriptTidier(new FakeTidierOptions { FailOnTidy = true });
        await using var stage = new TidyStage(tidier, new TidyOptions { Concurrency = 2 });

        stage.Enqueue(Segment(0, "a"));
        stage.Enqueue(Segment(1, "b"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(stage.CompleteAsync);
        Assert.Contains("configured to fail on every line", failure.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => stage.Enqueue(Segment(2, "c")));
    }

    [Fact]
    public async Task CancellationStopsTheLinesInFlight()
    {
        await using var tidier = new FakeTranscriptTidier(new FakeTidierOptions { PerLineDelay = TimeSpan.FromSeconds(10) });
        using var cancel = new CancellationTokenSource();
        await using var stage = new TidyStage(tidier, ct: cancel.Token);

        stage.Enqueue(Segment(0, "slow"));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(stage.CompleteAsync);
    }
}

public class TranscriptTidyTests
{
    private static TranscriptDocument Document() => new()
    {
        Segments =
        [
            new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Text = "um so we we went", SourceSegmentIndex = 0 },
            new TranscriptSegment { Start = TimeSpan.FromSeconds(2), End = TimeSpan.FromSeconds(4), Text = string.Empty, SourceSegmentIndex = 1 },
            new TranscriptSegment { Start = TimeSpan.FromSeconds(4), End = TimeSpan.FromSeconds(6), Text = "and then home", SourceSegmentIndex = 2, Speaker = "Speaker 2" },
        ],
        SourceName = "a.wav",
        ModelId = "asr",
    };

    [Fact]
    public async Task ThePassTidiesEverySegmentAndStampsItsProvenance()
    {
        await using var tidier = new FakeTranscriptTidier(new FakeTidierOptions { Backend = ComputeBackend.Vulkan });
        var reports = new List<TranscriptionProgress>();
        var progress = new SynchronousProgress(reports.Add);

        var (tidied, summary) = await TranscriptTidy.TidyAsync(Document(), tidier, progress: progress);

        Assert.Equal(["So we went", "", "And then home"], tidied.Segments.Select(s => s.Text));
        Assert.Equal("Speaker 2", tidied.Segments[2].Speaker);
        Assert.True(tidied.IsTidied);
        Assert.Equal("fake-tidier", tidied.TidyModelId);
        Assert.Equal(ComputeBackend.Vulkan, tidied.TidyBackend);
        Assert.Equal(0, tidied.TidyRefusedSegments);

        // The spoken document is untouched and keeps its own provenance.
        Assert.Equal("asr", tidied.ModelId);
        Assert.Null(Document().TidyModelId);

        Assert.Equal(3, summary.Segments);
        Assert.Equal(0, summary.Refused);
        Assert.Equal(1, summary.DeletedWords);
        Assert.Equal(0, summary.ReplacedWords);
        Assert.Null(summary.Describe());

        Assert.Equal(3, reports.Count);
        Assert.All(reports, r => Assert.Equal(TranscriptionStage.Tidying, r.Stage));
        Assert.Equal(3, reports[^1].SegmentsTotal);
    }

    [Fact]
    public async Task ARewriteThatBreaksTheContractLeavesTheLineAsSpokenAndIsCounted()
    {
        await using var tidier = new FakeTranscriptTidier(new FakeTidierOptions { Insert = "Well," });

        var (tidied, summary) = await TranscriptTidy.TidyAsync(Document(), tidier);

        Assert.Equal(["um so we we went", "", "and then home"], tidied.Segments.Select(s => s.Text));
        Assert.Equal(2, tidied.TidyRefusedSegments);
        Assert.Equal(2, summary.Refused);
        Assert.Equal("Tidy: 2 of 3 lines kept as spoken because the rewrite changed or added words.", summary.Describe());
    }

    [Fact]
    public void AssembleRefusesTheWrongNumberOfOutcomesAndAMovedSegment()
    {
        var document = Document();
        var capabilities = new FakeTranscriptTidier().Capabilities;

        Assert.Throws<InvalidOperationException>(() => TranscriptTidy.Assemble(document, [], capabilities));

        var moved = document.Segments.Select(s => new TidyOutcome { Accepted = true, Segment = s }).ToList();
        moved[0] = moved[0] with { Segment = moved[0].Segment with { Start = TimeSpan.FromSeconds(1) } };
        Assert.Throws<InvalidOperationException>(() => TranscriptTidy.Assemble(document, moved, capabilities));
    }

    [Fact]
    public async Task TheTidyPassFailsLikeTheOtherOptInsWithTheTranscriptHandedBackWhole()
    {
        var document = Document();
        await using var tidier = new FakeTranscriptTidier(new FakeTidierOptions { FailOnTidy = true });

        var (result, failure) = await OptInPass.Tidy.RunAsync(
            document,
            async () => (await TranscriptTidy.TidyAsync(document, tidier)).Document);

        Assert.Same(document, result);
        Assert.NotNull(failure);
        Assert.StartsWith(
            "Tidying failed for this file, so the transcript was written without the tidied version:",
            failure!.Describe(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheTidyProvenanceAndTheReplacedWordSurviveTheJsonRoundTrip()
    {
        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment
                {
                    Start = TimeSpan.Zero,
                    End = TimeSpan.FromSeconds(2),
                    Text = "the object",
                    Words =
                    [
                        new TranscriptWord { Text = "the", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Confidence = 0.9f },
                        new TranscriptWord { Text = "object", Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Confidence = 0.3f, ReplacedFrom = "objec" },
                    ],
                },
            ],
            TidyModelId = "gemma-4-E4B-it-qat-UD-Q4_K_XL",
            TidyBackend = ComputeBackend.Vulkan,
            TidyRefusedSegments = 2,
        };

        var json = TranscriptFormats.Json.Format(document);
        Assert.Contains("\"tidyModel\"", json, StringComparison.Ordinal);
        Assert.Contains("\"replacedFrom\": \"objec\"", json, StringComparison.Ordinal);

        var read = JsonTranscriptReader.Read(json);
        Assert.Equal("gemma-4-E4B-it-qat-UD-Q4_K_XL", read.TidyModelId);
        Assert.Equal(ComputeBackend.Vulkan, read.TidyBackend);
        Assert.Equal(2, read.TidyRefusedSegments);
        Assert.Equal("objec", read.Segments[0].Words[1].ReplacedFrom);
        Assert.Single(read.ReplacedWords);

        // A document nobody tidied writes none of it.
        var plain = TranscriptFormats.Json.Format(document with { TidyModelId = null, TidyBackend = null, TidyRefusedSegments = null });
        Assert.DoesNotContain("tidy", plain, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWebVttAndMarkdownHeadersSayTheTextWasTidied()
    {
        var document = Document() with { TidyModelId = "fake-tidier", TidyBackend = ComputeBackend.Cpu };

        Assert.Contains("NOTE Tidied by fake-tidier", TranscriptFormats.Vtt.Format(document), StringComparison.Ordinal);
        var markdown = TranscriptFormats.Markdown.Format(document);
        Assert.Contains("Tidied by", markdown, StringComparison.Ordinal);
        Assert.Contains("fake-tidier", markdown, StringComparison.Ordinal);
    }

    private sealed class SynchronousProgress(Action<TranscriptionProgress> report) : IProgress<TranscriptionProgress>
    {
        public void Report(TranscriptionProgress value) => report(value);
    }
}
