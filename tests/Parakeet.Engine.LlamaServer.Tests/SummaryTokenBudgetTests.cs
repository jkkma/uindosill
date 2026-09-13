using System.Text.Json;
using Parakeet.Core.Answers;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer.Tests;

public sealed class SummaryTokenBudgetTests
{
    private static AskRequest Request(SummaryStage stage, AnswerMode mode = AnswerMode.MapReduce) => new()
    {
        Question = "Summarize the recording",
        Transcript = new TranscriptDocument { Segments = [] },
        SummaryStage = stage,
        Mode = mode,
    };

    [Fact]
    public void OnlyFinalSynthesisReceivesTheLargerDefaultAnswerAllowance()
    {
        var options = new LlamaServerOptions { ModelPath = "test-model.gguf" };
        Assert.Equal(2_048, LlamaServerAnswerEngine.GenerationTokenBudget(Request(SummaryStage.Synthesis), options));
        foreach (var stage in new[] { SummaryStage.None, SummaryStage.Section, SummaryStage.Reduction })
        {
            Assert.Equal(1_024, LlamaServerAnswerEngine.GenerationTokenBudget(Request(stage), options));
        }
    }

    [Theory]
    [InlineData(false, 317, 613)]
    [InlineData(true, 444, 740)]
    public void ExplicitCapsReachTheRequestWithoutBeingRaisedToADefault(
        bool thinking, int expectedSummaryTokens, int expectedOtherTokens)
    {
        var options = new LlamaServerOptions
        {
            ModelPath = "test-model.gguf",
            MaxSummaryTokens = 317,
            MaxAnswerTokens = 613,
            ThinkBeforeAnswer = thinking,
            ThinkingBudgetTokens = 127,
        };
        var requests = new[]
        {
            Request(SummaryStage.None, AnswerMode.Retrieval),
            Request(SummaryStage.None, AnswerMode.WholeTranscript),
            Request(SummaryStage.None, AnswerMode.Survey),
            Request(SummaryStage.None),
            Request(SummaryStage.Section),
            Request(SummaryStage.Reduction),
            Request(SummaryStage.Synthesis),
        };
        foreach (var request in requests)
        {
            var expected = request.SummaryStage == SummaryStage.Synthesis
                ? expectedSummaryTokens : expectedOtherTokens;
            var budget = LlamaServerAnswerEngine.GenerationTokenBudget(request, options);
            Assert.Equal(expected, budget);
            using var body = JsonDocument.Parse(LlamaServerAnswerEngine.BuildRequestBody("rules", "source", null, budget));
            Assert.Equal(expected, body.RootElement.GetProperty("max_tokens").GetInt32());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AFinalSummaryNeedsAPositiveAnswerCap(int cap)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlamaServerOptions
        {
            ModelPath = "test-model.gguf", MaxSummaryTokens = cap,
        });
    }

    [Fact]
    public void ACombinedBudgetCannotOverflowIntoANativeSentinelValue()
    {
        var options = new LlamaServerOptions
        {
            ModelPath = "test-model.gguf",
            MaxSummaryTokens = int.MaxValue,
            ThinkBeforeAnswer = true,
            ThinkingBudgetTokens = 1,
        };
        Assert.Throws<OverflowException>(() =>
            LlamaServerAnswerEngine.GenerationTokenBudget(Request(SummaryStage.Synthesis), options));
    }
}
