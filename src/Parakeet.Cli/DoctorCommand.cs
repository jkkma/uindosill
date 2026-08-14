using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Parakeet.Audio;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp.Interop;

namespace Parakeet.Cli;

internal static class DoctorCommand
{
    private static readonly ComputeBackend[] Backends = [ComputeBackend.Cpu, ComputeBackend.Vulkan, ComputeBackend.Cuda];

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

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

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
