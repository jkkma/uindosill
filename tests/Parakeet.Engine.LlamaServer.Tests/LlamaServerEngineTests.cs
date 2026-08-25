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

    private static List<string> Arguments(LlamaServerOptions options, int port, string apiKeyFile) =>
        [.. LlamaServerProcess.BuildArguments(options, port, apiKeyFile)];

    [Fact]
    public void TheChildBindsLoopbackWithAKeyFileAndNoUiAndNoFit()
    {
        var arguments = Arguments(Options(), 4242, "/tmp/sekrit.key");

        Assert.Contains("--host", arguments);
        Assert.Equal("127.0.0.1", arguments[arguments.IndexOf("--host") + 1]);
        Assert.Equal("4242", arguments[arguments.IndexOf("--port") + 1]);

        // The file, never the key: a child's command line is readable by any same-user process
        // for as long as it runs, so the key itself must appear in no argument.
        Assert.Equal("/tmp/sekrit.key", arguments[arguments.IndexOf("--api-key-file") + 1]);
        Assert.DoesNotContain("--api-key", arguments);
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
    public void TheTemplateIsAppliedAndReasoningRoutingFollowsTheThinkingMode()
    {
        // --jinja always: the raw-prompt path was measured leaving models unable to stop
        // (2026-08-24). --reasoning-format none belongs to the grammar mode alone — it keeps
        // the grammar-shaped stream in content; everywhere else the server's reasoning parsing
        // is what keeps a template's thought channel out of the answer stream.
        var ungrammared = Arguments(Options(), 1, "k");
        Assert.Contains("--jinja", ungrammared);
        Assert.DoesNotContain("--reasoning-format", ungrammared);

        var grammarMode = Arguments(Options() with { UseGrammar = true }, 1, "k");
        Assert.Contains("--jinja", grammarMode);
        Assert.Equal("none", grammarMode[grammarMode.IndexOf("--reasoning-format") + 1]);

        var thinking = Arguments(Options() with { ThinkBeforeAnswer = true }, 1, "k");
        Assert.Contains("--jinja", thinking);
        Assert.DoesNotContain("--reasoning-format", thinking);
    }

    [Fact]
    public void VulkanGetsTheBf16KnobUnlessTheCallerSaysOtherwise()
    {
        // The laptop's driver hangs at model load without the bf16 knob (measured 2026-08-16),
        // and a 26B-class mixture cannot load at all without the expert-offload pair (measured
        // 2026-08-24, the app's own failed load) — docs/UNPROVEN.md carries both.
        var vulkan = LlamaServerProcess.BuildEnvironment(
            ComputeBackend.Vulkan, new Dictionary<string, string>());
        Assert.Equal("1", vulkan["GGML_VK_DISABLE_BFLOAT16"]);
        Assert.Equal("1", vulkan["LLAMA_ARG_CPU_MOE"]);
        Assert.Equal("1", vulkan["LLAMA_ARG_NO_HOST"]);

        var overridden = LlamaServerProcess.BuildEnvironment(
            ComputeBackend.Vulkan, new Dictionary<string, string>
            {
                ["GGML_VK_DISABLE_BFLOAT16"] = "0",
                ["LLAMA_ARG_CPU_MOE"] = "0",
            });
        Assert.Equal("0", overridden["GGML_VK_DISABLE_BFLOAT16"]);
        Assert.Equal("0", overridden["LLAMA_ARG_CPU_MOE"]);

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
    public void TheMessagesSplitTheContractWithoutChangingIt()
    {
        // The chat form: rules in the system message, evidence and question in the user
        // message, no "Answer:" cue — the template's assistant turn is that cue, and with it
        // the end-of-turn the raw-prompt path was measured to lack (2026-08-24). BuildPrompt
        // remains the two halves joined, so the two forms cannot drift apart.
        var (instruction, userContent) = AnswerPromptBuilder.BuildMessages(Request());

        Assert.Contains("You are answering questions", instruction, StringComparison.Ordinal);
        Assert.Contains(AnswerParser.AbstainSentinel, instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("[S1-S2]", instruction, StringComparison.Ordinal);

        Assert.Contains("[S1-S2] first stretch of speech second stretch", userContent, StringComparison.Ordinal);
        Assert.Contains("Question: what was said?", userContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Answer:", userContent, StringComparison.Ordinal);

        Assert.Equal(instruction + "\n" + userContent + "Answer:\n", AnswerPromptBuilder.BuildPrompt(Request()));
    }

    [Fact]
    public void ThePromptsDialsMatchTheGrammars()
    {
        // With the abstain production off, an instruction to "reply exactly NOT_IN_TRANSCRIPT"
        // steers the model toward an output the grammar makes unsamplable — measured as degraded
        // answers, not as nothing — and a quote instruction without a quote production is the
        // same shape. The prompt's dials must say only what the grammar can sample.
        var neither = AnswerPromptBuilder.BuildPrompt(Request(), allowAbstain: false, requireQuote: false);
        Assert.DoesNotContain(AnswerParser.AbstainSentinel, neither, StringComparison.Ordinal);
        Assert.DoesNotContain("verbatim", neither, StringComparison.Ordinal);

        var both = AnswerPromptBuilder.BuildPrompt(Request());
        Assert.Contains(AnswerParser.AbstainSentinel, both, StringComparison.Ordinal);
        Assert.Contains("verbatim", both, StringComparison.Ordinal);
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
    public void TheQuoteProductionAdmitsNoCitationBrackets()
    {
        // The parser lifts citations from the whole bullet before it lifts the quote, so a
        // grammar whose quote production admitted brackets would let a "grammar-constrained"
        // model write an id inside «…» and have it promoted to a real citation — model-authored
        // text becoming a rendered time, the central prohibition. The quote's character class
        // must exclude the brackets exactly as free text's does.
        var grammar = AnswerPromptBuilder.BuildGrammar(Request().Evidence, requireQuote: true)!;

        Assert.Contains(
            "quote ::= \"\\u00AB\" [^\\n\\[\\]\\u00AB\\u00BB]{8,300} \"\\u00BB\"",
            grammar,
            StringComparison.Ordinal);
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
        var body = LlamaServerAnswerEngine.BuildRequestBody("the rules", "the evidence", "the grammar", 512);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        // Messages, not a raw prompt: the template's turn structure is what end-of-turn was
        // trained against, and the raw-prompt path was measured leaving every candidate model
        // unable to stop (2026-08-24, docs/UNPROVEN.md).
        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("the rules", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("the evidence", messages[1].GetProperty("content").GetString());

        Assert.Equal("the grammar", root.GetProperty("grammar").GetString());
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("cache_prompt").GetBoolean());
        Assert.True(root.GetProperty("return_progress").GetBoolean());
        Assert.False(root.TryGetProperty("prompt", out _));
    }

    [Fact]
    public void NoGrammarMeansNoGrammarField()
    {
        using var json = JsonDocument.Parse(LlamaServerAnswerEngine.BuildRequestBody("r", "e", null));

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

            // Pinned on: this test asserts the GRAMMAR's guarantee (every citation resolves),
            // which only holds when the grammar decodes. The shipped default is ungrammared
            // since 2026-08-25; the test below carries that mode's weaker post-hoc contract.
            UseGrammar = true,
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

    [Fact]
    public async Task ThinkingModeStreamsAnAnswerAndTerminates()
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
            ],
            AudioDuration = TimeSpan.FromSeconds(20),
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
            ThinkBeforeAnswer = true,
        });

        await engine.LoadAsync(TestContext.Current.CancellationToken);

        // The thinking mode's contract is weaker by design and this asserts exactly it: the
        // stream terminates on its own (the raw-prompt path was measured never stopping,
        // 2026-08-24), the answer parses, and whatever thinking happened never reaches this
        // stream — no grammar, so citation resolution is the validator's business, not a
        // guarantee. The 0.6B's template opens no think block; a thinking template's routing
        // is the lab's to measure per model.
        var chunks = new List<string>();
        var progress = new List<AskProgress>();
        var request = new AskRequest
        {
            Question = "What did Maria present?",
            Transcript = transcript,
            Evidence = [TranscriptWindowBuilder.FromRun(transcript, 2, 2)],
        };

        await foreach (var chunk in engine.AskAsync(
            request, new Progress<AskProgress>(progress.Add), TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        var text = string.Concat(chunks);
        Assert.NotEmpty(text);
        Assert.DoesNotContain("<think>", text, StringComparison.Ordinal);

        var answer = AnswerParser.Parse(text);
        Assert.True(answer.Abstained || answer.Bullets.Count > 0, $"nothing parsed out of:\n{text}");
    }
}
