using System.Diagnostics;
using System.Globalization;
using Parakeet.Audio;
using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp;

namespace Parakeet.Cli;

internal static class BenchCommand
{
    public static async Task<int> RunAsync(CliContext context, ParsedCommandLine parsed, CancellationToken ct)
    {
        if (parsed.Positionals.Count == 0)
        {
            context.WriteError("bench needs an audio file. Use a real recording: clean text-to-speech decodes " +
                               "identically under conditions that break real speech, so it measures nothing useful.");
            return ExitCodes.UsageError;
        }

        var path = parsed.Positionals[0];
        if (!File.Exists(path))
        {
            context.WriteError($"File not found: {path}");
            return ExitCodes.UsageError;
        }

        var repeats = 3;
        if (parsed.Value("repeat") is { Length: > 0 } repeatText)
        {
            if (!CommandLineParser.TryParseInt(repeatText, out repeats) || repeats < 1)
            {
                context.WriteError($"--repeat needs a positive integer, got '{repeatText}'.");
                return ExitCodes.UsageError;
            }
        }

        var batchSizes = new List<int>();
        foreach (var raw in CommandLineParser.SplitList(parsed.Values("batch")))
        {
            if (!CommandLineParser.TryParseInt(raw, out var size) || size < 1)
            {
                context.WriteError($"--batch needs positive integers, got '{raw}'.");
                return ExitCodes.UsageError;
            }

            batchSizes.Add(size);
        }

        if (batchSizes.Count == 0)
        {
            batchSizes.Add(4);
        }

        var warmUp = !parsed.HasFlag("no-warmup");
        var options = TranscriptionOptions.Default with { Language = parsed.Value("language") };

        context.WriteLine($"file:     {path}");
        context.WriteLine($"machine:  {Environment.OSVersion.VersionString}, {Environment.ProcessorCount} logical processors");
        context.WriteLine($"threads:  not settable — the parakeet.cpp ABI takes no thread count (recommended policy would be {DecodeThreadPlanner.Recommended()})");
        context.WriteLine($"warm-up:  {(warmUp ? "yes" : "NO — the first pass below is inflated by arena allocation and graph construction")}");
        context.WriteLine();
        context.WriteLine("batch  pass  audio      decode     RTF     peak RSS");

        var anyFailure = false;

        foreach (var batchSize in batchSizes)
        {
            await using var engine = EngineFactory.Create(context, new EngineRequest
            {
                Fake = parsed.HasFlag("fake"),
                ModelId = parsed.Value("model"),
                ModelPath = parsed.Value("model-path"),
                Backend = EngineFactory.ParseBackend(parsed.Value("backend")),
                NativeDirectory = parsed.Value("native-dir"),
                WarmUp = warmUp,
                BatchSize = batchSize,
            });

            var loadStopwatch = Stopwatch.StartNew();
            await engine.LoadAsync(ct).ConfigureAwait(false);
            loadStopwatch.Stop();

            // Cold load is reported on its own line, never folded into a decode number: a
            // combined figure is the single easiest way to publish a real-time factor that
            // nobody else can reproduce.
            var warmUpNote = engine is ParakeetCppEngine { WarmUpDuration: { } warm }
                ? string.Create(CultureInfo.InvariantCulture, $", warm-up decode {warm.TotalSeconds:0.000} s")
                : string.Empty;

            context.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"batch {batchSize}: cold load {loadStopwatch.Elapsed.TotalSeconds:0.000} s{warmUpNote}"));

            for (var pass = 1; pass <= repeats; pass++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await using var audio = AudioSources.Open(path);
                    var stopwatch = Stopwatch.StartNew();
                    var document = await TranscriptionRunner.RunAsync(
                        engine, audio, options, Path.GetFileName(path), null, ct).ConfigureAwait(false);
                    stopwatch.Stop();

                    var duration = document.AudioDuration ?? TimeSpan.Zero;
                    var rtf = duration > TimeSpan.Zero
                        ? stopwatch.Elapsed.TotalSeconds / duration.TotalSeconds
                        : double.NaN;

                    context.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{batchSize,5}  {pass,4}  {duration.TotalSeconds,8:0.00}s  " +
                        $"{stopwatch.Elapsed.TotalSeconds,8:0.000}s  {rtf,6:0.000}  " +
                        $"{ModelsCommand.Bytes(PeakWorkingSet()),10}"));
                }
#pragma warning disable CA1031 // A failed pass is a data point, not a reason to abandon the sweep.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    anyFailure = true;
                    context.WriteError($"batch {batchSize} pass {pass} failed: {ex.Message}");
                }
            }
        }

        context.WriteLine();
        context.WriteLine(
            "Numbers from one machine describe one machine. Nothing here has been measured against real weights " +
            "on real Windows hardware, and no independent Parakeet benchmark on a 4–8 core mobile chip exists to " +
            "compare against, so treat any figure you did not produce yourself as marketing.");

        return anyFailure ? ExitCodes.PartialFailure : ExitCodes.Success;
    }

    private static long PeakWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PeakWorkingSet64;
    }
}
