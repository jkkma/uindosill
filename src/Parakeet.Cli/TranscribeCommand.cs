using System.Globalization;
using Parakeet.Audio;
using Parakeet.Core.Formatting;
using Parakeet.Core.Jobs;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Transcription;

namespace Parakeet.Cli;

internal static class TranscribeCommand
{
    public static async Task<int> RunAsync(CliContext context, ParsedCommandLine parsed, CancellationToken ct)
    {
        if (parsed.Positionals.Count == 0)
        {
            context.WriteError("transcribe needs at least one input file.");
            return ExitCodes.UsageError;
        }

        var formats = CommandLineParser.SplitList(parsed.Values("format"));
        if (formats.Count == 0)
        {
            formats = ["txt"];
        }

        foreach (var format in formats)
        {
            if (!TranscriptFormats.TryGet(format, out _))
            {
                context.WriteError(
                    $"Unknown format '{format}'. Known formats: {string.Join(", ", TranscriptFormats.Ids)}.");
                return ExitCodes.UsageError;
            }
        }

        var overwrite = (parsed.HasFlag("overwrite"), parsed.HasFlag("skip-existing")) switch
        {
            (true, true) => throw new CliUsageException("--overwrite and --skip-existing contradict each other."),
            (true, false) => OverwritePolicy.Overwrite,
            (false, true) => OverwritePolicy.Skip,
            _ => OverwritePolicy.Rename,
        };

        var options = BuildOptions(parsed);
        var quiet = parsed.HasFlag("quiet");

        var jobs = parsed.Positionals
            .Select(path => new TranscriptionJob
            {
                InputPath = path,
                Formats = formats,
                OutputDirectory = parsed.Value("out"),
                Overwrite = overwrite,
            })
            .ToList();

        await using var engine = EngineFactory.Create(context, new EngineRequest
        {
            Fake = parsed.HasFlag("fake"),
            ModelId = parsed.Value("model"),
            ModelPath = parsed.Value("model-path"),
            Backend = EngineFactory.ParseBackend(parsed.Value("backend")),
            NativeDirectory = parsed.Value("native-dir"),
            DisableVulkanBFloat16 = parsed.HasFlag("vk-disable-bf16"),
        });

        if (parsed.HasFlag("fake"))
        {
            context.WriteError("Using the canned engine: the audio pipeline is real, the words are not.");
        }

        WarnAboutThreads(context, parsed, engine);

        var runner = new BatchTranscriptionRunner((job, progress, token) =>
            RunOneAsync(context, engine, job, options, progress, quiet, token));

        var results = await runner.RunAsync(jobs, progress: null, ct).ConfigureAwait(false);

        return Report(context, results, quiet);
    }

    private static TranscriptionOptions BuildOptions(ParsedCommandLine parsed)
    {
        var maxSegment = TimeSpan.FromSeconds(30);
        if (parsed.Value("max-segment") is { Length: > 0 } raw)
        {
            if (!CommandLineParser.TryParseDouble(raw, out var seconds) || seconds <= 0)
            {
                throw new CliUsageException($"--max-segment needs a positive number of seconds, got '{raw}'.");
            }

            maxSegment = TimeSpan.FromSeconds(seconds);
        }

        int? threads = null;
        if (parsed.Value("threads") is { Length: > 0 } threadText)
        {
            if (!CommandLineParser.TryParseInt(threadText, out var value) || value < 1)
            {
                throw new CliUsageException($"--threads needs a positive integer, got '{threadText}'.");
            }

            threads = value;
        }

        var options = new TranscriptionOptions
        {
            Language = parsed.Value("language"),
            ThreadCount = threads,
            MaxSegmentLength = maxSegment,
            VoiceActivity = parsed.HasFlag("no-vad")
                ? VoiceActivityOptions.Disabled with { MaxSegmentLength = maxSegment }
                : VoiceActivityOptions.Default with { MaxSegmentLength = maxSegment },
        };

        options.Validate();
        return options;
    }

    private static void WarnAboutThreads(CliContext context, ParsedCommandLine parsed, ITranscriptionEngine engine)
    {
        if (parsed.Value("threads") is not { Length: > 0 } || engine.Capabilities.SupportsThreadCount)
        {
            return;
        }

        context.WriteError(
            "--threads was given but this engine ignores it: the parakeet.cpp ABI takes no thread count on any " +
            "entry point, so ggml decides. The value is recorded in the run summary and nowhere else.");
    }

