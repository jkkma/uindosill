using System.Globalization;
using Parakeet.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Formatting;
using Parakeet.Core.Jobs;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;

namespace Parakeet.Cli;

internal static class TranscribeCommand
{
    /// <summary>
    /// What goes between a translated run's file name and its extension. SubRip has no comment
    /// syntax and plain text has no header, so for those two this is the only place the output
    /// can say it is not the language that was spoken; for the rest it is what keeps a
    /// translated run from overwriting a plain one under --overwrite.
    /// </summary>
    private const string TranslatedInfix = "." + TranslationTarget.LanguageTag;

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
        var translationOptions = BuildTranslationOptions(parsed);

        if (speakerOptions is null && formats.Contains(TranscriptFormats.Rttm.Id, StringComparer.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                "-f rttm carries speaker turns, and there are none without --speakers; an empty .rttm would be written. " +
                "Add --speakers or drop the format.");
        }

        // Resolved before anything else, for the same reason the diariser is: the answer to
        // "--translate, and this build has no translator" is not a message about the ASR weights,
        // and it is not one that arrives after a three-hour decode.
        if (translationOptions is not null)
        {
            TranslatorFactory.Resolve(context, TranslationRequestFrom(parsed), formats);
        }

        // Resolved before the ASR engine, and only resolved — the labeller itself is built below,
        // after the engine, so the "using the canned X" messages stay in their old order. The point
        // is the message a user gets when the diariser is missing: "download the ASR model you have
        // not got" is the wrong answer to --speakers, and it is the answer they would get if the
        // engine were the first thing to fail.
        if (speakerOptions is not null && !parsed.HasFlag("fake"))
        {
            LabellerFactory.ResolveModel(context, new LabellerRequest
            {
                ModelId = parsed.Value("speaker-model"),
                ModelPath = parsed.Value("speaker-model-path"),
            });
        }

        var jobs = parsed.Positionals
            .Select(path => new TranscriptionJob
            {
                InputPath = path,
                Formats = formats,
                OutputDirectory = parsed.Value("out"),
                Overwrite = overwrite,

                // What makes a translated run's output its own rather than the transcription run's.
                StemSuffix = translationOptions is null ? string.Empty : TranslatedInfix,
            })
            .ToList();

        var requestedBackend = EngineFactory.ParseBackend(parsed.Value("backend"));

