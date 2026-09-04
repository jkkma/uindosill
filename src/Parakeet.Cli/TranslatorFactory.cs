using Parakeet.Core.Formatting;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;
using Parakeet.Engine.Python;

namespace Parakeet.Cli;

internal sealed record TranslatorRequest
{
    /// <summary>Use the canned translator: real pass, no weights, visibly not English.</summary>
    public bool Fake { get; init; }

    /// <summary>
    /// Catalogue id of a translation entry, or null to choose one: by the recogniser's languages
    /// when <see cref="RecogniserLanguages"/> is given, and otherwise the only one installed.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>A checkpoint directory to load directly instead of a catalogue entry.</summary>
    public string? ModelPath { get; init; }

    /// <summary>
    /// The languages the recogniser whose transcript is being translated writes, when there is one
    /// — <c>transcribe --translate</c> — and null for text with no recogniser behind it.
    /// </summary>
    /// <remarks>
    /// Since 2026-09-04 the catalogue holds two translation entries, one per recogniser: the
    /// many-to-one checkpoint reads the European recogniser's 25 languages and the Japanese one
    /// reads Japanese. Which one runs is decided by which recogniser wrote the transcript, because
    /// that is the one fact the pipeline has about the language of what it is translating — the
    /// transcript's own language field records a request, not a detection. An empty list is a
    /// recogniser that does not say, which is a sideloaded one, and it is refused rather than
    /// guessed for.
    /// </remarks>
    public IReadOnlyList<string>? RecogniserLanguages { get; init; }

    /// <summary>The recogniser's id, for the messages that name it. Null with <see cref="RecogniserLanguages"/>.</summary>
    public string? RecogniserId { get; init; }

    /// <summary>Intra-op threads for the ONNX sessions, or 0 to let ONNX Runtime choose.</summary>
    public int Threads { get; init; }

    /// <summary>
    /// Execution provider, or null for <c>auto</c> — resolved inside the sidecar, because only the
    /// ONNX Runtime that would have to initialise a provider knows whether it will.
    /// </summary>
    public string? Backend { get; init; }

    /// <summary>Allow a backend this project has not measured as faithful.</summary>
    public bool AllowUnverifiedBackend { get; init; }

    /// <summary>
    /// The flag the calling command spells the backend with — <c>transcribe</c> has
    /// <c>--translate-backend</c> and <c>translate</c> has <c>--backend</c>.
    /// </summary>
    /// <remarks>
    /// Carried rather than hardcoded, on <c>LabellerRequest</c>'s terms: a shared message that names
    /// one command's flag tells half its readers to fix a flag their command does not have.
    /// </remarks>
    public string BackendOption { get; init; } = "--translate-backend";
}

/// <summary>
/// Resolves the translator behind <c>--translate</c> and behind <c>uindosill translate</c>.
/// </summary>
/// <remarks>
/// The shape is <c>LabellerFactory</c>'s, and for the same reason: resolution has several ways to
/// fail — no translation entry in the catalogue, an entry that is not installed, a directory that
/// is not a checkpoint — and each wants a message saying which one happened. Two copies of that
/// become two different messages.
/// </remarks>
internal static class TranslatorFactory
{
    /// <summary>
    /// The nine files a translation entry installs, of which these eight must be present — the
    /// sidecar's own list (<c>translator/engine.py</c>'s <c>REQUIRED_FILES</c>), which is the
    /// authority: without <c>generation_config.json</c> the decode loads and silently loses its
    /// <c>bad_words_ids</c>, so a host list that did not name it let a checkpoint through that the
    /// sidecar then refused, until 2026-08-22.
    /// </summary>
    private static readonly string[] RequiredFiles =
    [
        "encoder_model.onnx", "decoder_model_merged.onnx", "config.json", "generation_config.json",
        "source.spm", "target.spm", "vocab.json", "tokenizer_config.json",
    ];

