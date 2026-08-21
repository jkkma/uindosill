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

    /// <summary>Catalogue id of a translation entry, or null for the only one installed.</summary>
    public string? ModelId { get; init; }

    /// <summary>A checkpoint directory to load directly instead of a catalogue entry.</summary>
    public string? ModelPath { get; init; }

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
    /// <summary>The nine files a translation entry installs, of which these must all be present.</summary>
    private static readonly string[] RequiredFiles =
    [
        "encoder_model.onnx", "decoder_model_merged.onnx", "config.json",
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
                "sentences at beam 6 it agreed with the CPU on 0 of 32 — the decoder falls into a repetition loop " +
                "— at 21.5x slower. Treat this English as unverified.");
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
                "agreed with the CPU on 0 of 32 translations — its decoder falls into a repetition loop — while " +
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
            descriptor = context.Catalog.TranslationModels.Count switch
            {
                0 => throw new CliUsageException("The model catalogue has no translation model."),
                1 => context.Catalog.TranslationModels[0],
                _ => throw new CliUsageException(
                    "The catalogue has more than one translation model; name one. Candidates: " +
                    string.Join(", ", context.Catalog.TranslationModels.Select(m => m.Id))),
            };
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

        if (translator is not SidecarTranscriptTranslator { Parity: { } parity } || parity.Passed)
        {
            return;
        }

        var examples = parity.Differing.Count > 0
            ? " " + string.Join(" ", parity.Differing)
            : string.Empty;

        context.WriteError(
            $"WARNING: this machine's translator reproduced {parity.Identical} of {parity.Total} of the " +
            $"reference's translations.{examples} The English below is this machine's own result and no figure " +
            $"published by this project describes it. {backendOption} cpu is the one that does.");
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
                $"{(missing.Count == 1 ? "is" : "are")} missing from {directory}. The route is nine files — two " +
                "graphs, two configs and a five-file tokenizer — and a partial set loads until it does not.");
        }
    }
}
