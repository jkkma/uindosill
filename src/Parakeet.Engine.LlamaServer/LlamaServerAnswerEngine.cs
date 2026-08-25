using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parakeet.Core.Answers;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer;

/// <summary>
/// <see cref="IAnswerEngine"/> over a bundled <c>llama-server</c> child process — the v2
/// language-model tier as decision 1 of `docs/V2-ASK-THE-TRANSCRIPT.md` decided it: the same
/// upstream release zips vendored under `native/`, started on a loopback port with an api-key,
/// spoken to over HTTP, and killed on exit. No struct layout crosses the process boundary; the
/// REST surface is the contract, and it changes far less often than `llama.h`.
/// </summary>
/// <remarks>
/// <para>
/// The stream this yields is the model's answer text: <c>AnswerParser</c> stays the single place
/// structure comes from, and citation trust in the shipped configuration is the validator's,
/// post-hoc — the template-only decode, temperature 0, no grammar, the one configuration the
/// 2026-08-24/25 sessions measured clean (docs/UNPROVEN.md). The grammar remains available
/// (<see cref="LlamaServerOptions.UseGrammar"/>) for models that do not terminate without it,
/// and thinking mode (<see cref="LlamaServerOptions.ThinkBeforeAnswer"/>) reasons first with the
/// server's parser keeping the thinking out of this stream. Grammar and thinking never combine:
/// an eager grammar was measured shaping the think block itself.
/// </para>
/// <para>
/// Ownership: disposing this is what ends the child while the host lives — there is no finalizer
/// backstop, deliberately, because a finalizer that killed a process the host might still be
/// streaming from would trade a bounded leak for a race. The kill-on-close job is the backstop
/// for a host that dies without disposing anything.
/// </para>
/// </remarks>
public sealed partial class LlamaServerAnswerEngine : IAnswerEngine
{
    private readonly LlamaServerOptions _options;
    private readonly SemaphoreSlim _loading = new(1, 1);
    private LlamaServerInstall? _install;
    private LlamaServerProcess? _server;
    private bool _disposed;

    public LlamaServerAnswerEngine(LlamaServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>The child's ggml log tail — model, KV and compute buffer lines included — for
    /// diagnostics and for the VRAM reading decision 4 wants beside the counters.</summary>
    public string? ServerOutputTail => _server?.OutputTail;

    public AnswerEngineCapabilities Capabilities => new()
    {
        EngineName = "llama-server",
        ModelId = Path.GetFileNameWithoutExtension(_options.ModelPath),
        Backend = (_install ?? LlamaServerLocator.TryFind(_options.Backend, _options.ServerRoot))?.Backend
            ?? _options.Backend
            ?? ComputeBackend.Cpu,
        Quantisation = TryReadQuantisation(Path.GetFileName(_options.ModelPath)),
        SupportsGrammar = true,
    };

    public async ValueTask LoadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Serialised: the check below is check-then-act, and two racing loads would each start a
        // child of which only one could ever be killed again. The second caller waits, sees the
        // first one's server, and returns.
        await _loading.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_server is { HasExited: false })
            {
                return;
            }

            if (_server is not null)
            {
                await _server.DisposeAsync().ConfigureAwait(false);
                _server = null;
            }

            _install = LlamaServerLocator.TryFind(_options.Backend, _options.ServerRoot)
                ?? throw new InvalidOperationException(
                    _options.Backend is { } wanted
                        ? $"No {LlamaServerLocator.BackendDirectoryName(wanted)} llama-server drop is vendored. Run scripts/vendor-llm-natives.ps1 -Backends {LlamaServerLocator.BackendDirectoryName(wanted)}."
                        : "No llama-server drop is vendored at all. Run scripts/vendor-llm-natives.ps1.");

