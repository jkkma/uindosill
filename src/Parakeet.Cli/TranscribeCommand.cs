using System.Globalization;
using Parakeet.Audio;
using Parakeet.Core.Diarisation;
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
        var speakerOptions = BuildSpeakerOptions(parsed);

        if (speakerOptions is null && formats.Contains(TranscriptFormats.Rttm.Id, StringComparer.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                "-f rttm carries speaker turns, and there are none without --speakers; an empty .rttm would be written. " +
                "Add --speakers or drop the format.");
        }

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
            DisableVulkanBFloat16 = EngineFactory.ParseVulkanBFloat16(
                parsed.HasFlag("vk-disable-bf16"), parsed.HasFlag("vk-bf16")),
        });

        if (parsed.HasFlag("fake"))
        {
            context.WriteError("Using the canned engine: the audio pipeline is real, the words are not.");
        }

        // The opt-in. Off, nothing below changes; on, a labeller is created — or refused, in this
        // build, unless the canned one was asked for — and every file gets a second pass.
        await using var labeller = speakerOptions is null ? null : CreateLabeller(context, parsed, speakerOptions);

        WarnAboutThreads(context, parsed, engine);

        var runner = new BatchTranscriptionRunner((job, progress, token) =>
            RunOneAsync(context, engine, labeller, job, options, speakerOptions, progress, quiet, token));

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

    /// <summary>Null when <c>--speakers</c> was not given: the whole feature is behind that flag.</summary>
    private static SpeakerLabellingOptions? BuildSpeakerOptions(ParsedCommandLine parsed)
    {
        if (!parsed.HasFlag("speakers"))
        {
            if (parsed.Value("speaker-count") is { Length: > 0 })
            {
                throw new CliUsageException("--speaker-count only means something with --speakers.");
            }

            return null;
        }

        // Refused here, before any engine is resolved, so the message the user sees is about the
        // labeller and not about whichever ASR model happens not to be installed.
        if (!parsed.HasFlag("fake"))
        {
            throw new CliUsageException(NoLabellerMessage);
        }

        int? count = null;
        if (parsed.Value("speaker-count") is { Length: > 0 } countText)
        {
            if (!CommandLineParser.TryParseInt(countText, out var value) || value < 1)
            {
                throw new CliUsageException($"--speaker-count needs a positive integer, got '{countText}'.");
            }

            count = value;
        }

        var options = new SpeakerLabellingOptions { SpeakerCount = count };
        options.Validate();
        return options;
    }

    /// <summary>
    /// The only labeller this build has is the canned one. Saying so, and stopping, is the honest
    /// answer to <c>--speakers</c> without <c>--fake</c>: the seam is built, the model behind it is
    /// still being chosen by measurement (docs/PHASES.md), and a flag that silently did nothing
    /// would print a transcript with no names and let the user think nobody was found.
    /// </summary>
    private const string NoLabellerMessage =
        "--speakers: no speaker labeller is available in this build. The diarisation model is still being chosen " +
        "by measurement (docs/PHASES.md § Decisions taken 2026-08-16); the seam it will plug into is here, and " +
        "--speakers --fake exercises it with canned speakers.";

    private static ISpeakerLabeller CreateLabeller(CliContext context, ParsedCommandLine parsed, SpeakerLabellingOptions options)
    {
        if (!parsed.HasFlag("fake"))
        {
            throw new CliUsageException(NoLabellerMessage);
        }

        context.WriteError("Using the canned speaker labeller: the second pass over the audio is real, the speakers are not.");
        var labeller = new FakeSpeakerLabeller();

        // The seam's capabilities are the caller's to honour: a count the labeller cannot fix is
        // said out loud rather than silently dropped.
        if (options.SpeakerCount is { } count && !labeller.Capabilities.SupportsFixedSpeakerCount)
        {
            context.WriteError($"--speaker-count {count} was given but this labeller decides the count itself; the value is ignored.");
        }

        return labeller;
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
        ISpeakerLabeller? labeller,
        TranscriptionJob job,
        TranscriptionOptions options,
        SpeakerLabellingOptions? speakerOptions,
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

        string? speakerWarning = null;
        if (labeller is not null && speakerOptions is not null)
        {
            // Both audio sources are single-read, so the second pass opens the file again. That is
            // a second decode of the whole file, and it is a cost only the opt-in pays.
            await using var second = AudioSources.Open(job.InputPath);
            document = await SpeakerLabelling.LabelAsync(
                document, labeller, second, speakerOptions, progress, ct).ConfigureAwait(false);
            speakerWarning = SpeakerLabelling.DescribeLimit(labeller, document);
        }

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
            // Silence wins when both apply: an empty transcript has no segments to flag, and the
            // reason it is empty is the only thing worth saying about it. A labeller at its speaker
            // cap is said after either, because it is about the names and not the words.
            Warning = Join(DescribeSilence(engine, document) ?? DescribeAnomalies(document, options), speakerWarning),
        };
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first} {second}";

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

    /// <summary>
    /// Points at the segments worth re-reading. Both signals come from data the engine already
    /// reported, and neither is a correctness claim: a script change says the decoder emitted a
    /// different alphabet there, which on this checkpoint is what its own language detection
    /// looks like from the outside. Nothing in the CLI can constrain that — <c>--language</c>
    /// reaches the ABI and a non-prompt model ignores it — so saying where it happened is the
    /// whole of what the tool can honestly do.
    /// </summary>
    private static string? DescribeAnomalies(TranscriptDocument document, TranscriptionOptions options)
    {
        var anomalies = TranscriptAnalysis.Analyse(document, options.LowConfidenceThreshold);
        if (anomalies.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();

        var script = anomalies.Where(a => a.Kind is TranscriptAnomalyKind.ScriptDisagreement).ToList();
        if (script.Count > 0)
        {
            var where = string.Join(
                ", ",
                script.Select(a => string.Create(
                    CultureInfo.InvariantCulture, $"{a.Start:hh\\:mm\\:ss} {a.Detail}")));

            parts.Add(
                (script.Count == 1 ? "One segment changed script" : $"{script.Count} segments changed script") +
                $": {where}. That describes the text, not the speech: the model chose another language for that " +
                "stretch, and --language cannot constrain it on this checkpoint.");
        }

        var low = anomalies.Count(a => a.Kind is TranscriptAnomalyKind.LowConfidence);
        if (low > 0)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{low} {(low == 1 ? "segment carries" : "segments carry")} a word below " +
                $"{options.LowConfidenceThreshold:0.##}."));
        }

        return string.Join(" ", parts);
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
