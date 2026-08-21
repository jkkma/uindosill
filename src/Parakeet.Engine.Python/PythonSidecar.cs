using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Parakeet.Engine.Python;

/// <summary>
/// The child interpreter and the line protocol over its stdin and stdout.
/// </summary>
/// <remarks>
/// <para>
/// One process for a whole run rather than one per file: the diariser's graph is 453 MiB and the
/// translator's 1.34 GiB, and a batch that reloads them per file spends more time loading than
/// working. It is started lazily — nothing spawns until a model is actually wanted — and stopped on
/// dispose.
/// </para>
/// <para>
/// <b>stdout is the protocol and stderr is everything else.</b> The Python side enforces that at
/// its end (see <c>protocol.claim_stdout</c>); this side keeps the tail of stderr so that when the
/// child dies there is something to report other than "it died". A traceback that nobody kept is
/// the difference between a bug report and a shrug.
/// </para>
/// <para>
/// Requests are correlated by id rather than by order, because progress messages interleave with
/// results and a second request may be sent before the first has finished. Nothing here assumes a
/// reply arrives before the next message does.
/// </para>
/// </remarks>
public sealed class PythonSidecar : IAsyncDisposable
{
    /// <summary>The protocol number this host speaks. Must match the sidecar's.</summary>
    public const int ProtocolVersion = 1;

    private const int StandardErrorLinesKept = 200;

    private readonly PythonRuntime.Resolution _runtime;
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly Queue<string> _standardError = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _errorGate = new();

    private Process? _process;
    private Task? _reader;
    private Task? _errorReader;
    private int _nextId;
    private volatile bool _disposed;

    public PythonSidecar(PythonRuntime.Resolution runtime) => _runtime = runtime;

    /// <summary>What the sidecar said about itself at the handshake.</summary>
    public JsonElement? Hello { get; private set; }

    private sealed class Pending
    {
        public required TaskCompletionSource<JsonElement> Completion { get; init; }

        public IProgress<(int Completed, int Total)>? Progress { get; init; }
    }

    /// <summary>Starts the interpreter and completes the handshake. Idempotent.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_process is not null)
        {
            return;
        }

        var start = new ProcessStartInfo
        {
            FileName = _runtime.Interpreter,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        start.ArgumentList.Add("-u");   // unbuffered: a buffered reply is a deadlock that looks like a slow model
        start.ArgumentList.Add("-m");
        start.ArgumentList.Add("uindosill_engines");

        // The package root reaches the child this way rather than by working directory, so the
        // host's own cwd — which is the user's, and arbitrary — cannot change which code runs.
        start.Environment["PYTHONPATH"] = _runtime.PackageRoot;
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        try
        {
            _process = Process.Start(start)
                ?? throw new PythonSidecarException($"Could not start {_runtime.Interpreter}.");
        }
        catch (Exception exc) when (exc is not PythonSidecarException)
        {
            throw new PythonSidecarException($"Could not start {_runtime.Interpreter}: {exc.Message}", exc);
        }

        _reader = Task.Run(ReadLoopAsync, CancellationToken.None);
        _errorReader = Task.Run(ReadErrorLoopAsync, CancellationToken.None);

        var hello = await SendAsync("hello", _ => { }, null, ct).ConfigureAwait(false);
        Hello = hello;

        var protocol = hello.TryGetProperty("protocol", out var value) ? value.GetInt32() : -1;
        if (protocol != ProtocolVersion)
        {
            throw new PythonSidecarException(
                $"The Python engines speak protocol {protocol} and this build speaks {ProtocolVersion}. " +
                "The bundled Python and the application are out of step — reinstall rather than mixing them.");
        }
    }

    /// <summary>Sends one request and waits for its result.</summary>
    /// <remarks>
    /// <paramref name="write"/> fills in the request's own fields; the id and op are this method's.
    /// An error message from the sidecar becomes a <see cref="PythonEngineException"/> carrying the
    /// kind, which is what lets a caller tell "this file could not be read" from "the model is not
    /// there" without matching on message text.
    /// </remarks>
    public async Task<JsonElement> SendAsync(
        string op,
        Action<Utf8JsonWriter> write,
        IProgress<(int Completed, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending
        {
            Completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously),
            Progress = progress,
        };
        _pending[id] = pending;

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("op", op);
            write(writer);
            writer.WriteEndObject();
        }

