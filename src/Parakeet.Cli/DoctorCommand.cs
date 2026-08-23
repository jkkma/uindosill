using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Parakeet.Audio;
using Parakeet.Core.Models;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp.Interop;
using Parakeet.Engine.Python;
using Parakeet.Engine.SileroVad;

namespace Parakeet.Cli;

internal static class DoctorCommand
{
    private static readonly ComputeBackend[] Backends = [ComputeBackend.Cpu, ComputeBackend.Vulkan, ComputeBackend.Cuda];

    /// <summary>How long a backend probe gets before it is killed and reported as hung.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(CliContext context, ParsedCommandLine parsed, CancellationToken ct)
    {
        context.WriteLine("Environment");
        context.WriteLine($"  runtime         {RuntimeInformation.FrameworkDescription}");
        context.WriteLine($"  os              {RuntimeInformation.OSDescription}");
        context.WriteLine($"  architecture    {RuntimeInformation.ProcessArchitecture}");
        context.WriteLine($"  rid             {RuntimeInformation.RuntimeIdentifier}");
        context.WriteLine($"  processors      {Environment.ProcessorCount}");
        context.WriteLine($"  base directory  {AppContext.BaseDirectory}");
        context.WriteLine();

        context.WriteLine("Audio");
        context.WriteLine($"  managed WAVE reader: RIFF, RF64, BW64; 8/16/24/32-bit PCM, 32/64-bit float, extensible");
        context.WriteLine($"  openable here:       {string.Join(" ", AudioSources.SupportedExtensions)}");
        if (!OperatingSystem.IsWindows())
        {
            context.WriteLine("  compressed containers need Media Foundation, which exists only on Windows");
        }

        context.WriteLine();

        context.WriteLine("Models");
        context.WriteLine($"  directory  {context.Store.RootDirectory}");
        var installed = context.Store is LocalModelStore local ? local.ListInstalled(context.Catalog) : [];
        if (installed.Count == 0)
        {
            context.WriteLine("  none installed");
        }
        else
        {
            foreach (var model in installed)
            {
                context.WriteLine($"  {model.Id,-32} {ModelsCommand.Bytes(model.SizeBytes),10}" +
                                  (model.IsSideloaded ? "  (sideloaded)" : string.Empty));
            }
        }

        context.WriteLine();
        context.WriteLine("Python (speaker labelling and translation run in it)");

        // Which of the three places answered, printed rather than left to be inferred. A figure
        // taken against an unknown interpreter is a figure nobody can reproduce, and until this
        // existed the only way to know which bundle a run had used was to reason about the
        // environment it ran in. The bundle is also a separate download since 2026-08-21, so "which
        // one is this" became a question a user can have rather than only a measurer.
        if (PythonRuntime.TryResolve(out var python, out var whyNot))
        {
            context.WriteLine($"  interpreter  {python!.Interpreter}");
            context.WriteLine($"  packages     {python.PackageRoot}");
            context.WriteLine($"  found        {python.SourceDescription}");
        }
        else
        {
            // Not a failure of `doctor`: a machine with no bundle is a machine where two opt-ins are
            // unavailable and everything else works, which is what this command exists to report.
            context.WriteLine("  not available, so --translate and speaker labelling are not either:");
            context.WriteLine($"  {whyNot}");
        }

        context.WriteLine();
        context.WriteLine("Speech detection (the default whenever this model is installed; runs in this process, on the CPU)");

        // In process rather than in a child, unlike the backends below: ONNX Runtime loading a
        // two-megabyte graph has no static initialiser that can take the process down, and what
        // this answers — is the model there, does the runtime open it — is the pair of facts the
        // window's checkbox is disabled over when either is missing.
        var detectionEntry = context.Catalog.VoiceActivityModels.FirstOrDefault();
        if (detectionEntry is null)
        {
            context.WriteLine("  no speech-detection entry in the catalogue");
        }
        else if (context.Store.PathFor(detectionEntry) is var detectionPath && !File.Exists(detectionPath))
        {
            context.WriteLine(
                $"  {detectionEntry.Id,-24} not installed — transcribe cuts on the energy gate until it is: " +
                $"uindosill models download {detectionEntry.Id}");
        }
        else
        {
            try
            {
                using var detector = new SileroSpeechDetector(detectionPath);
                context.WriteLine($"  {detectionEntry.Id,-24} ok — {detector.Name}");
            }
            catch (SpeechDetectorException exception)
            {
                context.WriteLine($"  {detectionEntry.Id,-24} installed but will not load: {exception.Message}");
            }
        }

        context.WriteLine();
        context.WriteLine("Backends (each probed in a child process)");

        var nativeDirectory = parsed.Value("native-dir");
        var anyWorking = false;

        foreach (var backend in Backends)
        {
            var result = await ProbeAsync(backend, nativeDirectory, ct).ConfigureAwait(false);
            context.WriteLine($"  {backend.ToString().ToLowerInvariant(),-8} {result}");
            anyWorking |= result.StartsWith("ok", StringComparison.Ordinal);
        }

        context.WriteLine();
        if (!anyWorking)
        {
            context.WriteLine(
                "No backend loaded. Vendor a pinned parakeet.cpp release into native/<rid>/<backend> " +
                "(docs/NATIVE-BINARIES.md) or set " + ParakeetNativeLibrary.DirectoryEnvironmentVariable + ".");
        }

        context.WriteLine(
            "A backend reported as 'crashed at load' is the AVX2 static-initialiser failure: the binary was built " +
            "with an instruction-set baseline this CPU does not have, and it dies before any code of ours runs. " +
            "Rebuild it with GGML_NATIVE=OFF (or GGML_CPU_ALL_VARIANTS for runtime dispatch) — no amount of " +
            "handling in this process can catch it.");

        return ExitCodes.Success;
    }