    /// <summary>
    /// Builds the translator and loads it, so that its capabilities are real before anything is
    /// said about them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Loading here rather than lazily, and asynchronous for that reason, exactly as
    /// <c>LabellerFactory.CreateAsync</c> is. With the engine out of process the provider is chosen
    /// inside the sidecar — only ONNX Runtime knows what will initialise — so the answer has to come
    /// back before it can be reported, and it is the load that runs the parity check whose warning
    /// belongs in front of a run rather than after it.
    /// </para>
    /// <para>
    /// <b>What it costs is 1.34 GiB held through the decode.</b> In <c>transcribe --translate</c> the
    /// translator now loads before the ASR does, and its graphs sit in the sidecar unused until the
    /// last pass. That is the price of finding out here that the bundled Python will not start, that
    /// the checkpoint is unreadable, or that the provider does not reproduce the reference — none of
    /// which <see cref="Resolve"/> can discover from the file system, and all of which would
    /// otherwise be discovered after a three-hour decode.
    /// </para>
    /// </remarks>
    public static async Task<ITranscriptTranslator> CreateAsync(
        CliContext context,
        TranslatorRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Fake)
        {
            context.WriteError("Using the canned translator: the pass is real, the English is not.");
            return new FakeTranscriptTranslator();
        }

        var (directory, descriptor) = ResolveModel(context, request);

        ITranscriptTranslator translator = new SidecarTranscriptTranslator(new SidecarTranslatorOptions
        {
            ModelDirectory = directory,
            ModelId = descriptor?.Id ?? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            IntraOpThreads = request.Threads,
            Provider = ResolveBackend(request),
            SourceLanguages = descriptor?.Languages ?? [],

            // What the catalogue says the checkpoint reads in front of a source, held against what
            // the sidecar finds in the vocabulary at load. A bare directory declares nothing, and
            // the sidecar's answer then stands alone.
            DeclaredTargetToken = descriptor?.TargetToken,
            HasDeclaredTargetToken = descriptor is not null,
        });

        // Disposed on a failed load rather than leaked: the sidecar is a process, and one left
        // running per failure is one process per file in a batch.
        try
        {
            await translator.LoadAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await translator.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Which provider ran changes the English and not only the clock. Nothing is said for cpu,
        // webgpu or cuda, and that silence is a measurement rather than an oversight: measured
        // 2026-08-21 on 32 FLEURS sentences at beam 6, webgpu returned the CPU's own translations on
        // 32 of 32 and cuda on 240 of 240. DirectML is the one worth a line.
        if (translator.Capabilities.Backend == ComputeBackend.DirectMl)
        {
            context.WriteError(
                "WARNING: translating on DirectML, which this project has not measured as faithful. On 32 FLEURS " +
                "sentences at beam 6 it agreed with the CPU on 0 of 32: the decoder falls into a repetition loop" +
                ", at 21.5x slower. Treat this English as unverified.");
        }

        // `auto` tried something better first and it did not build — said once, with the reason,
        // for the same reason LabellerFactory says it: a translation at CPU speed on a machine with
        // a GPU is a question until somebody reads why.
        if (translator is SidecarTranscriptTranslator { FellBackFrom.Count: > 0 } sidecar)
        {
            context.WriteError(
                $"{request.BackendOption} auto passed over {string.Join("; ", sidecar.FellBackFrom)}: this run is on " +
                $"{translator.Capabilities.Backend.ToString().ToLowerInvariant()}.");
        }

        ReportParity(context, translator, request.BackendOption);
        return translator;
    }

