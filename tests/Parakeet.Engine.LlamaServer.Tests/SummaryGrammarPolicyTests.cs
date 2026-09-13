using System.Text.Json;
using Parakeet.Core.Answers;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer.Tests;

public sealed class SummaryGrammarPolicyTests
{
    private static AskRequest Request(SummaryStage stage, AnswerMode mode = AnswerMode.MapReduce)
    {
        var transcript = new TranscriptDocument
        {
            Segments = Enumerable.Range(1, 6).Select(index => new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds((index - 1) * 10),
                End = TimeSpan.FromSeconds(index * 10),
                Text = $"Original source segment {index}.",
            }).ToArray(),
        };
        return new AskRequest
        {
            Question = "Summarize the recording",
            Transcript = transcript,
            Mode = mode,
            SummaryStage = stage,
            Evidence =
            [
                TranscriptWindowBuilder.FromRun(transcript, 1, 2),
                TranscriptWindowBuilder.FromRun(transcript, 5, 6),
            ],
        };
    }

    [Theory]
    [InlineData(SummaryStage.Section)]
    [InlineData(SummaryStage.Reduction)]
    [InlineData(SummaryStage.Synthesis)]
    public void ExplicitlyConstrainedSummaryPassesSendOnlyTheirSuppliedCitationRuns(SummaryStage stage)
    {
        var request = Request(stage);
        var options = new LlamaServerOptions { ModelPath = "test-model.gguf", UseGrammar = true };
        var grammar = LlamaServerAnswerEngine.BuildCitationGrammar(request, options,
            allowAbstain: false, requireQuote: false, wantLead: stage == SummaryStage.Synthesis);

        Assert.NotNull(grammar);
        Assert.Equal("cite ::= \"S1-S2\" | \"S5-S6\"", grammar.Split('\n').Single(line => line.StartsWith("cite ::=", StringComparison.Ordinal)));
        Assert.DoesNotContain("\"S1-S6\"", grammar, StringComparison.Ordinal);
        Assert.DoesNotContain("\"S3\"", grammar, StringComparison.Ordinal);
        Assert.Equal(stage == SummaryStage.Synthesis, grammar.Contains("\"[?]\"", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(LlamaServerAnswerEngine.BuildRequestBody("rules", "source", grammar));
        Assert.Equal(grammar, body.RootElement.GetProperty("grammar").GetString());
    }

    [Fact]
    public void TheGrammarCanForbidUncitedNotesWithoutChangingTheExistingDefault()
    {
        var evidence = Request(SummaryStage.Section).Evidence;
        var ordinary = AnswerPromptBuilder.BuildGrammar(evidence)!;
        var citedOnly = AnswerPromptBuilder.BuildGrammar(evidence, allowUncited: false)!;

        Assert.Contains(" | \"[?]\"", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("\"[?]\"", citedOnly, StringComparison.Ordinal);
        Assert.Equal("cites ::= \"[\" cite (\", \" cite){0,4} \"]\"",
            citedOnly.Split('\n').Single(line => line.StartsWith("cites ::=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryRequestModeHonorsTheExplicitGrammarPreference(bool useGrammar)
    {
        var options = new LlamaServerOptions { ModelPath = "test-model.gguf", UseGrammar = useGrammar };
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
            var notesOnly = request.SummaryStage is SummaryStage.Section or SummaryStage.Reduction;
            var grammar = LlamaServerAnswerEngine.BuildCitationGrammar(request, options,
                allowAbstain: !notesOnly, requireQuote: !notesOnly, wantLead: !notesOnly);
            var expected = useGrammar
                ? AnswerPromptBuilder.BuildGrammar(request.Evidence, allowAbstain: !notesOnly,
                    requireQuote: !notesOnly, wantLead: !notesOnly, allowUncited: !notesOnly)
                : null;
            Assert.Equal(expected, grammar);
            using var body = JsonDocument.Parse(LlamaServerAnswerEngine.BuildRequestBody("rules", "source", grammar));
            Assert.Equal(useGrammar, body.RootElement.TryGetProperty("grammar", out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThinkingNeverReceivesAnEagerGrammarIncludingEverySummaryStage(bool useGrammar)
    {
        var options = new LlamaServerOptions
        {
            ModelPath = "test-model.gguf", UseGrammar = useGrammar, ThinkBeforeAnswer = true,
        };
        foreach (var stage in Enum.GetValues<SummaryStage>())
        {
            var grammar = LlamaServerAnswerEngine.BuildCitationGrammar(Request(stage), options,
                allowAbstain: true, requireQuote: false, wantLead: true);
            Assert.Null(grammar);
            using var body = JsonDocument.Parse(LlamaServerAnswerEngine.BuildRequestBody("rules", "source", grammar));
            Assert.False(body.RootElement.TryGetProperty("grammar", out _));
        }
    }
}
