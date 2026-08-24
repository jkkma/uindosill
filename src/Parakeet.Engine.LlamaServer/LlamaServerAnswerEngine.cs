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
/// The stream this yields is the model's raw text: <c>AnswerParser</c> stays the single place
/// structure comes from, and the grammar built beside the prompt is what keeps that text inside
/// the shape the parser reads — including keeping a thinking model's reasoning out of the answer
/// channel, which the grammar was measured to do where <c>--reasoning-budget 0</c> alone was not
/// (docs/UNPROVEN.md).
/// </remarks>
public sealed partial class LlamaServerAnswerEngine : IAnswerEngine
{
    private readonly LlamaServerOptions _options;
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

        // The abstain path is mechanical, not the model's to decide: an empty transcript, or
        // retrieval that found nothing, is answered without a model in the room — the same
        // behaviour the fake promises, decision 6's "empty retrieval yields an abstention".
        if (request.Transcript.IsEmpty || (request.Mode == AnswerMode.Retrieval && request.Evidence.Count == 0))
        {
            yield return AnswerParser.AbstainSentinel;
            yield return "\n";
            yield break;
        }

        var prompt = AnswerPromptBuilder.BuildPrompt(request);
        var grammar = _options.UseGrammar
            ? AnswerPromptBuilder.BuildGrammar(request.Evidence, _options.AllowAbstain, _options.RequireQuote)
            : null;

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("completion", UriKind.Relative))
        {
            Content = new ByteArrayContent(BuildRequestBody(prompt, grammar, _options.MaxAnswerTokens)),
        };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await server.Client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"llama-server answered {(int)response.StatusCode} to /completion: {body}");
        }

        var generated = 0;
        var prefillTokens = 0;
        int? prefillTotal = null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var payload in SseReader.ReadPayloadsAsync(stream, ct).ConfigureAwait(false))
        {
            if (payload == "[DONE]")
            {
                break;
            }

            using var chunk = JsonDocument.Parse(payload);
            var root = chunk.RootElement;

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
                });
            }

            if (root.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } text)
            {
                generated++;
                progress?.Report(new AskProgress
                {
                    PrefillTokens = prefillTokens,
                    PrefillTotalTokens = prefillTotal,
                    GeneratedTokens = generated,
                });
                yield return text;
            }

            if (root.TryGetProperty("stop", out var stop) && stop.ValueKind == JsonValueKind.True)
            {
                break;
            }
        }

        if (server.HasExited)
        {
            throw new InvalidOperationException(
                $"llama-server died mid-answer. Its last lines:\n{server.OutputTail}");
        }
    }

    /// <summary>
    /// The /completion request, written explicitly so the fields sent are the fields decided:
    /// <c>cache_prompt</c> because a follow-up question re-uses the evidence prefix, and
    /// <c>return_progress</c> because the prefill wait must be drawable.
    /// </summary>
    internal static byte[] BuildRequestBody(string prompt, string? grammar, int maxAnswerTokens = 1_024)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("prompt", prompt);
            writer.WriteNumber("n_predict", maxAnswerTokens);
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
    }
}