    /// <summary>
    /// Which execution provider the translator is allowed to use, from what the user asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A whitelist rather than a passthrough, for the reason <c>LabellerFactory</c>'s is one and
    /// with a different measurement behind it. On 2026-08-21, 32 FLEURS es_419 sentences at beam 6:
    /// <c>webgpu</c> returned the CPU's own translations on <b>32 of 32</b> at 1.30x the speed and
    /// <c>cuda</c> on <b>240 of 240</b>, while <c>dml</c> matched on <b>0 of 32</b> — the decoder
    /// falls into a repetition loop — at 21.5x <i>slower</i>.
    /// </para>
    /// <para>
    /// DirectML's failure here is not the diariser's and does not have the diariser's fix. There the
    /// graph optimiser fuses the model into one wrong node and <c>ORT_DISABLE_ALL</c> rescues it;
    /// here the encoder and the decoder are each clean at full optimisation when driven directly, so
    /// the fault is in <c>optimum</c>'s merged KV-cache path and no optimisation level moves it. It
    /// stays reachable behind the unverified flag so that measuring it stays possible.
    /// </para>
    /// </remarks>
    private static string ResolveBackend(TranslatorRequest request)
    {
        if (request.Backend is not { Length: > 0 } asked)
        {
            return "auto";
        }

        var backend = asked.Trim().ToLowerInvariant();
        return backend switch
        {
            "auto" or "cpu" or "cuda" or "webgpu" => backend,
            "dml" or "directml" when request.AllowUnverifiedBackend => "dml",
            "dml" or "directml" => throw new CliUsageException(
                $"{request.BackendOption} dml is refused. Measured on 32 FLEURS sentences at beam 6, DirectML " +
                "agreed with the CPU on 0 of 32 translations, its decoder falls into a repetition loop, while " +
                $"running 21.5x slower, so it is neither faithful nor fast. Add {request.BackendOption}-unverified " +
                $"to measure it anyway, or use {request.BackendOption} webgpu, which returned the CPU's own " +
                "translations on 32 of 32 at 1.30x the speed."),
            _ => throw new CliUsageException(
                $"Unknown translator backend '{asked}' for {request.BackendOption}. Choose cpu, cuda, webgpu or dml."),
        };
    }

    /// <summary>
    /// Checks that a translator can be had, without building one — so <c>transcribe</c> refuses
    /// before it loads 1.34 GiB of ASR weights and decodes a file whose translation it cannot write.
    /// </summary>
    public static void Resolve(CliContext context, TranslatorRequest request, IReadOnlyList<string>? formats = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        // The word-timed format is refused here rather than only after the load, because whether it
        // is refused does not depend on anything the load discovers. Leaving it to Check alone meant
        // `--translate -f vtt-words` started a Python, loaded 1.34 GiB of graphs and ran a parity
        // fixture before refusing on a combination of two flags — a usage error reported at the cost
        // of a model load.
        if (formats is not null)
        {
            RefuseWordTimings(formats);
        }

        if (request.Fake)
        {
            return;
        }

        // The backend too, and not only the model. A refused provider name is a usage error, and a
        // usage error discovered after a three-hour decode is a usage error reported too late.
        ResolveBackend(request);
        ResolveModel(context, request);
    }

    public static (string Directory, ModelDescriptor? Descriptor) ResolveModel(CliContext context, TranslatorRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.ModelPath is { Length: > 0 } explicitPath)
        {
            if (!Directory.Exists(explicitPath))
            {
                throw new CliUsageException(
                    $"Translation model directory not found: {explicitPath}. --translate-model-path takes the " +
                    "exported checkpoint directory, not one file inside it.");
            }

            RequireFiles(explicitPath, explicitPath);
            return (explicitPath, null);
        }

        ModelDescriptor descriptor;
        if (request.ModelId is { Length: > 0 } id)
        {
            if (!context.Catalog.TryGet(id, out var found))
            {
                throw new CliUsageException(
                    $"Unknown model '{id}'. Run 'uindosill models list' to see the catalogue.");
            }

            if (found.Task != ModelTask.Translation)
            {
                throw new CliUsageException(
                    $"'{found.Id}' is a {found.Task.ToString().ToLowerInvariant()} model, not a translation model; " +
                    "it cannot write English. Run 'uindosill models list' to see which entries do.");
            }

            descriptor = found;
        }
        else
        {
            descriptor = Choose(context, request);
        }

        var path = context.Store.PathFor(descriptor);
        if (!context.Store.IsInstalled(descriptor))
        {
            throw new CliUsageException(
                $"The translation model '{descriptor.Id}' is not installed. Run " +
                $"'uindosill models download {descriptor.Id}' first (it would be at {path}).");
        }

