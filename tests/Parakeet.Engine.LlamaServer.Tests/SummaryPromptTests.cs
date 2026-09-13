using Parakeet.Core.Answers;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer.Tests;

public sealed class SummaryPromptTests
{
    private static AskRequest Request() => new()
    {
        Question = "Summarize the video",
        Mode = AnswerMode.MapReduce,
        Transcript = new TranscriptDocument
        {
            SourceName = "https://www.youtube.com/watch?v=example",
            Segments = [new TranscriptSegment { Text = "The original words.", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10) }],
        },
        Evidence = [new TranscriptWindow
        {
            FirstSegment = 1, LastSegment = 1, Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(10), Text = "The original words.",
        }],
    };

    [Fact]
    public void ASectionIsNeverPresentedAsTheCompleteRecording()
    {
        var (instruction, content) = AnswerPromptBuilder.BuildMessages(
            Request() with { SummaryStage = SummaryStage.Section }, requireQuote: false);
        Assert.Contains("section of a longer recording", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("complete transcript", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("Open with one sentence", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("250-400 words", instruction, StringComparison.Ordinal);
        Assert.Contains("Transcript section:", content, StringComparison.Ordinal);
        Assert.Contains("[S1] The original words.", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SynthesisReadsGeneratedNotesWithoutMisrepresentingThemAsSpokenEvidence()
    {
        var (instruction, content) = AnswerPromptBuilder.BuildMessages(Request() with
        {
            SummaryStage = SummaryStage.Synthesis,
            SummaryNotes = "- Topic: a generated paraphrase [S1]",
        }, requireQuote: false);
        Assert.Contains("summaries, not verbatim speech", instruction, StringComparison.Ordinal);
        Assert.Contains("original transcript", instruction, StringComparison.Ordinal);
        Assert.Contains("beginning, middle and end", instruction, StringComparison.Ordinal);
        Assert.Contains("250-400 words", instruction, StringComparison.Ordinal);
        Assert.Contains("a generated paraphrase [S1]", content, StringComparison.Ordinal);
        Assert.DoesNotContain("The original words.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("complete transcript", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void SynthesisCannotSilentlyFallBackToSourceTextWhenItsNotesAreMissing() =>
        Assert.Throws<ArgumentException>(() => AnswerPromptBuilder.BuildMessages(
            Request() with { SummaryStage = SummaryStage.Synthesis }, requireQuote: false));

    [Fact]
    public void AUrlQueryIsNotUsedAsTheRecordingTitle()
    {
        var (instruction, _) = AnswerPromptBuilder.BuildMessages(Request(), requireQuote: false);
        Assert.DoesNotContain("file is named", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("watch?v=", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDisplayTitleCanNameAnImportedRecordingWithoutBecomingEvidence()
    {
        var (instruction, _) = AnswerPromptBuilder.BuildMessages(
            Request() with { RecordingName = "Mr. Beast / Castle Super Beast 388" }, requireQuote: false);
        Assert.Contains("\"Mr. Beast / Castle Super Beast 388\"", instruction, StringComparison.Ordinal);
        Assert.Contains("never as a fact about its contents", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("watch?v=", instruction, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SummaryStage.Section)]
    [InlineData(SummaryStage.Reduction)]
    public void IntermediateNotesCannotBeInstructedToUseTheUncitedFallback(SummaryStage stage)
    {
        var (instruction, _) = AnswerPromptBuilder.BuildMessages(Request() with
        {
            SummaryStage = stage,
            SummaryNotes = "- Topic: a paraphrase [S1]",
        }, requireQuote: false);
        Assert.Contains("Include only supported, cited notes", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("gets [?]", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(AnswerParser.AbstainSentinel, instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("Open with one sentence", instruction, StringComparison.Ordinal);
    }
}
