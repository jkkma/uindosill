using System.Text;
using System.Text.Json;
using Parakeet.Core.Tidying;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer.Tests;

/// <summary>The tidier's half of the child-process contract: the request it sends and the reply it reads.</summary>
public class LlamaServerTidierTests
{
    [Fact]
    public void TheSlotCountIsSentOnlyWhenNamed()
    {
        var options = new LlamaServerOptions { ModelPath = "/models/some-model-Q8_0.gguf" };

        var plain = LlamaServerProcess.BuildArguments(options, 1, "k");
        Assert.DoesNotContain("-np", plain);

        var four = LlamaServerProcess.BuildArguments(options with { ParallelSlots = 4 }, 1, "k").ToList();
        Assert.Equal("4", four[four.IndexOf("-np") + 1]);
    }

    [Fact]
    public void TheRequestIsOneGreedyUnstreamedTurnUnderTheInstruction()
    {
        var body = LlamaServerTranscriptTidier.BuildRequestBody("um so we we went", 100);
        using var parsed = JsonDocument.Parse(body);
        var root = parsed.RootElement;

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(TidyPromptBuilder.Instruction, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("um so we we went", messages[1].GetProperty("content").GetString());

        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("cache_prompt").GetBoolean());
        Assert.Equal(100, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("grammar", out _));
    }

    [Fact]
    public void TheTokenCapIsTwoAndAHalfPerWordPlusAMargin()
    {
        Assert.Equal(64, TidyPromptBuilder.MaxTokensFor(string.Empty));
        Assert.Equal(64 + 67, TidyPromptBuilder.MaxTokensFor(string.Join(' ', Enumerable.Repeat("w", 27))));
    }

    [Fact]
    public void TheReplyIsTheMessagesContentTrimmedAndACapHitIsRefused()
    {
        var reply = """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"  So we went.\n"}}]}""";
        Assert.Equal("So we went.", LlamaServerTranscriptTidier.ReadContent(reply, 100));

        var capped = """{"choices":[{"finish_reason":"length","message":{"role":"assistant","content":"So we"}}]}""";
        var failure = Assert.Throws<InvalidOperationException>(() => LlamaServerTranscriptTidier.ReadContent(capped, 100));
        Assert.Contains("100-token cap", failure.Message, StringComparison.Ordinal);

        var error = """{"error":{"code":500,"message":"slot unavailable"}}""";
        Assert.Contains("slot unavailable", Assert.Throws<InvalidOperationException>(() => LlamaServerTranscriptTidier.ReadContent(error, 100)).Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => LlamaServerTranscriptTidier.ReadContent("not json", 100));
        Assert.Equal(string.Empty, LlamaServerTranscriptTidier.ReadContent("""{"choices":[{"finish_reason":"stop","message":{"role":"assistant"}}]}""", 100));
    }
}

/// <summary>
/// The tidier against the real child, gated the way the answer engine's tests are: a small GGUF
/// and a vendored drop named in the environment, cpu unless a backend is named.
/// </summary>
public sealed class LlamaServerTidierIntegrationTests
{
    private const string ModelVariable = "UINDOSILL_LLM_TEST_MODEL";
    private const string ServerRootVariable = "UINDOSILL_LLM_SERVER_ROOT";
    private const string BackendVariable = "UINDOSILL_LLM_TEST_BACKEND";

    [Fact]
    public async Task ATidyRunsFourLinesInFlightAgainstTheRealServerAndEveryLineComesBackThroughTheContract()
    {
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        var serverRoot = Environment.GetEnvironmentVariable(ServerRootVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(serverRoot),
            $"Set {ModelVariable} to a small GGUF and {ServerRootVariable} to a native/win-x64/llm directory to run the tidier against the real server.");

        var backend = Environment.GetEnvironmentVariable(BackendVariable) is { Length: > 0 } named
            ? Enum.Parse<ComputeBackend>(named, ignoreCase: true)
            : ComputeBackend.Cpu;

        await using var tidier = new LlamaServerTranscriptTidier(new LlamaServerOptions
        {
            ModelPath = model!,
            Backend = backend,
            ServerRoot = serverRoot,
            ContextSize = 4096,
            ParallelSlots = 4,
        });

        await tidier.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(backend, tidier.Capabilities.Backend);

        var lines = new[]
        {
            "um so the the meeting opened with the budget review",
            "uh Maria presented the the axolotl project",
            "the team agreed agreed to meet again on Friday",
            "and um that was that",
            "we we should go now",
        };

        var document = new TranscriptDocument
        {
            Segments = lines.Select((text, i) => new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(i * 5),
                End = TimeSpan.FromSeconds((i * 5) + 5),
                Text = text,
                SourceSegmentIndex = i,
            }).ToList(),
        };

        var (tidied, summary) = await TranscriptTidy.TidyAsync(
            document, tidier, new TidyOptions { Concurrency = 4 }, ct: TestContext.Current.CancellationToken);

        // What this asserts is the contract and the plumbing, not the model: a small prop model
        // may obey the instruction or not, and either way every line that comes back is either
        // a subsequence of what was spoken or the spoken line itself.
        Assert.Equal(lines.Length, tidied.Segments.Count);
        Assert.Equal(lines.Length, summary.Segments);
        Assert.Equal(tidier.Capabilities.ModelId, tidied.TidyModelId);
        Assert.Equal(backend, tidied.TidyBackend);

        for (var i = 0; i < lines.Length; i++)
        {
            var spoken = Core.Text.TranscriptNormalizer.WordErrorRateTokens(lines[i], keepFillers: false);
            var kept = Core.Text.TranscriptNormalizer.WordErrorRateTokens(tidied.Segments[i].Text, keepFillers: false);
            Assert.True(IsSubsequence(kept, spoken), $"line {i}: '{tidied.Segments[i].Text}' is not a subsequence of '{lines[i]}'");
            Assert.Equal(document.Segments[i].Start, tidied.Segments[i].Start);
        }
    }

    private static bool IsSubsequence(string[] candidate, string[] spoken)
    {
        var at = 0;
        foreach (var token in candidate)
        {
            while (at < spoken.Length && spoken[at] != token)
            {
                at++;
            }

            if (at == spoken.Length)
            {
                return false;
            }

            at++;
        }

        return true;
    }
}
