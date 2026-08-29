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

        // The loader is asked only when its answer decides something: on the Vulkan backend, with
        // the placement left automatic. Creating an instance loads every installed driver, and a
        // CUDA or CPU child has no reason to pay for that.
        //
        // **Still Vulkan-only after 2026-08-29, though CUDA now honours the picker.** What CUDA
        // gained is the two *explicit* placements, and neither asks the loader anything — the fit
        // rule that would need a device size is deliberately not used there. See
        // `BuildEnvironment` for why.
        var automatic = install.Backend == ComputeBackend.Vulkan
            && options.ExpertPlacement == MoeExpertPlacement.Automatic;
        var graphics = automatic ? VulkanDeviceProbe.Describe() : null;

        // The file on disk rather than anything the catalogue says about it: the models folder is
        // not this application's alone to write, and a size read from the file is a size that is
        // really there. Zero on a read that fails, which the rule treats as "does not fit".
        var modelBytes = automatic ? SizeOrZero(options.ModelPath) : 0;

        foreach (var (name, value) in BuildEnvironment(
            install.Backend, options.Environment, options.ExpertPlacement, graphics, modelBytes))
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

            // Prefill is about 60% of an answer's wall on the second machine, so the batch
            // it is chunked into is not a detail — see LlamaServerOptions.BatchSize.
            "-b", options.BatchSize.ToString(CultureInfo.InvariantCulture),
            "-ub", options.PhysicalBatchSize.ToString(CultureInfo.InvariantCulture),

            // The register's decision 1 names --fit on as a way to be fooled: it trims layers
            // and context to what fits, so a model that does not fit still runs, silently
            // degraded. Off, and a failure is a failure someone can read.
            "--fit", "off",

            // No browser UI on a loopback port nobody is meant to find.
            "--no-webui",

            // The model's own chat template, applied server-side. The raw-prompt path was
            // measured on 2026-08-24 (docs/UNPROVEN.md, the product-path gauntlet) leaving
            // every candidate model unable to stop: the template's turn structure is what
            // end-of-turn was trained against, and without it the grammar bounds the shape of
            // the runaway but not its length.
            "--jinja",
        };

        // Thinking is turned off, not merely redirected — measured 2026-08-25, and the defect it
        // repairs was in the shipped default. The server's `--reasoning` defaults to `auto`,
        // which lets the model's own template decide, so a thinking model thought regardless of
        // this setting: `--reasoning-format` only chose where the thought text was filed, and
        // filing it under reasoning_content (the default parse) meant the engine dropped it and
        // the answer budget was spent before a single content token existed. On the second
        // machine the 26B-A4B answered a twelve-segment overview with **nothing at all in 79.4 s**
        // under `auto`, and with the same prompt under `off` produced a lead and four cited
        // bullets in 45.5 s. A toggle labelled "think before answering" has to be the thing that
        // decides, and now it is.
        arguments.Add("--reasoning");
        arguments.Add(options.ThinkBeforeAnswer ? "on" : "off");

        if (!options.ThinkBeforeAnswer && options.UseGrammar)
        {
            // The grammar mode only: every generated token stays in content, where the grammar
            // shapes it from the first sampled token. Without this a template that forces a
            // think block open files the whole grammar-shaped stream under reasoning_content
            // and a client reading content sees nothing — measured 2026-08-16,
            // docs/UNPROVEN.md. Everywhere else the server's default reasoning parsing is what
            // keeps a template's thought channel out of the answer stream — ungrammared, a
            // literal thought tag would otherwise land in content as a junk bullet.
            arguments.Add("--reasoning-format");
            arguments.Add("none");
        }

        if (options.FlashAttention is { } flashAttention)
        {
            arguments.Add("-fa");
            arguments.Add(flashAttention);
        }

        if (options.DraftModelPath is { Length: > 0 } draft)
        {
            // Speculative decoding against the model's own multi-token-prediction head. The type
            // is named rather than left to the server: `--spec-type` defaults to `none`, so
            // handing over a draft model without it drafts nothing at all.
            arguments.Add("--spec-type");
            arguments.Add("draft-mtp");
            arguments.Add("-md");
            arguments.Add(draft);

            // The head follows the model onto whatever the model is on. A draft left on the CPU
            // while the target decodes on the device pays a transfer per drafted token, which is
            // the cost speculative decoding exists to avoid.
            arguments.Add("-ngld");
            arguments.Add(options.GpuLayers.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    /// <summary>
    /// The child's environment. On Vulkan, <c>GGML_VK_DISABLE_BFLOAT16=1</c> unless the caller
    /// says otherwise: the laptop's driver hangs at model load without it (measured 2026-08-16,
    /// docs/UNPROVEN.md), and a hang on load is strictly worse than bf16 being unavailable.
    /// Stage 0.1 — a driver update and a re-run — is what retires this default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expert-offload pair is the conditional one. It was unconditional on Vulkan until
    /// 2026-08-25, which was the second machine's answer applied to every machine: on that
    /// laptop's UMA split a 26B-class mixture cannot load at all without it, and on a card with
    /// memory of its own it parks in system RAM weights that would have fitted in VRAM. Those
    /// are not the same cost — one is a model that does not run, the other is a model that runs
    /// slowly — which is why <see cref="GpuClass.Unknown"/> resolves to system memory: the
    /// unanswered question takes the failure that still starts.
    /// </para>
    /// <para>
    /// Neither branch of the automatic rule has been measured against the other on one machine.
    /// The card side has never been measured at all — no discrete-GPU Vulkan ask run exists —
    /// and docs/UNPROVEN.md carries that as its own entry.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> BuildEnvironment(
        ComputeBackend backend,
        IReadOnlyDictionary<string, string> overrides,
        MoeExpertPlacement placement = MoeExpertPlacement.Automatic,
        VulkanGraphics? graphics = null,
        long modelBytes = 0)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        // Genuinely Vulkan's, and the reason this block existed at all: a ggml-vulkan environment
        // variable working around the bfloat16 path (parakeet.cpp issue #62's neighbour).
        if (backend == ComputeBackend.Vulkan)
        {
            environment["GGML_VK_DISABLE_BFLOAT16"] = "1";
        }

        // **The picker reached nothing on CUDA until 2026-08-29, and now it does.** Expert
        // placement was written inside the Vulkan block above rather than beside it, because the
        // measurement that produced it was a Vulkan/UMA failure — so choosing "System memory" on a
        // CUDA machine set no environment at all and changed nothing, which is the silently-inert
        // control this window refuses to ship everywhere else. The vendored CUDA drop takes the
        // same flags (`--cpu-moe`, `--n-cpu-moe`, `--no-host` are all in its `--help`, checked
        // 2026-08-29), so nothing about the binary required that.
        //
        // **`Automatic` deliberately does NOT use the fit rule on CUDA**, and this is the part
        // that was nearly got wrong. The rule wants the file plus a quarter of it plus a gibibyte
        // — about 20.8 GiB for the 26B-A4B at UD-Q4_K_XL — and its allowance was anchored to a
        // Vulkan/UMA measurement. Applied to a discrete card it offloads models that run perfectly
        // well: measured 2026-08-29 on an RTX 5080, that model loads on CUDA with no offload at
        // all, using 15,731 MiB of 16,303 MiB and generating at 22.4 tok/s. Part of the file never
        // reaches the card — VRAM used is some 493 MiB *below* the file size, and that figure
        // already includes the KV cache — so "does the file fit in VRAM" is the wrong question
        // here in a way it is not on a UMA split. Until there is a discrete-card measurement of
        // what "does not fit" costs, `Automatic` on CUDA keeps doing what it was measured doing.
        var offloadExperts = backend switch
        {
            ComputeBackend.Cpu => false,
            ComputeBackend.Vulkan => ExpertsGoToSystemMemory(placement, graphics, modelBytes),
            _ => placement == MoeExpertPlacement.SystemMemory,
        };

        if (offloadExperts)
        {
            // Mixture-of-experts weights stay in system memory. Measured on the second machine's
            // Vulkan 2026-08-24 (docs/UNPROVEN.md): a 26B-class mixture that runs comfortably with
            // this cannot load at all without it.
            environment["LLAMA_ARG_CPU_MOE"] = "1";

            // **`--no-host` stays Vulkan's, and that is the half of "both or neither" that was
            // actually about the backend.** The overflow it fixes is a UMA driver splitting its
            // memory into two ~7.8 GiB heaps, where "CPU" placement lands in the pinned heap and
            // exceeds it; a discrete card has no such split, so setting it here would be carrying
            // a workaround to hardware that does not have the fault. `--cpu-moe` without
            // `--no-host` is the combination measured to fail *on that UMA machine*, which is a
            // statement about that machine rather than about the pair of flags.
            if (backend == ComputeBackend.Vulkan)
            {
                environment["LLAMA_ARG_NO_HOST"] = "1";
            }
        }

        foreach (var (name, value) in overrides)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>
    /// The automatic rule, and the two ways to overrule it. Pure and internal so the suite holds
    /// it without a Vulkan loader: the probe answers <see cref="VulkanGraphics"/>, and everything
    /// that turns on the answer is here.
    /// </summary>
    internal static bool ExpertsGoToSystemMemory(
        MoeExpertPlacement placement, VulkanGraphics? graphics, long modelBytes) =>
        placement switch
        {
            MoeExpertPlacement.Device => false,
            MoeExpertPlacement.SystemMemory => true,

            // Automatic: a card holds its own experts when they fit on it. Everything else — the
            // processor's graphics, a machine that could not be asked, and a card too small for
            // the model in front of it — does not.
            _ => graphics is not { Class: GpuClass.Discrete } card
                 || !FitsOnDevice(modelBytes, card.DeviceLocalBytes),
        };

    /// <summary>
    /// Whether a model of <paramref name="modelBytes"/> belongs on a device with
    /// <paramref name="deviceLocalBytes"/> of its own memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keying the placement on the device's <i>type</i> alone was wrong on the machine that has a
    /// card too small for the model: an 8 GiB card is a `DISCRETE_GPU`, and a 26B-class mixture at
    /// IQ4_XS is about 14 GiB, so "is there a card" answered yes to a question that was really
    /// "does this fit". Off by a whole model.
    /// </para>
    /// <para>
    /// <b>The allowance is anchored to one measurement and is deliberately conservative.</b> The
    /// 9B Q8_0 — an 8.87 GiB file — held about 11.7 GiB on the desktop's card at a 53,248-token
    /// context (2026-08-24, docs/UNPROVEN.md): 2.83 GiB above the file, of which 1.63 GiB was KV
    /// at that length. A quarter of the file plus a gibibyte is 3.2 GiB on that model, so the rule
    /// asks for more room than the largest load in the record actually took. It has to be
    /// conservative in that direction because the two errors are not equal: refusing a card that
    /// would have fitted costs speed, and accepting one that does not costs a model that will not
    /// load, since the engine runs with <c>--fit off</c> so nothing silently trims to fit.
    /// </para>
    /// <para>
    /// <b>What it does not know:</b> the KV cost per token is a property of the architecture and
    /// is not readable from the file, so the allowance does not grow with
    /// <see cref="LlamaServerOptions.ContextSize"/> — a whole-transcript ask on a card at the
    /// margin can still be a load this rule expected to fit. Unmeasured, and marked so.
    /// A file size or a heap size of zero means "not known", and not-known does not fit.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The model file's size, or zero when it cannot be read. Never throws: a placement decision
    /// must not be the thing that stops a load, and zero is the answer that keeps the experts
    /// where they are known to work.
    /// </summary>
    private static long SizeOrZero(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    internal static bool FitsOnDevice(long modelBytes, long deviceLocalBytes) =>
        modelBytes > 0
        && deviceLocalBytes > 0
        && modelBytes + (modelBytes / 4) + GibiByte <= deviceLocalBytes;

    private const long GibiByte = 1L << 30;

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
                    $"llama-server did not become healthy within {timeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s - a /health probe was accepted and then stalled. Its last lines:\n{OutputTail}");
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
