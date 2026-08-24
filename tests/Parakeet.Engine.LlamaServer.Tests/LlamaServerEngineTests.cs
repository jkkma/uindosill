using System.Text;
using System.Text.Json;
using Parakeet.Core.Answers;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer.Tests;

public class LlamaServerLocatorTests
{
    private static string Root(params string[] backendsWithServer)
    {
        var root = Directory.CreateTempSubdirectory("uindosill-llm").FullName;
        foreach (var backend in backendsWithServer)
        {
            var directory = Path.Combine(root, backend);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "llama-server.exe"), "not really");
        }

        return root;
    }

    [Fact]
    public void TheBestPresentBackendWins()
    {
        var root = Root("cpu", "vulkan");

        var install = LlamaServerLocator.TryFind(root: root);

        Assert.NotNull(install);
        Assert.Equal(ComputeBackend.Vulkan, install!.Backend);
        Assert.EndsWith("llama-server.exe", install.ExecutablePath, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitBackendIsNotFallenAwayFrom()
    {
        // A caller who asked for CUDA and silently got CPU is the fallback failure the ASR tier
        // documents; asking is binding, and absence is an answer.
        var root = Root("cpu", "vulkan");

        Assert.Null(LlamaServerLocator.TryFind(ComputeBackend.Cuda, root));
        Assert.Equal(ComputeBackend.Cpu, LlamaServerLocator.TryFind(ComputeBackend.Cpu, root)!.Backend);
    }

    [Fact]
    public void NothingVendoredIsNullNotAnError()
    {
        Assert.Null(LlamaServerLocator.TryFind(root: Root()));
    }
}

public class LlamaServerArgumentTests
{
    private static LlamaServerOptions Options() => new() { ModelPath = "/models/some-model-Q8_0.gguf" };

    private static List<string> Arguments(LlamaServerOptions options, int port, string apiKey) =>
        [.. LlamaServerProcess.BuildArguments(options, port, apiKey)];

    [Fact]
    public void TheChildBindsLoopbackWithAKeyAndNoUiAndNoFit()
    {
        var arguments = Arguments(Options(), 4242, "sekrit");

        Assert.Contains("--host", arguments);
        Assert.Equal("127.0.0.1", arguments[arguments.IndexOf("--host") + 1]);
        Assert.Equal("4242", arguments[arguments.IndexOf("--port") + 1]);
        Assert.Equal("sekrit", arguments[arguments.IndexOf("--api-key") + 1]);
        Assert.Contains("--no-webui", arguments);

        // --fit on trims layers and context to what fits, so a model that does not fit still
        // runs, silently degraded — the register calls it a way to be fooled, and it is off.
        Assert.Equal("off", arguments[arguments.IndexOf("--fit") + 1]);
    }

    [Fact]
    public void FlashAttentionIsSentOnlyWhenChosen()
    {
        Assert.DoesNotContain("-fa", Arguments(Options(), 1, "k"));

        var chosen = Arguments(Options() with { FlashAttention = "off" }, 1, "k");
        Assert.Equal("off", chosen[chosen.IndexOf("-fa") + 1]);
    }

    [Fact]
    public void VulkanGetsTheBf16KnobUnlessTheCallerSaysOtherwise()
    {
        // The laptop's driver hangs at model load without it — measured 2026-08-16,
        // docs/UNPROVEN.md — and a hang is strictly worse than bf16 being unavailable.
        var vulkan = LlamaServerProcess.BuildEnvironment(
            ComputeBackend.Vulkan, new Dictionary<string, string>());
        Assert.Equal("1", vulkan["GGML_VK_DISABLE_BFLOAT16"]);

        var overridden = LlamaServerProcess.BuildEnvironment(
            ComputeBackend.Vulkan, new Dictionary<string, string> { ["GGML_VK_DISABLE_BFLOAT16"] = "0" });
        Assert.Equal("0", overridden["GGML_VK_DISABLE_BFLOAT16"]);

        Assert.Empty(LlamaServerProcess.BuildEnvironment(ComputeBackend.Cpu, new Dictionary<string, string>()));
    }
}

public class AnswerPromptBuilderTests
{
    private static TranscriptDocument Transcript(params string[] texts)
    {
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < texts.Length; i++)
        {
            segments.Add(new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(i * 10),
                End = TimeSpan.FromSeconds((i * 10) + 10),
                Text = texts[i],
            });
        }

        return new TranscriptDocument { Segments = segments };
    }

    private static AskRequest Request()
    {
        var transcript = Transcript("first stretch of speech", "second stretch", "third stretch");
        return new AskRequest
        {
            Question = "what was said?",
            Transcript = transcript,
            Evidence =
            [
                TranscriptWindowBuilder.FromRun(transcript, 1, 2),
                TranscriptWindowBuilder.FromRun(transcript, 3, 3),
            ],
        };
    }

    [Fact]
    public void ThePromptCarriesTheEvidenceByIdTheQuestionAndTheAbstainSentinel()
    {
        var prompt = AnswerPromptBuilder.BuildPrompt(Request());

        Assert.Contains("[S1-S2] first stretch of speech second stretch", prompt, StringComparison.Ordinal);
        Assert.Contains("[S3] third stretch", prompt, StringComparison.Ordinal);
        Assert.Contains("what was said?", prompt, StringComparison.Ordinal);
        Assert.Contains(AnswerParser.AbstainSentinel, prompt, StringComparison.Ordinal);
        Assert.Contains("Never write a timestamp", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLanguageLineAppearsOnlyWhenKnown()
    {
        Assert.DoesNotContain("BCP-47", AnswerPromptBuilder.BuildPrompt(Request()), StringComparison.Ordinal);
        Assert.Contains(
            "BCP-47 tag is: de",
            AnswerPromptBuilder.BuildPrompt(Request() with { Language = "de" }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheGrammarEnumeratesEveryLiveIdAndNoOther()
    {
        // The register's decision 6 bullet: every live id in, no other id possible. The ids are
        // literal alternatives, so the only S-tokens in the grammar are the evidence's own.
        var grammar = AnswerPromptBuilder.BuildGrammar(Request().Evidence)!;

        Assert.Contains("\"S1-S2\"", grammar, StringComparison.Ordinal);
        Assert.Contains("\"S3\"", grammar, StringComparison.Ordinal);

        var sTokens = System.Text.RegularExpressions.Regex.Matches(grammar, "\"S[0-9-S]*\"")
            .Select(m => m.Value)
            .Distinct()
            .ToList();
        Assert.Equal(2, sTokens.Count);
    }

    [Fact]
    public void TheAbstainAndQuoteProductionsAreDials()
    {
        var evidence = Request().Evidence;

        var full = AnswerPromptBuilder.BuildGrammar(evidence, allowAbstain: true, requireQuote: true)!;
        Assert.Contains(AnswerParser.AbstainSentinel, full, StringComparison.Ordinal);
        Assert.Contains("quote ::=", full, StringComparison.Ordinal);

        var neither = AnswerPromptBuilder.BuildGrammar(evidence, allowAbstain: false, requireQuote: false)!;
        Assert.DoesNotContain(AnswerParser.AbstainSentinel, neither, StringComparison.Ordinal);
        Assert.DoesNotContain("quote", neither, StringComparison.Ordinal);

        // The uncited marker is not a dial: [?] must stay expressible under every grammar, or
        // the grammar forces the model to invent an id when it has none.
        Assert.Contains("[?]", full, StringComparison.Ordinal);
        Assert.Contains("[?]", neither, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEvidenceMeansNoGrammar()
    {
        // A grammar over an empty id set could only cite [?]; whether that or an unconstrained
        // answer is the honest fallback is the caller's call, so the builder refuses to guess.
        Assert.Null(AnswerPromptBuilder.BuildGrammar([]));
    }
}

public class SseReaderTests
{
    private static async Task<List<string>> Read(string raw)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var payloads = new List<string>();
        await foreach (var payload in SseReader.ReadPayloadsAsync(stream))
        {
            payloads.Add(payload);
        }

        return payloads;
    }

    [Fact]
    public async Task PayloadsAreReadEventByEvent()
    {
        var payloads = await Read("data: {\"content\":\"a\"}\n\ndata: {\"content\":\"b\"}\n\n");

        Assert.Equal(["{\"content\":\"a\"}", "{\"content\":\"b\"}"], payloads);
    }

    [Fact]
    public async Task CommentsUnknownFieldsAndAMissingFinalBlankLineAreTolerated()
    {
        var payloads = await Read(": keepalive\nevent: message\ndata: {\"x\":1}");

        Assert.Equal(["{\"x\":1}"], payloads);
    }

    [Fact]
    public async Task MultiLineDataJoinsWithNewlines()
    {
        var payloads = await Read("data: one\ndata: two\n\n");

        Assert.Equal(["one\ntwo"], payloads);
    }
}

public class RequestBodyTests
{
    [Fact]
    public void TheBodySaysWhatWasDecided()
    {
        var body = LlamaServerAnswerEngine.BuildRequestBody("the prompt", "the grammar", 512);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal("the prompt", root.GetProperty("prompt").GetString());
        Assert.Equal("the grammar", root.GetProperty("grammar").GetString());
        Assert.Equal(512, root.GetProperty("n_predict").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("cache_prompt").GetBoolean());
        Assert.True(root.GetProperty("return_progress").GetBoolean());
    }

    [Fact]
    public void NoGrammarMeansNoGrammarField()
    {
        using var json = JsonDocument.Parse(LlamaServerAnswerEngine.BuildRequestBody("p", null));

        Assert.False(json.RootElement.TryGetProperty("grammar", out _));
    }
}

public class QuantisationNameTests
{
    [Theory]
    [InlineData("Qwen3.5-9B-Q8_0.gguf", "Q8_0")]
    [InlineData("gemma-4-12b-it-Q6_K.gguf", "Q6_K")]
    [InlineData("Qwen3.5-9B-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("Qwen3.6-35B-A3B-UD-IQ4_XS.gguf", "UD-IQ4_XS")]
    [InlineData("gpt-oss-20b-MXFP4.gguf", "MXFP4")]
    [InlineData("model-bf16.gguf", "BF16")]
    [InlineData("no-quant-here.gguf", null)]
    public void TheQuantisationIsReadFromTheFileName(string fileName, string? expected) =>
        Assert.Equal(expected, LlamaServerAnswerEngine.TryReadQuantisation(fileName));
}

/// <summary>
/// The engine against a real server and a real (tiny) model. Needs the vendored drop and a GGUF,
/// neither of which is in the clone, so these skip unless both are named — the same arrangement
/// as the Silero tests, and for the same reason: a count that depends on what is installed
/// cannot be written into a document CI checks.
/// </summary>
public sealed class LlamaServerIntegrationTests
{
    private const string ModelVariable = "UINDOSILL_LLM_TEST_MODEL";
    private const string ServerRootVariable = "UINDOSILL_LLM_SERVER_ROOT";

    /// <summary>cpu unless named: the laptop names vulkan to exercise the bf16 default on the
    /// driver that hangs without it, and the desktop names cuda for the sm_120 corroboration.</summary>
    private const string BackendVariable = "UINDOSILL_LLM_TEST_BACKEND";

    [Fact]
    public async Task AnAskStreamsParsesAndResolvesAgainstTheRealServer()
    {
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        var serverRoot = Environment.GetEnvironmentVariable(ServerRootVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(serverRoot),
            $"Set {ModelVariable} to a small GGUF and {ServerRootVariable} to a native/win-x64/llm directory to run the engine against the real server.");

        var transcript = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "The meeting opened with the quarterly budget review." },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(20), Text = "Maria presented the axolotl conservation project." },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(30), Text = "The team agreed to meet again on Friday." },
            ],
            AudioDuration = TimeSpan.FromSeconds(30),
        };

        var backend = Environment.GetEnvironmentVariable(BackendVariable) is { Length: > 0 } named
            ? Enum.Parse<ComputeBackend>(named, ignoreCase: true)
            : ComputeBackend.Cpu;

        await using var engine = new LlamaServerAnswerEngine(new LlamaServerOptions
        {
            ModelPath = model!,
            Backend = backend,
            ServerRoot = serverRoot,
            ContextSize = 4096,
            MaxAnswerTokens = 256,
        });

        await engine.LoadAsync(TestContext.Current.CancellationToken);

        var chunks = new List<string>();
        var request = new AskRequest
        {
            Question = "What did Maria present?",
            Transcript = transcript,
            Evidence =
            [
                TranscriptWindowBuilder.FromRun(transcript, 2, 2),
                TranscriptWindowBuilder.FromRun(transcript, 1, 1),
            ],
        };

        await foreach (var chunk in engine.AskAsync(request, ct: TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        var text = string.Concat(chunks);
        Assert.NotEmpty(text);

        // The whole seam, against the real thing: stream → parser → validator. What is asserted
        // is the engine's guarantee, not the model's quality: the grammar makes an id that is
        // not live unsamplable, so every citation must RESOLVE. Quote fidelity is a per-model
        // dial the validator measures rather than a property this engine can promise — the first
        // real run showed the 0.6B dutifully quoting «S2>», a fluent three-character invention
        // the check caught, which is the check working. The raw text travels with a failure,
        // because "false" alone says nothing about what the model actually emitted.
        var answer = AnswerParser.Parse(text);
        Assert.True(answer.Abstained || answer.Bullets.Count > 0, $"nothing parsed out of:\n{text}");

        var validation = CitationValidator.Validate(answer, transcript);
        foreach (var citation in validation.Bullets.SelectMany(b => b.Citations))
        {
            if (citation.Citation.IsUncitedMarker)
            {
                continue;
            }

            Assert.True(
                citation.Check.Resolves && citation.Check.NonEmpty && citation.Check.WithinDuration,
                $"citation [{citation.Citation.Raw}] does not resolve, in:\n{text}");
            Assert.NotNull(citation.Start);
            Assert.NotNull(citation.End);
        }
    }
}
