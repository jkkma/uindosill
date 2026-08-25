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
        var root = TestTemp.NewDirectory("uindosill-llm");
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
    public void TheThinkingModeDecidesWhetherTheModelThinksAtAll()
    {
        // The defect this pins, measured 2026-08-25 and shipped until then: `--reasoning`
        // defaults to `auto`, so the model's template decided and a thinking model thought
        // whatever this setting said — `--reasoning-format` only chose where the thought text
        // was filed, and under the default parse the engine dropped it, so the answer budget
        // could be spent before one content token existed. The 26B-A4B produced nothing at all
        // in 79.4 s under `auto` and a full cited overview in 45.5 s under `off`.
        var off = Arguments(Options(), 1, "k");
        Assert.Equal("off", off[off.IndexOf("--reasoning") + 1]);

        var on = Arguments(Options() with { ThinkBeforeAnswer = true }, 1, "k");
        Assert.Equal("on", on[on.IndexOf("--reasoning") + 1]);

        // The grammar mode is a non-thinking mode too, and says so for the same reason.
        var grammarMode = Arguments(Options() with { UseGrammar = true }, 1, "k");
        Assert.Equal("off", grammarMode[grammarMode.IndexOf("--reasoning") + 1]);
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
    public void TheOverviewPromptAsksForCoverageGroupingAndAFramingSentence()
    {
        // The whole-transcript instruction is a different job from the retrieval one, not a
        // longer one: the model holds the recording rather than a shortlist already judged
        // relevant, so what it needs told is coverage and grouping — and the framing sentence
        // the panel renders above the claims.
        var whole = Request() with { Mode = AnswerMode.WholeTranscript };
        var (instruction, userContent) = AnswerPromptBuilder.BuildMessages(whole, requireQuote: false);

        Assert.Contains("complete transcript", instruction, StringComparison.Ordinal);
        Assert.Contains("short topic label", instruction, StringComparison.Ordinal);
        Assert.Contains("whole recording", instruction, StringComparison.Ordinal);
        Assert.Contains("Cite every part", instruction, StringComparison.Ordinal);
        Assert.Contains("Never write a timestamp", instruction, StringComparison.Ordinal);

        // The evidence is the transcript here, and it says so.
        Assert.Contains("Transcript:", userContent, StringComparison.Ordinal);
        Assert.Contains("[S1-S2] first stretch of speech second stretch", userContent, StringComparison.Ordinal);

        // Retrieval keeps its own framing and its own rules. Both modes label their bullets —
        // labels read well in either, and a reader scanning an enumeration wants them — but
        // only the overview is told to cover the recording and cite every discussing part,
        // which are the two things a summary is graded on and a pointed answer never needs.
        var (retrieval, _) = AnswerPromptBuilder.BuildMessages(Request());
        Assert.Contains("from transcript evidence", retrieval, StringComparison.Ordinal);
        Assert.Contains("short topic label", retrieval, StringComparison.Ordinal);
        Assert.DoesNotContain("Cite every part", retrieval, StringComparison.Ordinal);
        Assert.DoesNotContain("draw on the whole recording", retrieval, StringComparison.Ordinal);
        Assert.Contains("make sense on its own", retrieval, StringComparison.Ordinal);

        // And a question with one answer may be answered in one sentence: forcing bullets under
        // a "yes" made the panel restate its own opening. The lead carries ids either way, so
        // stopping there costs no citation.
        Assert.Contains("answers the question completely, write nothing more", retrieval, StringComparison.Ordinal);
    }

    [Fact]
    public void BothModesOpenWithASentenceAnsweringTheQuestion()
    {
        // Added to retrieval 2026-08-25, on the maintainer reading a real answer: a list of
        // cited fragments never says the "yes" a yes-or-no question asked for, and a fragment
        // lifted out of a digression reads as a non-sequitur with a timestamp on it. The
        // wording is one job in both modes — answer what was asked — because for "give me a
        // summary" that is what the recording covers, and for "did they mention X" it is yes.
        var (retrieval, _) = AnswerPromptBuilder.BuildMessages(Request());
        var (whole, _) = AnswerPromptBuilder.BuildMessages(
            Request() with { Mode = AnswerMode.WholeTranscript }, requireQuote: false);

        Assert.Contains("Open with one sentence answering the question directly", retrieval, StringComparison.Ordinal);
        Assert.Contains("Open with one sentence answering the question directly", whole, StringComparison.Ordinal);

        // The opening sentence carries ids like any other line, and that clause is load-bearing:
        // without it the model wrote good openings and cited none of them, so every answer led
        // with a line the panel had to mark.
        Assert.Contains("ending with ids like every other line", retrieval, StringComparison.Ordinal);

        // And the grammar admits one wherever the prompt asks for it — the two are one contract.
        var grammar = AnswerPromptBuilder.BuildGrammar(Request().Evidence, wantLead: true)!;
        Assert.Contains("lead ::=", grammar, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverviewIsToldNotToWriteSectionHeadings()
    {
        // The topic-label instruction invites them, and a heading is a line that asserts
        // nothing, cites nothing, and therefore renders as an unsupported claim — real ones
        // observed 2026-08-25. The maintainer's decision the same day: forbid them in the prompt
        // rather than guess at them in the parser, since a heuristic over "uncited line ending
        // in a colon" would eventually swallow a real claim, and the bullets' own topic labels
        // already group what a heading would have grouped.
        var (whole, _) = AnswerPromptBuilder.BuildMessages(
            Request() with { Mode = AnswerMode.WholeTranscript }, requireQuote: false);

        Assert.Contains("Do not write section headings", whole, StringComparison.Ordinal);
        Assert.Contains("short topic label", whole, StringComparison.Ordinal);

        // Retrieval never wrote one and does not carry the line — it has no topic-label
        // instruction to invite it, and prompt length is not free.
        var (retrieval, _) = AnswerPromptBuilder.BuildMessages(Request());
        Assert.DoesNotContain("section headings", retrieval, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRecordingsFileNameIsOfferedForNamingAndFencedFromEvidence()
    {
        // The file name is provenance the application holds and the transcript does not — it is
        // how a person refers to the recording. Fenced on purpose: a claim sourced from a file
        // name would be the one line in an answer with no segment behind it. The directory never
        // travels; the prompt has no use for the user's folder structure.
        var request = Request();
        var named = request with
        {
            Mode = AnswerMode.WholeTranscript,
            Transcript = request.Transcript with
            {
                SourceName = Path.Combine("C:", "Users", "someone", "Castle Super Beast 287.mp3"),
            },
        };

        var (instruction, _) = AnswerPromptBuilder.BuildMessages(named, requireQuote: false);
        Assert.Contains("\"Castle Super Beast 287\"", instruction, StringComparison.Ordinal);
        Assert.Contains("never as a fact about its contents", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(".mp3", instruction, StringComparison.Ordinal);

        // Nameless recordings simply do not get the line, and retrieval never does.
        var (nameless, _) = AnswerPromptBuilder.BuildMessages(
            request with { Mode = AnswerMode.WholeTranscript }, requireQuote: false);
        Assert.DoesNotContain("file is named", nameless, StringComparison.Ordinal);

        var (retrieval, _) = AnswerPromptBuilder.BuildMessages(named with { Mode = AnswerMode.Retrieval });
        Assert.DoesNotContain("file is named", retrieval, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLeadHasAGrammarProductionExactlyWhereThePromptAsksForOne()
    {
        // This file's stated principle: prompt and grammar are two statements of one contract.
        // An instruction to open with a framing sentence, under a grammar that cannot sample
        // one, is the same defect as an abstain instruction with no abstain production.
        var evidence = Request().Evidence;

        var withLead = AnswerPromptBuilder.BuildGrammar(evidence, wantLead: true)!;
        Assert.Contains("lead ::=", withLead, StringComparison.Ordinal);
        Assert.Contains("root ::= abstain | lead bullet{1,8}", withLead, StringComparison.Ordinal);

        var without = AnswerPromptBuilder.BuildGrammar(evidence)!;
        Assert.DoesNotContain("lead", without, StringComparison.Ordinal);
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

    [Fact]
    public async Task TheWholeTranscriptModeAsksOverTheRecordingAndTerminates()
    {
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        var serverRoot = Environment.GetEnvironmentVariable(ServerRootVariable);
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(serverRoot),
            $"Set {ModelVariable} to a small GGUF and {ServerRootVariable} to a native/win-x64/llm directory to run the engine against the real server.");

        var transcript = new TranscriptDocument
        {
            SourceName = "Quarterly Review Meeting.wav",
            AudioDuration = TimeSpan.FromSeconds(40),
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "The meeting opened with the quarterly budget review." },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(20), Text = "Maria presented the axolotl conservation project." },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(30), Text = "Then the team argued about the catering budget for an hour." },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(30), End = TimeSpan.FromSeconds(40), Text = "The team agreed to meet again on Friday." },
            ],
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

        // The whole-transcript path end to end against a real child: the evidence is the
        // recording tiled once, the ask is global, and the stream has to stop on its own. What
        // is asserted is the engine's and the parser's, never the model's judgement — a 0.6B's
        // summary is not evidence about a 26B's, and coverage is what the labelled set will
        // measure. The lead is not required: whether a model TAKES the framing sentence is a
        // per-model behaviour, so this pins that when one is produced it parses as the lead and
        // never as a claim, and that every resolving citation names a real span.
        var chunks = new List<string>();
        var request = new AskRequest
        {
            Question = "Give me a summary of this recording.",
            Transcript = transcript,
            Mode = AnswerMode.WholeTranscript,
            Evidence = TranscriptWindowBuilder.Build(transcript, TranscriptWindowOptions.Cover),
        };

        await foreach (var chunk in engine.AskAsync(request, ct: TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        var text = string.Concat(chunks);
        Assert.NotEmpty(text);

        var answer = AnswerParser.Parse(text, allowLead: true);
        Assert.True(
            answer.Abstained || answer.Bullets.Count > 0 || answer.Lead is not null,
            $"nothing parsed out of:\n{text}");

        // No forced quote on this path — an overview bullet is a synthesis across minutes, so
        // the prompt does not ask for one and the model has no quote instruction to obey.
        Assert.All(answer.Bullets, b => Assert.Null(b.Quote));

        var validation = CitationValidator.Validate(answer, transcript);
        var resolved = validation.Bullets.Concat(validation.Lead is { } lead ? [lead] : Array.Empty<ResolvedBullet>());
        foreach (var citation in resolved.SelectMany(b => b.Citations))
        {
            if (citation.Citation.IsUncitedMarker || !citation.Check.Resolves)
            {
                // Ungrammared, an invented id is possible and renders unresolved — the post-hoc
                // contract, not a failure of this test.
                continue;
            }

            Assert.True(
                citation.Check.NonEmpty && citation.Check.WithinDuration,
                $"citation [{citation.Citation.Raw}] resolved to nothing usable, in:\n{text}");
            Assert.NotNull(citation.Start);
        }
    }
}