        RequireFiles(path, descriptor.Id);
        return (path, descriptor);
    }

    /// <summary>
    /// Which translation entry runs when none was named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One entry is the answer whoever asks, which is how the catalogue stood until 2026-09-04.
    /// With more than one, the question is what language the text is in, and the pipeline knows
    /// that in exactly one case: <c>transcribe --translate</c>, where the recogniser that wrote the
    /// transcript declares what it writes. The entry that reads every language it declares is the
    /// one — <see cref="ModelCatalog.TranslationModelsFor"/> — and a recogniser that declares
    /// nothing, which is a sideloaded one, is refused rather than guessed for.
    /// </para>
    /// <para>
    /// A text file says nothing about its language, so <c>translate</c> falls back to the only
    /// entry installed, and with two installed it asks for <c>--model</c> — the same shape as the
    /// pre-2026-09-04 refusal, reached one step later.
    /// </para>
    /// </remarks>
    private static ModelDescriptor Choose(CliContext context, TranslatorRequest request)
    {
        var candidates = context.Catalog.TranslationModels;
        if (candidates.Count == 0)
        {
            throw new CliUsageException("The model catalogue has no translation model.");
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var listed = string.Join(", ", candidates.Select(m => $"{m.Id} ({string.Join(" ", m.Languages)})"));

        if (request.RecogniserLanguages is { } languages)
        {
            var recogniser = request.RecogniserId ?? "the speech model";
            if (languages.Count == 0)
            {
                throw new CliUsageException(
                    $"The catalogue has {candidates.Count} translation models and {recogniser} does not say which " +
                    $"languages it writes, so none can be chosen for it. Name one with {ModelOption(request)}. " +
                    $"Candidates: {listed}.");
            }

            var matching = context.Catalog.TranslationModelsFor(languages);
            return matching.Count switch
            {
                1 => matching[0],
                0 => throw new CliUsageException(
                    $"No translation model reads what {recogniser} writes ({string.Join(" ", languages)}). " +
                    $"Candidates: {listed}."),
                _ => throw new CliUsageException(
                    $"{matching.Count} translation models read what {recogniser} writes; name one with " +
                    $"{ModelOption(request)}. Candidates: {string.Join(", ", matching.Select(m => m.Id))}."),
            };
        }

        var installed = candidates.Where(context.Store.IsInstalled).ToList();
        return installed.Count switch
        {
            1 => installed[0],
            0 => throw new CliUsageException(
                $"A translation model is not installed: the catalogue has {candidates.Count}, and none of them is. " +
                "Run 'uindosill models download <id>' for the one that reads your text's language. " +
                $"Candidates: {listed}."),
            _ => throw new CliUsageException(
                $"{installed.Count} translation models are installed, and a text file does not say which language " +
                $"it is in; name one with {ModelOption(request)}. Installed: " +
                $"{string.Join(", ", installed.Select(m => $"{m.Id} ({string.Join(" ", m.Languages)})"))}."),
        };
    }

    /// <summary>The flag that names a translation model, spelled for whichever command is asking.</summary>
    private static string ModelOption(TranslatorRequest request) =>
        request.BackendOption == "--backend" ? "--model" : "--translate-model";

    /// <summary>
    /// What a caller must be told before a pass runs, given what the translator turned out to be.
    /// </summary>
    /// <remarks>
    /// The capabilities are the translator's to declare and the caller's to honour, the way
    /// <c>LabellerFactory</c> says out loud that a speaker count was ignored rather than dropping
    /// it. Three of them bind here. A translator that needs to be told the source language cannot
    /// be driven at all, because nothing in this pipeline detects one. One that does not preserve
    /// word timings — which is every translator this product can ship — makes the word-timed
    /// subtitle format meaningless, so that format is refused rather than written against times
    /// that no longer belong to the words. And one that does not read the context option is said to
    /// have ignored it, because a lever that silently does nothing is worse than no lever.
    /// </remarks>
    public static void Check(ITranscriptTranslator translator, IReadOnlyList<string> formats, TranslationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(formats);

        var capabilities = translator.Capabilities;

        if (capabilities.RequiresSourceLanguage)
        {
            throw new CliUsageException(
                $"The translator '{capabilities.EngineName}' has to be told which language it is reading, and " +
                "nothing here detects one: --language is a hint for the ASR model and is inert on this checkpoint. " +
                "Only a many-to-one translator can run this pass.");
        }

        if (!capabilities.PreservesWordTimings)
        {
            RefuseWordTimings(formats);
        }
    }

    /// <summary>
    /// Refuses the word-timed subtitle format under a translation pass.
    /// </summary>
    /// <remarks>
    /// Shared by the pre-flight and the post-load check so the two cannot come to differ. The
    /// pre-flight can make this call without a translator because the answer is a property of
    /// translation rather than of a checkpoint — no translator this product can ship preserves word
    /// timings — and <see cref="Check"/> makes it again because one that somehow did should not be
    /// refused.
    /// </remarks>
    private static void RefuseWordTimings(IReadOnlyList<string> formats)
    {
        if (!formats.Contains(TranscriptFormats.WordTimedVtt.Id, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        throw new CliUsageException(
            $"-f {TranscriptFormats.WordTimedVtt.Id} times every word, and translation does not carry word " +
            "timings: the English words are not the words that were spoken and nothing aligns them. It is " +
            "refused rather than written with the old timings on the new text. Drop the format, or drop " +
            "--translate and get the word timings of what was actually said.");
    }

    /// <summary>
    /// Says so when this machine's translator does not reproduce the committed reference.
    /// </summary>
    /// <remarks>
    /// The sibling of <c>LabellerFactory</c>'s parity line, and it exists for the same reason: a
    /// provider measured faithful elsewhere is a prior, and what happened here is the evidence. What
    /// it catches is a translation that is wrong in a way nothing about it reveals — measured
    /// 2026-08-21, DirectML returned the CPU's translation on 0 of 32 FLEURS sentences while every
    /// one of them came back as an ordinary-looking sentence.
    /// </remarks>
    public static void ReportParity(CliContext context, ITranscriptTranslator translator, string backendOption)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(translator);

        // Three failing shapes — a count short of the total, a reason given instead of one, and a
        // check that could not run, which until 2026-08-22 was reported as nothing at all — and the
        // result describes its own; what is added here is what the English is and the remedy.
        if (translator is not SidecarTranscriptTranslator { Parity: { } parity } || parity.Describe() is not { } finding)
        {
            return;
        }

        context.WriteError(
            finding + " The English below is this machine's own result and no figure published by this project " +
            $"describes it. {backendOption} cpu is the one that does.");
    }

    /// <summary>The context report, which needs the options and so cannot live in <see cref="Check"/>'s caller.</summary>
    public static void ReportIgnoredContext(CliContext context, ITranscriptTranslator translator, TranslationOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(options);

        if (options.ContextSegments > 0 && !translator.Capabilities.HonoursContext)
        {
            context.WriteError(
                $"--context-segments {options.ContextSegments} was given and this translator decodes each segment " +
                "on its own; the value is ignored. The recommended checkpoint is a sentence-level model with no " +
                "way to mark which of its input is context, and every published figure for it is a no-context " +
                "figure.");
        }
    }

    private static void RequireFiles(string directory, string what)
    {
        var missing = RequiredFiles.Where(file => !File.Exists(Path.Combine(directory, file))).ToList();
        if (missing.Count > 0)
        {
            throw new CliUsageException(
                $"'{what}' is not a complete translation checkpoint: {string.Join(", ", missing)} " +
                $"{(missing.Count == 1 ? "is" : "are")} missing from {directory}. Eight of the checkpoint's nine files " +
                "are required: two graphs, two configs and four of the five tokenizer files. A partial set " +
                "loads until it does not.");
        }
    }
}