        await using var engine = EngineFactory.Create(context, new EngineRequest
        {
            Fake = parsed.HasFlag("fake"),
            ModelId = parsed.Value("model"),
            ModelPath = parsed.Value("model-path"),
            Backend = requestedBackend,
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
        await using var labeller = speakerOptions is null
            ? null
            : await CreateLabellerAsync(context, parsed, speakerOptions, ct).ConfigureAwait(false);

        // The other opt-in, created after the labeller because it runs after it. Its capabilities
        // are checked against the formats now rather than at write time: refusing a file's fourth
        // output after the first three have been written is not a refusal.
        await using var translator = translationOptions is null
            ? null
            : await TranslatorFactory.CreateAsync(context, TranslationRequestFrom(parsed), ct).ConfigureAwait(false);

        if (translator is not null)
        {
            TranslatorFactory.Check(translator, formats);
            TranslatorFactory.ReportIgnoredContext(context, translator, translationOptions!);
            WarnAboutLanguageHint(context, parsed);
        }

        WarnAboutThreads(context, parsed, engine);

        // Pre-tripped by --fake, which is the canned engine's whole point: it answers cpu whatever
        // was asked for, and a fallback line about a backend it was never going to use is noise.
        var backendChecked = parsed.HasFlag("fake");
        var backendWasNamed = parsed.Value("backend") is { Length: > 0 };

        // The engine loads lazily, on the first decode, so the backend it resolved to is not
        // knowable until something asks it to load. Doing that here — once for the batch, from the
        // first file that exists — is what puts the fallback line ahead of the fifty minutes it is
        // there to explain, rather than after them or, as it was, never.
        async ValueTask EnsureEngineLoadedAsync(CancellationToken token)
        {
            if (backendChecked)
            {
                await engine.LoadAsync(token).ConfigureAwait(false);
                return;
            }

            backendChecked = true;

            if (await LoadAndDescribeBackendAsync(engine, requestedBackend, backendWasNamed, token)
                    .ConfigureAwait(false) is { } fallback)
            {
                context.WriteError(fallback);
            }
        }

        var runner = new BatchTranscriptionRunner((job, progress, token) => RunOneAsync(
            context, engine, EnsureEngineLoadedAsync, labeller, translator, job, options, speakerOptions,
            translationOptions, progress, quiet, token));

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

    /// <summary>Which translation model the flags ask for, resolved the same way twice.</summary>
    /// <remarks>
    /// Built in one place because <c>--translate</c> resolves its model twice: once up front so a
    /// missing translator is reported before 1.34 GiB of ASR weights load, and once for real after.
    /// Two copies of this would be two chances for the pre-flight check to resolve a different
    /// model from the one that runs.
    /// </remarks>
    private static TranslatorRequest TranslationRequestFrom(ParsedCommandLine parsed) => new()
    {
        Fake = parsed.HasFlag("fake"),
        ModelId = parsed.Value("translate-model"),
        ModelPath = parsed.Value("translate-model-path"),
        Threads = ParseThreads(parsed.Value("translate-threads"), "--translate-threads"),
        Backend = parsed.Value("translate-backend"),
        AllowUnverifiedBackend = parsed.HasFlag("translate-backend-unverified"),
    };

    /// <summary>Null when <c>--translate</c> was not given: the whole pass is behind that flag.</summary>
    private static TranslationOptions? BuildTranslationOptions(ParsedCommandLine parsed)
    {
        if (!parsed.HasFlag("translate"))
        {
            if (parsed.Value("context-segments") is { Length: > 0 })
            {
                throw new CliUsageException("--context-segments only means something with --translate.");
            }

            return null;
        }

        var contextSegments = 0;
        if (parsed.Value("context-segments") is { Length: > 0 } text)
        {
            if (!CommandLineParser.TryParseInt(text, out var value) || value < 0)
            {
                throw new CliUsageException($"--context-segments needs a non-negative integer, got '{text}'.");
            }

            contextSegments = value;
        }

        var options = new TranslationOptions { ContextSegments = contextSegments };
        options.Validate();
        return options;
    }

    /// <summary>
    /// Says that <c>--language</c> did not reach the translator, when both were given.
    /// </summary>
    /// <remarks>
    /// The two flags are one letter apart in a help listing and a mile apart in what they do, and
    /// the plausible misreading — that <c>--language en</c> asks for English out — is exactly the
    /// one that would leave somebody waiting for a translation that was never requested. It is a
    /// hint to the speech model about what it is listening to; it reaches the ABI, no catalogue
    /// model applies it, and no translator ever sees it.
    /// </remarks>
    private static void WarnAboutLanguageHint(CliContext context, ParsedCommandLine parsed)
    {
        if (parsed.Value("language") is not { Length: > 0 } hint)
        {
            return;
        }

        context.WriteError(
            $"--language {hint} is a hint to the speech model about the audio, not a translation target: the " +
            "translator is many-to-one into English and is never told what it is reading. --translate is what asks " +
            "for English.");
    }

    /// <summary>
    /// The real diariser, or the canned one under <c>--fake</c>.
    /// </summary>
    /// <remarks>
    /// <c>--fake</c> keeps meaning what it always meant here: canned everything, no weights, so the
    /// opt-in stays exercisable end to end on a machine with nothing installed. Without it,
    /// <c>--speakers</c> now loads the real model, and the second read of the audio it costs is a
    /// second decode as well as a second inference.
    /// </remarks>
    private static Task<ISpeakerLabeller> CreateLabellerAsync(
        CliContext context, ParsedCommandLine parsed, SpeakerLabellingOptions options, CancellationToken ct) =>
        LabellerFactory.CreateAsync(
            context,
            new LabellerRequest
            {
                Fake = parsed.HasFlag("fake"),
                ModelId = parsed.Value("speaker-model"),
                ModelPath = parsed.Value("speaker-model-path"),
                Threads = ParseThreads(parsed.Value("speaker-threads"), "--speaker-threads"),
                Backend = parsed.Value("speaker-backend"),
                AllowUnverifiedBackend = parsed.HasFlag("speaker-backend-unverified"),
            },
            options,
            ct);

    /// <summary>
    /// Parses a thread count. <paramref name="option"/> is the flag the caller's own command spells
    /// it with — <c>transcribe</c> has <c>--speaker-threads</c> and <c>diarise</c> has
    /// <c>--threads</c>, and a shared message that names one of them tells half its users to fix a
    /// flag their command does not have.
    /// </summary>
    internal static int ParseThreads(string? value, string option)
    {
        if (value is not { Length: > 0 })
        {
            return 0;
        }

        if (!CommandLineParser.TryParseInt(value, out var threads) || threads < 0)
        {
            throw new CliUsageException($"{option} needs a non-negative integer, got '{value}'.");
        }

        return threads;
    }

    /// <summary>
    /// Loads the engine and describes the outcome when the backend that came back is not the
    /// backend that was asked for. Null when they agree, which is every ordinary run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in this command reported the loaded backend at all until 2026-08-20, which was
    /// survivable while a bare <c>--backend</c> always meant Vulkan: the fallback from there is
    /// CPU, and the user had typed nothing to be contradicted. It stopped being survivable when the
    /// default became "the fastest tier on disk". A machine carrying the CUDA drop with no working
    /// driver behind it now silently resolves to CUDA, fails, and lands on CPU — twelve times
    /// slower than the CUDA it was reaching for and seven times slower than the Vulkan it skipped,
    /// with no line of output to say why a job that used to take four minutes took fifty.
    /// </para>
    /// <para>
    /// It names Vulkan when CUDA falls through because the loader's chain is CUDA then CPU and
    /// never CUDA then Vulkan — deliberately, so that a deliberate CUDA request fails loudly — and
    /// a reader who did not choose CUDA needs telling that the middle tier is still there and how
    /// to ask for it. On stderr, with the other notices, so a piped transcript is unaffected.
    /// </para>
    /// <para>
    /// The load is not incidental to the check, it is the check. A parakeet.cpp engine reports the
    /// backend it was <em>asked</em> for until <c>LoadAsync</c> rewrites the capability from what
    /// the native loader resolved, so a comparison made against a freshly constructed engine
    /// compares the request against itself and can never fire. It was written that way on
    /// 2026-08-20 and was dead until 2026-08-21 — the failure above, going unreported by the line
    /// written to report it.
    /// </para>
    /// </remarks>
    internal static async ValueTask<string?> LoadAndDescribeBackendAsync(
        ITranscriptionEngine engine, ComputeBackend requested, bool wasNamed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(engine);

        await engine.LoadAsync(ct).ConfigureAwait(false);
        return DescribeBackendFallback(requested, engine.Capabilities.Backend, wasNamed);
    }

    /// <summary>
    /// The message, given the two backends and whether the user named one. Split from the load so
    /// that the wording is checkable without a model on disk, which is all CI has.
    /// </summary>
    internal static string? DescribeBackendFallback(
        ComputeBackend requested, ComputeBackend loaded, bool wasNamed)
    {
        if (loaded == requested)
        {
            return null;
        }

        static string Name(ComputeBackend backend) => backend.ToString().ToLowerInvariant();

        var how = wasNamed
            ? $"{Name(requested)} was requested"
            : $"{Name(requested)} was chosen automatically, because this build carries its binaries,";

        var suggestion = requested == ComputeBackend.Cuda && loaded != ComputeBackend.Vulkan
            ? "  Vulkan is not tried after CUDA; pass --backend vulkan for the other GPU tier."
            : string.Empty;

        return $"{how} but the native loader fell back to {Name(loaded)}.{suggestion}";
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
        Func<CancellationToken, ValueTask> ensureEngineLoaded,
        ISpeakerLabeller? labeller,
        ITranscriptTranslator? translator,
        TranscriptionJob job,
        TranscriptionOptions options,
        SpeakerLabellingOptions? speakerOptions,
        TranslationOptions? translationOptions,
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

        // After the file is known to be there and before its audio is opened: a queue of names that
        // are all typos should not pay for a model load, and a fallback to CPU has to be on screen
        // before the decode it explains rather than after it. Idempotent — only the first file
        // through here loads anything.
        await ensureEngineLoaded(ct).ConfigureAwait(false);

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

            // Before the labeller decodes a sample, and it is the one warning here that is about
            // the file rather than about the request: past where this labeller's output has been
            // established, the speaker labels are a guess and nothing after the run will say so.
            if (SpeakerLabelling.DescribeDurationRisk(labeller.Capabilities, second.Duration) is { } longRun)
            {
                context.WriteError($"WARNING: {job.InputPath}: {longRun}");
            }

            document = await SpeakerLabelling.LabelAsync(
                document, labeller, second, speakerOptions, progress, ct).ConfigureAwait(false);
            speakerWarning = SpeakerLabelling.DescribeLimit(labeller, document);

            // A merge the user's own --speaker-count asked for is still a merge, and the seconds
            // beside each one are its evidence: near zero is one voice that drifted onto a second
            // label, a large number is two people the count has just put under one name. Read off
            // the document, which is also where the saved transcript's copy comes from, so the line
            // printed here and the record archived beside it cannot disagree.
            foreach (var merge in document.SpeakerFolds)
            {
                context.WriteError($"{job.InputPath}: merged {merge.Describe()}.");
            }
        }

        // Last, and after the speakers on purpose: SpeakerAssignment attributes a speaker per word
        // and cuts segments where the speaker changes, and a translated segment has no words. Run
        // the other way round it would fall back to "whoever talks most across the span" on every
        // segment — a coarser label, arrived at silently.
        //
        // The transcript as the engine wrote it is kept: the anomalies reported below are about
        // what was heard, and translation destroys both signals they rest on — a translated segment
        // has no word confidences, and a stretch the model emitted in another script comes back as
        // English prose. Reading them off the translation would quietly stop reporting either.
        var transcribed = document;
        string? numeralWarning = null;
        if (translator is not null && translationOptions is not null)
        {
            document = await TranscriptTranslation.TranslateAsync(
                document, translator, translationOptions, progress, ct).ConfigureAwait(false);

            // Dates and figures are what a listener checks a transcript for, and they are where a
            // two-model cascade meets worst. Compared against the transcript as the engine wrote
            // it, which is what `transcribed` is being kept for, so the comparison is against what
            // was heard rather than against a second reading of the English.
            numeralWarning = TranslationNumerals.Describe(transcribed.Segments, document.Segments);
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
            // cap is said after either, because it is about the names and not the words. A number
            // the English lost is said last, because it is about one segment rather than the file.
            Warning = Join(
                Join(DescribeSilence(engine, transcribed) ?? DescribeAnomalies(transcribed, options), speakerWarning),
                numeralWarning),
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
    /// <remarks>
    /// Internal rather than private so a test can hold the property the caller depends on: this
    /// reads the transcript as the engine wrote it, and translation destroys both signals it rests
    /// on. No CLI invocation can reach the difference — the canned engine writes Latin script at
    /// confidences well above the threshold, and the threshold is not a flag — so the two documents
    /// are handed to it directly instead.
    /// </remarks>
    internal static string? DescribeAnomalies(TranscriptDocument document, TranscriptionOptions options)
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