    private static async Task<JobResult> RunOneAsync(
        CliContext context,
        ITranscriptionEngine engine,
        TranscriptionJob job,
        TranscriptionOptions options,
        IProgress<TranscriptionProgress>? _,
        bool quiet,
        CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;

        if (!File.Exists(job.InputPath))
        {
            return new JobResult
            {
                Job = job,
                State = JobState.Failed,
                Error = $"File not found: {job.InputPath}",
            };
        }

        await using var audio = AudioSources.Open(job.InputPath);

        IProgress<TranscriptionProgress>? progress = null;
        if (!quiet && context.Interactive)
        {
            progress = new Progress<TranscriptionProgress>(p => WriteProgress(context, job, p));
        }

        var document = await TranscriptionRunner.RunAsync(
            engine, audio, options, job.DisplayName, progress, ct).ConfigureAwait(false);

        if (!quiet && context.Interactive)
        {
            context.Error.Write('\r');
            context.Error.Write(new string(' ', 78));
            context.Error.Write('\r');
        }

        var files = await TranscriptWriter.WriteAsync(document, job, ct: ct).ConfigureAwait(false);

        return new JobResult
        {
            Job = job,
            State = JobState.Completed,
            Document = document,
            OutputFiles = files,
            Elapsed = DateTimeOffset.UtcNow - started,
            Warning = DescribeSilence(engine, document),
        };
    }

    /// <summary>
    /// Turns "the transcript is empty" into an explanation. An empty file with no message is
    /// indistinguishable from a broken install, and it is the single most common way a local
    /// transcription tool wastes somebody's afternoon.
    /// </summary>
    private static string? DescribeSilence(ITranscriptionEngine engine, TranscriptDocument document)
    {
        if (!document.IsEmpty)
        {
            return null;
        }

        var report = (engine as SegmentingTranscriptionEngine)?.LastSegmentationReport;

        if (report is null)
        {
            return "No speech was transcribed.";
        }

        if (report.IsDigitalSilence)
        {
            return "Every sample in this file is exactly zero: the track is digitally silent. " +
                   "If the recording should have sound, the wrong track or the wrong input device was captured.";
        }

        if (report.LooksLikeMissedSpeech)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"The file contains audio (peak {report.PeakDb:0.#} dBFS, noise floor {report.NoiseFloorDb:0.#} dBFS) " +
                $"but voice-activity detection found nothing to decode. Re-run with --no-vad to transcribe it in " +
                $"fixed windows.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"No speech found. Peak level {report.PeakDb:0.#} dBFS across {report.TotalAudio:hh\\:mm\\:ss} of audio.");
    }

    private static void WriteProgress(CliContext context, TranscriptionJob job, TranscriptionProgress progress)
    {
        var fraction = progress.Fraction;
        var percent = fraction is { } f ? $"{f * 100:0}%" : "  ?";
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"\r{job.DisplayName,-32} {progress.Stage,-10} {percent,4} {progress.Processed:hh\\:mm\\:ss}");

        context.Error.Write(line.Length > 78 ? line[..78] : line.PadRight(78));
    }

    private static int Report(CliContext context, IReadOnlyList<JobResult> results, bool quiet)
    {
        var failed = 0;

        foreach (var result in results)
        {
            switch (result.State)
            {
                case JobState.Completed:
                    if (!quiet)
                    {
                        var rtf = result.Document?.RealTimeFactor;
                        var suffix = rtf is { } value
                            ? string.Create(CultureInfo.InvariantCulture, $" (RTF {value:0.###})")
                            : string.Empty;

                        context.WriteLine($"{result.Job.DisplayName}{suffix}");
                        foreach (var file in result.OutputFiles)
                        {
                            context.WriteLine($"  wrote {file}");
                        }

                        if (result.OutputFiles.Count == 0)
                        {
                            context.WriteLine("  wrote nothing (existing output kept)");
                        }
                    }

                    if (result.Warning is { } warning)
                    {
                        context.WriteError($"{result.Job.DisplayName}: {warning}");
                    }

                    break;

                case JobState.Failed:
                    failed++;
                    context.WriteError($"{result.Job.DisplayName}: {result.Error}");
                    break;

                case JobState.Cancelled:
                    context.WriteError($"{result.Job.DisplayName}: cancelled");
                    break;

                default:
                    break;
            }
        }

        if (failed == 0)
        {
            return ExitCodes.Success;
        }

        return failed == results.Count ? ExitCodes.RuntimeError : ExitCodes.PartialFailure;
    }
}
