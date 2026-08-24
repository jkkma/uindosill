using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Parakeet.Core.Hosting;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer;

/// <summary>
/// One running <c>llama-server</c> child: started on a loopback port with an api-key, health-checked
/// before anyone asks it anything, in the kill-on-close job so it cannot outlive a host that dies,
/// and killed — never asked nicely — on stop, because under the residency policy an unload is a
/// process exit, the one form of unload that cannot leak.
/// </summary>
internal sealed class LlamaServerProcess : IAsyncDisposable
{
    private const int OutputTailLines = 120;

    private readonly Process _process;
    private readonly Queue<string> _tail = new();
    private readonly Lock _tailGate = new();

    private LlamaServerProcess(Process process, HttpClient client, Uri baseAddress, bool inKillOnCloseJob)
    {
        _process = process;
        Client = client;
        BaseAddress = baseAddress;
        InKillOnCloseJob = inKillOnCloseJob;
    }

    /// <summary>Authenticated, loopback, no timeout of its own — streams outlive any fixed one.</summary>
    public HttpClient Client { get; }

    public Uri BaseAddress { get; }

    public bool InKillOnCloseJob { get; }

    public bool HasExited => _process.HasExited;

    /// <summary>
    /// The last lines the child wrote, for the crash notice. ggml logs its model, KV and compute
    /// buffer sizes here too, which is the allocation half of every VRAM question.
    /// </summary>
    public string OutputTail
    {
        get
        {
            lock (_tailGate)
            {
                return string.Join('\n', _tail);
            }
        }
    }

    public static async Task<LlamaServerProcess> StartAsync(
        LlamaServerInstall install, LlamaServerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException(
                $"The model to serve is not there: {options.ModelPath}", options.ModelPath);
        }

        var port = TakeFreePort();
        var apiKey = Guid.NewGuid().ToString("N");

        // The key travels in a file, not on the command line: a command line is readable by any
        // same-user process for the child's whole life, while the file is gone the moment the
        // child has read it. --api-key-file is in the vendored b10603 build's own --help
        // (read on 2026-08-24); the temp directory is already per-user on Windows.
        var apiKeyFile = Path.Combine(Path.GetTempPath(), $"uindosill-llm-{Guid.NewGuid():N}.key");
        await File.WriteAllTextAsync(apiKeyFile, apiKey, ct).ConfigureAwait(false);

        var start = new ProcessStartInfo
        {
            FileName = install.ExecutablePath,
            WorkingDirectory = install.Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in BuildArguments(options, port, apiKeyFile))
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in BuildEnvironment(install.Backend, options.Environment))
        {
            start.Environment[name] = value;
        }

        try
        {
            var process = Process.Start(start)
                ?? throw new InvalidOperationException($"Could not start {install.ExecutablePath}.");

            var inJob = KillOnCloseJob.TryAssign(process);

            var client = new HttpClient
            {
                BaseAddress = new Uri(FormattableString.Invariant($"http://127.0.0.1:{port}/")),
                // Streams run as long as an answer takes; per-request cancellation is the timeout.
                Timeout = Timeout.InfiniteTimeSpan,
            };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var server = new LlamaServerProcess(process, client, client.BaseAddress, inJob);
            process.OutputDataReceived += (_, e) => server.Append(e.Data);
            process.ErrorDataReceived += (_, e) => server.Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await server.WaitHealthyAsync(options.LoadTimeout, ct).ConfigureAwait(false);
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return server;
        }
        finally
        {
            // A healthy child has read the key; a failed one is dead. Either way the file has
            // done its job, and a leftover key on disk is the thing this arrangement exists to
            // avoid outliving.
            try
            {
                File.Delete(apiKeyFile);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The child's command line, one argument per element. Internal and pure so the suite can
    /// hold it without a process: these flags are decisions with records behind them, and a
    /// refactor that dropped one would otherwise only be found by running a model.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(LlamaServerOptions options, int port, string apiKeyFile)
    {
        var arguments = new List<string>
        {
            "-m", options.ModelPath,
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),

            // The file, never the key itself: a child's command line is readable by any
            // same-user process for as long as it runs.
            "--api-key-file", apiKeyFile,
            "-c", options.ContextSize.ToString(CultureInfo.InvariantCulture),
            "-ngl", options.GpuLayers.ToString(CultureInfo.InvariantCulture),

            // The register's decision 1 names --fit on as a way to be fooled: it trims layers
            // and context to what fits, so a model that does not fit still runs, silently
            // degraded. Off, and a failure is a failure someone can read.
            "--fit", "off",

            // No browser UI on a loopback port nobody is meant to find.
            "--no-webui",
        };

        if (options.FlashAttention is { } flashAttention)
        {
            arguments.Add("-fa");
            arguments.Add(flashAttention);
        }

        return arguments;
    }

    /// <summary>
    /// The child's environment. On Vulkan, <c>GGML_VK_DISABLE_BFLOAT16=1</c> unless the caller
    /// says otherwise: the laptop's driver hangs at model load without it (measured 2026-08-16,
    /// docs/UNPROVEN.md), and a hang on load is strictly worse than bf16 being unavailable.
    /// Stage 0.1 — a driver update and a re-run — is what retires this default.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildEnvironment(
        ComputeBackend backend, IReadOnlyDictionary<string, string> overrides)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (backend == ComputeBackend.Vulkan)
        {
            environment["GGML_VK_DISABLE_BFLOAT16"] = "1";
        }

        foreach (var (name, value) in overrides)
        {
            environment[name] = value;
        }

        return environment;
    }

    private async Task WaitHealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"llama-server exited with code {_process.ExitCode} before it became healthy. Its last lines:\n{OutputTail}");
            }

            try
            {
                // The probe itself is bounded by what remains of the deadline: the client's own
                // timeout is infinite for the sake of answer streams, and a child that accepts
                // the connection and then stalls (the driver-hang-at-load class) would otherwise
                // hang this await past any timeout the caller set.
                var remaining = deadline - DateTime.UtcNow;
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (remaining > TimeSpan.Zero)
                {
                    probe.CancelAfter(remaining);
                }
                else
                {
                    await probe.CancelAsync().ConfigureAwait(false);
                }

                using var response = await Client.GetAsync(new Uri("health", UriKind.Relative), probe.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet; a 9 GB model is tens of seconds from a cold disk.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A probe still hanging at the deadline is the timeout, with the stall named.
                throw new TimeoutException(
                    $"llama-server did not become healthy within {timeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s — a /health probe was accepted and then stalled. Its last lines:\n{OutputTail}");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"llama-server did not become healthy within {timeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s. Its last lines:\n{OutputTail}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_tailGate)
        {
            _tail.Enqueue(line);
            while (_tail.Count > OutputTailLines)
            {
                _tail.Dequeue();
            }
        }
    }

    /// <summary>
    /// Binds port 0 to learn a free port, then releases it for the child. The port can be taken
    /// in the gap; the cost of that race is a failed start with the child's own message, not a
    /// silent misbind, and the child binds loopback only either way.
    /// </summary>
    private static int TakeFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        try
        {
            if (!_process.HasExited)
            {
                // The kill is the unload — decided, not expedient: a process exit is the one
                // unload that cannot leak VRAM, and the adapter returning to idle after it is
                // measured (docs/UNPROVEN.md).
                _process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill.
        }
        catch (OperationCanceledException)
        {
            // It is being killed; not waiting further is the worst case, and the job object
            // still holds it.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