    /// <summary>
    /// Runs <c>uindosill probe --backend X</c> as a child process and interprets its exit.
    /// </summary>
    /// <remarks>
    /// The child is the whole point. Loading a native built for a newer instruction set can
    /// execute an illegal instruction from a static initialiser, which kills the process
    /// outright — no exception to catch, no stack trace to print. In a child, that becomes an
    /// exit code and a line of output instead of the tool vanishing.
    /// </remarks>
    private static async Task<string> ProbeAsync(ComputeBackend backend, string? nativeDirectory, CancellationToken ct)
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            return "skipped (cannot locate this executable to re-launch)";
        }

        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // A framework-dependent launch goes through `dotnet <dll>`; re-launching the host with
        // the same dll keeps the probe working in both shapes.
        if (Path.GetFileNameWithoutExtension(executable) is "dotnet")
        {
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        start.ArgumentList.Add("probe");
        start.ArgumentList.Add("--backend");
        start.ArgumentList.Add(backend.ToString().ToLowerInvariant());

        if (nativeDirectory is { Length: > 0 })
        {
            start.ArgumentList.Add("--native-dir");
            start.ArgumentList.Add(nativeDirectory);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return "skipped (could not start the probe process)";
        }

        // Both pipes must be drained CONCURRENTLY. Reading stdout to completion first deadlocks
        // the moment the child writes more to stderr than the pipe buffer holds (~4 KB on
        // Windows): the child blocks writing stderr, this process blocks reading stdout, and
        // neither moves again. A failing probe reports every path it tried, which is well past
        // that, so the failure path was the one that hung — the success path never did.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // A probe that never answers must not take the diagnostic down with it. A tool whose job
        // is to explain why things are broken is the last place to hang without a message.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout firing and the kill; nothing to do.
            }

            return $"timed out after {ProbeTimeout.TotalSeconds:0} s and was killed";
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode == 0)
        {
            return "ok — " + stdout.Trim();
        }

        // A negative or signal-shaped exit code means the child died rather than exited.
        if (process.ExitCode is < 0 or > 128)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"crashed at load (exit {process.ExitCode}) — see the note below");
        }

        var message = stderr.Trim();
        var firstLine = message.Split('\n', 2)[0];
        return $"unavailable — {(firstLine.Length == 0 ? "no message" : firstLine)}";
    }

    /// <summary>The child half of <see cref="ProbeAsync"/>.</summary>
    public static int Probe(CliContext context, ParsedCommandLine parsed)
    {
        var backend = EngineFactory.ParseBackend(parsed.Value("backend"));

        try
        {
            ParakeetNativeLibrary.Configure(backend, allowFallback: false, parsed.Value("native-dir"));
            var abi = ParakeetNativeLibrary.EnsureLoadedAndCompatible();
            context.WriteLine($"abi {abi} from {ParakeetNativeLibrary.LoadedPath}");

            // Outside the C ABI, so a re-vendored build can silently lack it — and then a CUDA
            // process is back to aborting on exit. Said here, where the binary is identified,
            // and only when it is missing: every vendored build so far has it.
            if (ParakeetNativeLibrary.ShutdownBackendAvailable == false)
            {
                context.WriteLine(
                    "  no pk::shutdown_backend export — on cuda the process will abort with 0xC0000409 at " +
                    "exit after a good run (docs/GOTCHAS.md, gotcha 19)");
            }

            return ExitCodes.Success;
        }
        catch (ParakeetNativeLoadException ex)
        {
            context.WriteError(ex.Message);
            return ExitCodes.RuntimeError;
        }
        catch (ParakeetAbiMismatchException ex)
        {
            context.WriteError(ex.Message);
            return ExitCodes.RuntimeError;
        }
    }
}
