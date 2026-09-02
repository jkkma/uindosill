using System.Buffers;
using System.Text.Json;
using Parakeet.Core.Tidying;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer;

/// <summary>
/// <see cref="ITranscriptTidier"/> over a bundled <c>llama-server</c> child process — the same
/// child, the same drop and the same flags the answer engine uses, serving a smaller model that
/// is asked one line at a time.
/// </summary>
/// <remarks>
/// <para>
/// What comes back is a candidate and nothing more: <see cref="TidyContract"/> in the core is
/// what decides whether the transcript takes it, and this class has no way around that. The
/// prompt (<see cref="TidyPromptBuilder"/>) asks the model to obey the contract; the contract
/// checks that it did.
/// </para>
/// <para>
/// Several lines are in flight at once — the stage keeps <see cref="TidyOptions.Concurrency"/>
/// of them — so the child is started with that many slots
/// (<see cref="LlamaServerOptions.ParallelSlots"/>). The measurement ran four against the
/// server's own default of four; naming the number keeps the behaviour where it was measured
/// rather than where a later server build's default puts it.
/// </para>
/// <para>
/// Ownership is the answer engine's: disposing this kills the child, and the kill-on-close job
/// is the backstop for a host that dies without disposing anything.
/// </para>
/// </remarks>
public sealed class LlamaServerTranscriptTidier : ITranscriptTidier
{
    private readonly LlamaServerOptions _options;
    private readonly SemaphoreSlim _loading = new(1, 1);
    private LlamaServerInstall? _install;
    private LlamaServerProcess? _server;
    private bool _disposed;

    public LlamaServerTranscriptTidier(LlamaServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// The context the tidy's child is started with: the spike's 8,192 in place of the answer
    /// engine's 16,384, shared across the slots — 2,048 per line in flight at four, against
    /// lines that measure a few hundred tokens with the instruction.
    /// </summary>
    public const int ContextSize = 8_192;

    /// <summary>
    /// A tidier over <paramref name="modelPath"/> at the flags the tidy was measured with: the
    /// engine's own arguments, the spike's context, as many slots as lines in flight, and the
    /// drafting head beside the weights when there is one — worth 1.8–2.1x on decode here, where
    /// a rewrite mostly copies its input (docs/UNPROVEN.md). One place, so the command line and
    /// the window start the same child.
    /// </summary>
    public static LlamaServerTranscriptTidier Create(
        string modelPath, ComputeBackend? backend, string? serverRoot, int concurrency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        return new LlamaServerTranscriptTidier(new LlamaServerOptions
        {
            ModelPath = modelPath,
            Backend = backend,
            ServerRoot = serverRoot,
            ContextSize = ContextSize,
            ParallelSlots = concurrency,
            DraftModelPath = DraftModelLocator.FindBeside(modelPath),
        });
    }

    /// <summary>The child's log tail, for diagnostics.</summary>
    public string? ServerOutputTail => _server?.OutputTail;

    public TidierCapabilities Capabilities => new()
    {
        EngineName = "llama-server",
        ModelId = Path.GetFileNameWithoutExtension(_options.ModelPath),
        Backend = (_install ?? LlamaServerLocator.TryFind(_options.Backend, _options.ServerRoot))?.Backend
            ?? _options.Backend
            ?? ComputeBackend.Cpu,
        Quantisation = LlamaServerAnswerEngine.TryReadQuantisation(Path.GetFileName(_options.ModelPath)),
    };

    public async ValueTask LoadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _loading.WaitAsync(ct).ConfigureAwait(false);
        try
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
        finally
        {
            _loading.Release();
        }
    }

    public async Task<string> TidyLineAsync(string line, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_server is not { } server)
        {
            throw new InvalidOperationException("Tidy before load. Call LoadAsync first.");
        }

        if (server.HasExited)
        {
            throw new InvalidOperationException(
                "llama-server died while idle. Its last lines:\n"
                + await server.DrainedOutputTailAsync().ConfigureAwait(false));
        }

        var maxTokens = TidyPromptBuilder.MaxTokensFor(line);

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("v1/chat/completions", UriKind.Relative))
        {
            Content = new ByteArrayContent(BuildRequestBody(line, maxTokens)),
        };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        HttpResponseMessage response;
        try
        {
            response = await server.Client.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException failure) when (server.HasExited)
        {
            throw new InvalidOperationException(
                "llama-server died mid-line. Its last lines:\n"
                + await server.DrainedOutputTailAsync().ConfigureAwait(false),
                failure);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"llama-server answered {(int)response.StatusCode} to /v1/chat/completions: {body}");
            }

            return ReadContent(body, maxTokens);
        }
    }

    /// <summary>
    /// The rewrite out of a non-streamed chat completion. A cap hit is refused rather than
    /// returned, because a line that ran to its cap stopped copying its input somewhere, and the
    /// half that came back would read as a tidied line.
    /// </summary>
    internal static string ReadContent(string body, int maxTokens)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(body);
        }
        catch (JsonException failure)
        {
            throw new InvalidOperationException("llama-server sent a reply that is not JSON.", failure);
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException($"llama-server reported an error: {error.GetRawText()}");
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("llama-server's reply carried no choices.");
            }

            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish)
                && finish.ValueKind == JsonValueKind.String
                && finish.GetString() == "length")
            {
                throw new InvalidOperationException(
                    $"The rewrite hit its {maxTokens}-token cap before it finished, which a line that copies its input never does.");
            }

            if (!choice.TryGetProperty("message", out var reply)
                || !reply.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return (content.GetString() ?? string.Empty).Trim();
        }
    }

    /// <summary>
    /// The request, non-streamed: a line's rewrite is a second or two and there is nothing to
    /// draw while it arrives. Greedy, for the answer engine's reasons; <c>cache_prompt</c>
    /// because every request shares the instruction.
    /// </summary>
    internal static byte[] BuildRequestBody(string line, int maxTokens)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", TidyPromptBuilder.Instruction);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", line);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteNumber("max_tokens", maxTokens);
            writer.WriteNumber("temperature", 0);
            writer.WriteBoolean("stream", false);
            writer.WriteBoolean("cache_prompt", true);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _loading.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_server is not null)
            {
                await _server.DisposeAsync().ConfigureAwait(false);
                _server = null;
            }
        }
        finally
        {
            _loading.Release();
            _loading.Dispose();
        }
    }
}
