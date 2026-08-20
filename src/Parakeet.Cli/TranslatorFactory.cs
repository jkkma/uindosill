using Parakeet.Core.Formatting;
using Parakeet.Core.Models;
using Parakeet.Core.Translation;
using Parakeet.Engine.Marian;

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

    public static ITranscriptTranslator Create(CliContext context, TranslatorRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Fake)
        {
            context.WriteError("Using the canned translator: the pass is real, the English is not.");
            return new FakeTranscriptTranslator();
        }

        var (directory, descriptor) = ResolveModel(context, request);

        return new MarianTranscriptTranslator(new MarianTranslatorOptions
        {
            ModelDirectory = directory,
            ModelId = descriptor?.Id ?? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            IntraOpThreads = request.Threads,
            SourceLanguages = descriptor?.Languages ?? [],
        });
    }

    /// <summary>
    /// Checks that a translator can be had, without building one — so <c>transcribe</c> refuses
    /// before it loads 1.34 GiB of ASR weights and decodes a file whose translation it cannot write.
    /// </summary>
    public static void Resolve(CliContext context, TranslatorRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Fake)
        {
            return;
        }

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

        if (!capabilities.PreservesWordTimings
            && formats.Contains(TranscriptFormats.WordTimedVtt.Id, StringComparer.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                $"-f {TranscriptFormats.WordTimedVtt.Id} times every word, and translation does not carry word " +
                "timings: the English words are not the words that were spoken and nothing aligns them. It is " +
                "refused rather than written with the old timings on the new text. Drop the format, or drop " +
                "--translate and get the word timings of what was actually said.");
        }
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
