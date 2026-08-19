using System.Reflection;
using System.Text;
using Parakeet.Audio;
using Parakeet.Cli;
using Parakeet.Core.Models;
using Parakeet.Engine.ParakeetCpp;
using Parakeet.Engine.ParakeetCpp.Interop;

Console.OutputEncoding = Encoding.UTF8;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C asks for a clean stop; a second one lets the runtime kill the process.
    e.Cancel = !cancellation.IsCancellationRequested;
    cancellation.Cancel();
};

return await CliEntryPoint.RunAsync(args, CliContext.CreateDefault(), cancellation.Token).ConfigureAwait(false);

namespace Parakeet.Cli
{
    internal static class CliEntryPoint
    {
        public static async Task<int> RunAsync(string[] args, CliContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(context);

            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
            {
                WriteUsage(context);
                return args.Length == 0 ? ExitCodes.UsageError : ExitCodes.Success;
            }

            if (args[0] is "--version" or "-v")
            {
                context.WriteLine(Version());
                return ExitCodes.Success;
            }

            var commandName = args[0];
            var command = Commands.All.FirstOrDefault(c => c.Name == commandName);
            if (command is null)
            {
                context.WriteError($"Unknown command '{commandName}'.");
                WriteUsage(context);
                return ExitCodes.UsageError;
            }

            var parsed = CommandLineParser.Parse(command, args[1..]);

            if (parsed.HasFlag("help"))
            {
                context.WriteLine(CommandLineParser.RenderHelp(command));
                return ExitCodes.Success;
            }

            if (parsed.HasErrors)
            {
                foreach (var error in parsed.Errors)
                {
                    context.WriteError(error);
                }

                return ExitCodes.UsageError;
            }

            try
            {
                return await DispatchAsync(context, command, parsed, ct).ConfigureAwait(false);
            }
            catch (CliUsageException ex)
            {
                context.WriteError(ex.Message);
                return ExitCodes.UsageError;
            }
            catch (ArgumentException ex)
            {
                // Options are validated by the same code the library uses, so a rejected value
                // arrives here as an argument exception. It is the user's input that is wrong,
                // which makes it a usage error rather than a crash.
                context.WriteError(ex.Message);
                return ExitCodes.UsageError;
            }
            catch (OperationCanceledException)
            {
                context.WriteError("Cancelled.");
                return ExitCodes.RuntimeError;
            }
            catch (Exception ex) when (ex is ParakeetNativeException
                                          or ParakeetNativeLoadException
                                          or ParakeetAbiMismatchException
                                          or AudioDecodeException
                                          or ModelInstallException
                                          or FileNotFoundException
                                          or DirectoryNotFoundException
                                          or IOException
                                          or UnauthorizedAccessException)
            {
                context.WriteError(ex.Message);
                return ExitCodes.RuntimeError;
            }
            finally
            {
                // Every command's engine has been disposed by now (they are `await using`), so this
                // is the one place to release parakeet.cpp's process-global backend while the GPU
                // driver is still alive. Left to static destruction, a CUDA run that wrote a
                // perfect transcript exited 0xC0000409 (gotcha 19) — which is what made
                // measure-transcribe.ps1 call good runs failures. A no-op when no native library
                // was loaded, and upstream's own CLI does the same after every subcommand.
                ParakeetNativeLibrary.TryShutdownBackend();
            }
        }

        private static Task<int> DispatchAsync(
            CliContext context, CommandSpec command, ParsedCommandLine parsed, CancellationToken ct)
        {
            if (command == Commands.Transcribe)
            {
                return TranscribeCommand.RunAsync(context, parsed, ct);
            }

            if (command == Commands.Diarise)
            {
                return DiariseCommand.RunAsync(context, parsed, ct);
            }

            if (command == Commands.Models)
            {
                return ModelsCommand.RunAsync(context, parsed, ct);
            }

            if (command == Commands.Bench)
            {
                return BenchCommand.RunAsync(context, parsed, ct);
            }

            if (command == Commands.Doctor)
            {
                return DoctorCommand.RunAsync(context, parsed, ct);
            }

            if (command == Commands.Probe)
            {
                return Task.FromResult(DoctorCommand.Probe(context, parsed));
            }

            if (command == Commands.Notice)
            {
                return Task.FromResult(NoticeCommand.Run(context));
            }

            if (command == Commands.Formats)
            {
                return Task.FromResult(NoticeCommand.Formats(context));
            }

            if (command == Commands.Wer)
            {
                return Task.FromResult(WerCommand.Run(context, parsed));
            }

            if (command == Commands.Der)
            {
                return Task.FromResult(DerCommand.Run(context, parsed));
            }

            if (command == Commands.Rttm)
            {
                return Task.FromResult(RttmCommand.Run(context, parsed));
            }

            context.WriteError($"Command '{command.Name}' has no handler.");
            return Task.FromResult(ExitCodes.UsageError);
        }

        internal static void WriteUsage(CliContext context)
        {
            context.WriteLine($"uindosill {Version()} — local file transcription with NVIDIA Parakeet");
            context.WriteLine();
            context.WriteLine("Usage: uindosill <command> [options]");
            context.WriteLine();

            var width = Commands.All.Max(c => c.Name.Length);
            foreach (var command in Commands.All)
            {
                if (command == Commands.Probe)
                {
                    continue;
                }

                context.WriteLine($"  {command.Name.PadRight(width)}  {command.Summary}");
            }

            context.WriteLine();
            context.WriteLine("Run 'uindosill <command> --help' for the options of one command.");
        }

        internal static string Version() =>
            typeof(CliEntryPoint).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                .Split('+')[0]
            ?? "0.0.0";
    }
}
