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
    private PythonSidecarException? _fault;
    private int _nextId;
    private volatile bool _disposed;

    public PythonSidecar(PythonRuntime.Resolution runtime) => _runtime = runtime;

    /// <summary>What the sidecar said about itself at the handshake.</summary>
    public JsonElement? Hello { get; private set; }

    /// <summary>
    /// True once the child is known to be gone, or to have failed its handshake, or to have broken
    /// the protocol — after which every request is refused at once rather than written to a pipe
    /// nothing is reading.
    /// </summary>
    /// <remarks>
    /// Before this existed a dead child was discovered by the next write, and only by it: the
    /// diariser decodes and stages a whole file before it sends anything, so in a batch every file
    /// after the one the child died on paid its own decode and then failed the same way. The first
    /// failure is the one that is kept, because it carries the reason — the traceback tail, the
    /// protocol number — and everything after it is a consequence.
    /// </remarks>
    public bool IsFaulted => _fault is not null;

    /// <summary>Throws the recorded failure, if there is one.</summary>
    /// <remarks>
    /// For a caller with work to do before its request, so that the sidecar's death is found out
    /// before that work rather than by the write after it. A fresh exception each time, with the
    /// recorded one as its inner, so every caller gets its own stack and the first one's message.
    /// </remarks>
    public void ThrowIfFaulted()
    {
        if (_fault is { } fault)
        {
            throw new PythonSidecarException(fault.Message, fault);
        }
    }

    /// <summary>
    /// Records <paramref name="fresh"/> as the sidecar's failure unless one is already recorded,
    /// and returns the one that stands — <paramref name="fresh"/> itself when it was first, or a
    /// new exception carrying the earlier one's message when it was not.
    /// </summary>
    private PythonSidecarException Faulted(PythonSidecarException fresh)
    {
        var standing = Interlocked.CompareExchange(ref _fault, fresh, null);
        return standing is null ? fresh : new PythonSidecarException(standing.Message, standing);
    }

    private sealed class Pending
    {
        public required TaskCompletionSource<JsonElement> Completion { get; init; }

        public IProgress<(int Completed, int Total)>? Progress { get; init; }
    }

    /// <summary>Starts the interpreter and completes the handshake. Idempotent.</summary>
    /// <remarks>
    /// "Started" means handshaken. A child whose handshake failed is not started a second time and
    /// is not pretended to have started: it was killed when the handshake failed, the reason was
    /// recorded, and every later call throws it again — otherwise a protocol-2 sidecar refused once
    /// would be answering a protocol-1 host's requests from the second file on.
    /// </remarks>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfFaulted();

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

        try
        {
            var hello = await SendAsync("hello", _ => { }, null, ct).ConfigureAwait(false);
            Hello = hello;

            var protocol = hello.TryGetProperty("protocol", out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : -1;
            if (protocol != ProtocolVersion)
            {
                throw new PythonSidecarException(
                    $"The Python engines speak protocol {protocol} and this build speaks {ProtocolVersion}. " +
                    "The bundled Python and the application are out of step — reinstall rather than mixing them.");
            }
        }
        catch (Exception exc)
        {
            // Recorded before the kill, so that this reason stands over the "exited unexpectedly"
            // the reader will report once the child is gone — unless the child went first, in which
            // case its own death, with the stderr tail attached, is the better reason and is kept.
            Faulted(exc as PythonSidecarException
                ?? new PythonSidecarException($"The Python engines failed their handshake: {exc.Message}", exc));
            TryKill();
            throw;
        }
    }

    private void TryKill()
    {
        try
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone.
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
        ThrowIfFaulted();

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

            // A write that fails is a child that is gone, and the tail of what it said before it
            // went belongs on this message as much as on the reader's — this is the one a batch
            // sees when the child died between two files, with nothing pending to carry the other.
            throw Faulted(new PythonSidecarException(
                $"The Python engines stopped accepting input: {exc.Message}" + DescribeStandardError(), exc));
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
            FailAll(Faulted(new PythonSidecarException(
                $"The Python engines' output ended: {exc.Message}" + DescribeStandardError(), exc)));
            return;
        }

        // stdout closed: the child is gone, so nothing still waiting will ever be answered — and
        // nothing sent later will be either, which is what recording the fault is for.
        FailAll(Faulted(new PythonSidecarException(
            "The Python engines exited unexpectedly." + DescribeStandardError())));
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