            _server = await LlamaServerProcess.StartAsync(_install, _options, ct).ConfigureAwait(false);
        }
        finally
        {
            _loading.Release();
        }
    }

    public async IAsyncEnumerable<string> AskAsync(
        AskRequest request,
        IProgress<AskProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_server is not { HasExited: false } server)
        {
            throw new InvalidOperationException("Ask before load. Call LoadAsync first.");
        }

        // The abstain path is mechanical, not the model's to decide: an empty transcript, or an
        // ask handed no evidence at all — in any mode; the whole-transcript path passes windows
        // tiling the recording, and this engine builds none itself — is answered without a model
        // in the room, the same behaviour the fake promises (decision 6).
        if (request.Transcript.IsEmpty || request.Evidence.Count == 0)
        {
            yield return AnswerParser.AbstainSentinel;
            yield return "\n";
            yield break;
        }

        // The verbatim quote is retrieval's check and is dropped on the whole-transcript path —
        // decided 2026-08-25, on what the two modes ask the model to produce. A retrieval bullet
        // answers a pointed question from a span, so a quote is a sentence really in it and the
        // substring check is the strongest guarantee this project has. An overview bullet is a
        // synthesis across minutes; forcing one sentence of it to be verbatim either
        // misrepresents the claim or picks a line so generic it verifies against anything, and
        // the check that passes then has checked nothing. Citation trust in this mode is
        // resolve-only: the id still names a real span the reader can click and hear.
        var requireQuote = _options.RequireQuote && request.Mode != AnswerMode.WholeTranscript;
        var wantLead = request.Mode == AnswerMode.WholeTranscript;

        var (instruction, userContent) =
            AnswerPromptBuilder.BuildMessages(request, _options.AllowAbstain, requireQuote);

        // In thinking mode the grammar must stay home: an eager grammar constrains sampling
        // wherever the stream happens to be, and it was measured shaping the think block itself
        // — every grammar-legal token filed as reasoning, content empty (2026-08-16, re-measured
        // 2026-08-24). Citation trust in this mode is the parser's and validator's, post-hoc.
        var grammar = _options.UseGrammar && !_options.ThinkBeforeAnswer
            ? AnswerPromptBuilder.BuildGrammar(request.Evidence, _options.AllowAbstain, requireQuote, wantLead)
            : null;
        var maxTokens = _options.MaxAnswerTokens
            + (_options.ThinkBeforeAnswer ? _options.ThinkingBudgetTokens : 0);

        // A prompt past the context would be truncated server-side in silence, leaving the
        // grammar's ids live for evidence the model never saw. Four characters per token is an
        // estimate and the message says so; the panel's ~2k-token evidence never comes near it.
        var estimatedTokens = (instruction.Length + userContent.Length) / 4;
        if (estimatedTokens > _options.ContextSize)
        {
            throw new InvalidOperationException(
                $"The prompt is roughly {estimatedTokens} tokens (estimated at four characters per token) "
                + $"against a context of {_options.ContextSize}. Fewer or shorter evidence windows are the "
                + "fix; a silent truncation is not.");
        }

        // The chat endpoint, not /completion: the model's template supplies the turn structure
        // end-of-turn was trained against. The raw-prompt path was measured on 2026-08-24
        // leaving all four candidate models unable to stop — substance first, then grammar-legal
        // filler to the token cap (docs/UNPROVEN.md, the product-path gauntlet). `grammar` and
        // `return_progress` both work on this endpoint at the vendored build — probed before
        // this change, prompt_progress frames and grammar-shaped output observed.
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("v1/chat/completions", UriKind.Relative))
        {
            Content = new ByteArrayContent(BuildRequestBody(instruction, userContent, grammar, maxTokens)),
        };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await server.Client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"llama-server answered {(int)response.StatusCode} to /v1/chat/completions: {body}");
        }

        var generated = 0;
        var thinking = 0;
        var prefillTokens = 0;
        int? prefillTotal = null;
        var endedCleanly = false;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        // The loop advances by hand rather than await foreach so a transport failure can be told
        // apart by whether the child is still alive: a dead child's tail is the diagnostic, and a
        // raw socket exception would bury it.
        var payloads = SseReader.ReadPayloadsAsync(stream, ct).GetAsyncEnumerator(ct);
        await using var _ = payloads.ConfigureAwait(false);
        while (true)
        {
            string payload;
            try
            {
                if (!await payloads.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                payload = payloads.Current;
            }
            catch (Exception failure) when (failure is IOException or HttpRequestException && server.HasExited)
            {
                throw new InvalidOperationException(
                    $"llama-server died mid-answer. Its last lines:\n{server.OutputTail}", failure);
            }

            if (payload == "[DONE]")
            {
                endedCleanly = true;
                break;
            }

            // An empty data: line is SSE housekeeping, not a frame to parse.
            if (string.IsNullOrWhiteSpace(payload))
            {
                continue;
            }

            JsonDocument chunk;
            try
            {
                chunk = JsonDocument.Parse(payload);
            }
            catch (JsonException failure)
            {
                throw new InvalidOperationException(
                    $"llama-server sent a frame that is not JSON: '{Snippet(payload)}'.", failure);
            }

            using var parsed = chunk;
            var root = parsed.RootElement;

            // A slot-level failure — context overflow, an OOM the server survived — arrives as an
            // error frame with the process still alive, and swallowing it would parse whatever
            // came before as a truncated answer.
            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(
                    $"llama-server reported an error mid-answer: {error.GetRawText()}");
            }

            // With return_progress on, prefill chunks carry how much of the prompt has been
            // processed — the wait the panel renders, 467.9 measured seconds at its worst.
            if (root.TryGetProperty("prompt_progress", out var prefill)
                && prefill.ValueKind == JsonValueKind.Object
                && prefill.TryGetProperty("processed", out var processed)
                && prefill.TryGetProperty("total", out var total))
            {
                prefillTokens = processed.GetInt32();
                prefillTotal = total.GetInt32();
                progress?.Report(new AskProgress
                {
                    PrefillTokens = prefillTokens,
                    PrefillTotalTokens = prefillTotal,
                    GeneratedTokens = generated,
                    ThinkingTokens = thinking,
                });
            }

            // Chat-completion chunks: the delta carries reasoning_content while the model
            // thinks — counted for progress, never yielded, so the panel can show activity
            // without the thinking ever reaching the parser — and content when it answers.
            // A non-null finish_reason is the clean end; [DONE] follows it.
            string? text = null;
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("reasoning_content", out var thought)
                        && thought.ValueKind == JsonValueKind.String
                        && thought.GetString() is { Length: > 0 })
                    {
                        thinking++;
                        progress?.Report(new AskProgress
                        {
                            PrefillTokens = prefillTokens,
                            PrefillTotalTokens = prefillTotal,
                            GeneratedTokens = generated,
                            ThinkingTokens = thinking,
                        });
                    }

                    if (delta.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String
                        && content.GetString() is { Length: > 0 } piece)
                    {
                        generated++;
                        progress?.Report(new AskProgress
                        {
                            PrefillTokens = prefillTokens,
                            PrefillTotalTokens = prefillTotal,
                            GeneratedTokens = generated,
                            ThinkingTokens = thinking,
                        });
                        text = piece;
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var finish)
                    && finish.ValueKind == JsonValueKind.String)
                {
                    endedCleanly = true;
                }
            }

            if (text is not null)
            {
                yield return text;
            }

            if (endedCleanly)
            {
                break;
            }
        }

        if (server.HasExited)
        {
            throw new InvalidOperationException(
                $"llama-server died mid-answer. Its last lines:\n{server.OutputTail}");
        }

        // The stream closing with neither a stop chunk nor [DONE], under a child that still
        // lives, is a truncation — and a truncated stream parses as bullets, which is worse
        // than an error. What exact frames the vendored build emits around failure is still
        // the open observation docs/UNPROVEN.md owes; this only refuses the unambiguous case.
        if (!endedCleanly)
        {
            throw new InvalidOperationException(
                $"llama-server's answer stream ended before the answer did. Its last lines:\n{server.OutputTail}");
        }
    }

    /// <summary>The first line of a payload, bounded, for an error message.</summary>
    private static string Snippet(string payload)
    {
        var line = payload.AsSpan();
        var newline = line.IndexOf('\n');
        if (newline >= 0)
        {
            line = line[..newline];
        }

        return line.Length > 160 ? string.Concat(line[..157], "…") : line.ToString();
    }

    /// <summary>
    /// The /v1/chat/completions request, written explicitly so the fields sent are the fields
    /// decided: <c>messages</c> because the model's template supplies the end-of-turn the
    /// raw-prompt path was measured to lack (2026-08-24), <c>cache_prompt</c> because a
    /// follow-up question re-uses the evidence prefix, and <c>return_progress</c> because the
    /// prefill wait must be drawable — its <c>prompt_progress</c> frames were probed working on
    /// this endpoint at the vendored build before the endpoint moved.
    /// </summary>
    internal static byte[] BuildRequestBody(
        string instruction, string userContent, string? grammar, int maxTokens = 1_024)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", instruction);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", userContent);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteNumber("max_tokens", maxTokens);

            // Greedy decoding, pinned. Every measured figure and every clean answer in the
            // record was taken at temperature 0; unpinned, the server's own default (~0.8)
            // applies, and under the grammar's any-character text production a hot sampler
            // wanders into charset-legal noise — observed in the app on 2026-08-25, repetition
            // loops and foreign-script tokens inside English bullets, every one caught by the
            // quote check. Citing a transcript is extraction, not creation; greedy is the mode
            // the task wants and the only one the lab has measured.
            writer.WriteNumber("temperature", 0);
            writer.WriteBoolean("stream", true);
            writer.WriteBoolean("cache_prompt", true);
            writer.WriteBoolean("return_progress", true);
            if (grammar is not null)
            {
                writer.WriteString("grammar", grammar);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The quantisation as the file name spells it — <c>Q8_0</c>, <c>UD-IQ4_XS</c>, <c>MXFP4</c> —
    /// or null when the name carries none. Provenance for the answer's provenance line, read from
    /// the one place the lab's GGUF files record it.
    /// </summary>
    internal static string? TryReadQuantisation(string fileName)
    {
        var match = QuantisationPattern().Match(fileName);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:UD-)?(?:I?Q[0-9](?:_[A-Z0-9]+)*|MXFP4(?:_MOE)?|NVFP4|BF16|F16|F32)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex QuantisationPattern();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        _loading.Dispose();
    }
}