        var line = Encoding.UTF8.GetString(buffer.ToArray());

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var input = _process?.StandardInput
                ?? throw new PythonSidecarException("The Python engines are not running.");
            await input.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await input.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is not OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw new PythonSidecarException($"The Python engines stopped accepting input: {exc.Message}", exc);
        }
        finally
        {
            _writeGate.Release();
        }

        using var registration = ct.Register(() => pending.Completion.TrySetCanceled(ct));
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        var output = _process!.StandardOutput;
        try
        {
            while (await output.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                Dispatch(line);
            }
        }
        catch (Exception exc)
        {
            FailAll(new PythonSidecarException($"The Python engines' output ended: {exc.Message}", exc));
            return;
        }

        // stdout closed: the child is gone, so nothing still waiting will ever be answered.
        FailAll(new PythonSidecarException(
            "The Python engines exited unexpectedly." + DescribeStandardError()));
    }

    private void Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // Not protocol. The Python side sends everything else to stderr, so this means a
            // library wrote to a handle it should not have; record it and carry on rather than
            // tearing down a run over one stray line.
            RecordStandardError("non-protocol line on stdout: " + Truncate(line));
            return;
        }

        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
        {
            RecordStandardError("protocol message with no id: " + Truncate(line));
            document.Dispose();
            return;
        }

        var id = idElement.GetInt32();
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        if (type == "progress")
        {
            if (_pending.TryGetValue(id, out var running) && running.Progress is { } report)
            {
                var completed = root.TryGetProperty("completed", out var c) ? c.GetInt32() : 0;
                var total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
                report.Report((completed, total));
            }

            document.Dispose();
            return;
        }

        if (!_pending.TryRemove(id, out var pending))
        {
            document.Dispose();
            return;
        }

        if (type == "error")
        {
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "internal" : "internal";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var trace = root.TryGetProperty("traceback", out var tb) ? tb.GetString() : null;
            document.Dispose();
            pending.Completion.TrySetException(new PythonEngineException(kind, message, trace));
            return;
        }

        // The document owns the memory the element points at, so it is cloned rather than disposed
        // out from under the awaiting caller.
        var clone = root.Clone();
        document.Dispose();
        pending.Completion.TrySetResult(clone);
    }

    private async Task ReadErrorLoopAsync()
    {
        var error = _process!.StandardError;
        try
        {
            while (await error.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                RecordStandardError(line);
            }
        }
        catch
        {
            // The child is going away; its stderr going with it is not itself an error.
        }
    }

    private void RecordStandardError(string line)
    {
        lock (_errorGate)
        {
            _standardError.Enqueue(line);
            while (_standardError.Count > StandardErrorLinesKept)
            {
                _standardError.Dequeue();
            }
        }
    }

    /// <summary>The tail of the child's stderr, for a message about why it died.</summary>
    public string DescribeStandardError()
    {
        string[] lines;
        lock (_errorGate)
        {
            lines = [.. _standardError];
        }

        return lines.Length == 0
            ? string.Empty
            : Environment.NewLine + "Last output from the Python engines:" + Environment.NewLine +
              string.Join(Environment.NewLine, lines.TakeLast(20));
    }

    private void FailAll(Exception exception)
    {
        foreach (var id in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "…";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var process = _process;
        if (process is null)
        {
            return;
        }

        // Ask first: a clean shutdown lets Python release the graph and flush anything it is
        // holding. Killing works too, and is what happens if it does not answer promptly.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendShutdownAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Already gone.
            }
        }

        FailAll(new PythonSidecarException("The Python engines were shut down."));

        try
        {
            if (_reader is not null)
            {
                await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            if (_errorReader is not null)
            {
                await _errorReader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch
        {
            // The readers end when the pipes close; a slow one is not worth blocking a shutdown.
        }

        process.Dispose();
        _writeGate.Dispose();
        _process = null;
    }

    private async Task SendShutdownAsync(CancellationToken ct)
    {
        // Not through SendAsync: that refuses once disposed, and this runs during disposal.
        var id = Interlocked.Increment(ref _nextId);
        var input = _process?.StandardInput;
        if (input is null)
        {
            return;
        }

        await input.WriteAsync($"{{\"id\":{id},\"op\":\"shutdown\"}}\n".AsMemory(), ct).ConfigureAwait(false);
        await input.FlushAsync(ct).ConfigureAwait(false);
        input.Close();
    }
}

/// <summary>
/// A failure the sidecar reported, as opposed to a failure of the sidecar.
/// </summary>
/// <remarks>
/// <see cref="Kind"/> is the closed vocabulary the protocol defines — <c>request</c>, <c>model</c>,
/// <c>audio</c>, <c>internal</c> — so a caller can decide whether to abandon a batch or skip a file
/// without matching on message text, which is how a reworded message silently changes behaviour.
/// </remarks>
public sealed class PythonEngineException : Exception
{
    public PythonEngineException(string kind, string message, string? pythonTraceback = null)
        : base(message)
    {
        Kind = kind;
        PythonTraceback = pythonTraceback;
    }

    public string Kind { get; }

    /// <summary>The child's traceback, when it sent one. Diagnostics, never shown to a user as-is.</summary>
    public string? PythonTraceback { get; }
}
