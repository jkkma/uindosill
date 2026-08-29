using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;

namespace Parakeet.Core.Models;

/// <summary>
/// The set of models the app knows how to fetch. Loaded from a JSON manifest rather than
/// written in code so that pinning a digest is a data change a release engineer can make and
/// review, not a code change.
/// </summary>
public sealed class ModelCatalog
{
    private readonly Dictionary<string, ModelDescriptor> _byId;
    private readonly Dictionary<ModelTask, IReadOnlyList<ModelDescriptor>> _byTask;
    private readonly Dictionary<string, IReadOnlyList<ModelDescriptor>> _byDeclaredFileName;

    private ModelCatalog(IReadOnlyList<ModelDescriptor> models, IReadOnlyList<DeferredModelPin> deferred)
    {
        Models = models;
        Deferred = deferred;
        _byId = models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        _byTask = Enum.GetValues<ModelTask>().ToDictionary(
            task => task,
            task => (IReadOnlyList<ModelDescriptor>)[.. models.Where(m => m.Task == task)]);

        // OrdinalIgnoreCase throughout: case is not a distinction a Windows file name makes, and
        // an index built without the comparer would miss the same file spelled differently.
        _byDeclaredFileName = models
            .Where(model => model.IsMultiFile)
            .SelectMany(model => model.Files
                .Where(file => !file.FileName.Contains('/', StringComparison.Ordinal))
                .Select(file => (file.FileName, Model: model)))
            .GroupBy(pair => pair.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ModelDescriptor>)[.. group.Select(pair => pair.Model)],
                StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Lazy<ModelCatalog> DefaultCatalog = new(LoadEmbedded, isThreadSafe: true);

    /// <summary>The catalogue shipped with the application.</summary>
    public static ModelCatalog Default => DefaultCatalog.Value;

    /// <summary>The models this build can select and install. Lookup by id searches only these.</summary>
    public IReadOnlyList<ModelDescriptor> Models { get; }

    /// <summary>
    /// Digests recorded for a later version, deliberately unreachable from <see cref="Models"/>,
    /// <see cref="TryGet"/> and <see cref="Get"/>. See <see cref="DeferredModelPin"/> for why
    /// these are a separate type rather than catalogue entries with a flag: a descriptor asserts a
    /// licence, and for these files no licence has been established.
    /// </summary>
    public IReadOnlyList<DeferredModelPin> Deferred { get; }

    /// <summary>
    /// The transcription model an unspecified <c>--model</c> resolves to. Only ever a
    /// <see cref="ModelTask.Transcription"/> entry: a diarisation model marked recommended, or
    /// listed first, must not become the default ASR model by falling through this.
    /// </summary>
    public ModelDescriptor? Recommended =>
        TranscriptionModels.FirstOrDefault(m => m.Recommended) ?? TranscriptionModels.FirstOrDefault();

    /// <summary>The entries <c>transcribe</c> may load.</summary>
    public IReadOnlyList<ModelDescriptor> TranscriptionModels => _byTask[ModelTask.Transcription];

    /// <summary>The entries the speaker-labelling opt-in may load. Empty until a model is integrated.</summary>
    public IReadOnlyList<ModelDescriptor> DiarisationModels => _byTask[ModelTask.Diarisation];

    /// <summary>The entries the translation opt-in may load. Empty until a model is integrated.</summary>
    public IReadOnlyList<ModelDescriptor> TranslationModels => _byTask[ModelTask.Translation];

    /// <summary>The entries the neural speech-detection opt-in may load in place of the energy gate.</summary>
    public IReadOnlyList<ModelDescriptor> VoiceActivityModels => _byTask[ModelTask.VoiceActivity];

    /// <summary>The entries the Ask tab may load to answer questions about a transcript.</summary>
    public IReadOnlyList<ModelDescriptor> AnsweringModels => _byTask[ModelTask.Answering];

    /// <summary>
    /// The answering model the Ask panel serves when nobody has chosen one. Only ever a
    /// <see cref="ModelTask.Answering"/> entry, for the same reason <see cref="Recommended"/> is
    /// only ever a transcription one: the flag is read per task, so an entry marked recommended
    /// for one job cannot become the default for another.
    /// </summary>
    /// <remarks>
    /// Null when no answering entry is marked, which is a meaningful state rather than a broken
    /// one — the panel then falls back to the largest file on disk, exactly as it did before any
    /// entry claimed the position. Nothing here says the file is installed; that is a question
    /// about a folder, and the caller owns it.
    /// </remarks>
    public ModelDescriptor? RecommendedAnswering =>
        AnsweringModels.FirstOrDefault(m => m.Recommended);

    /// <summary>
    /// The multi-file entries that declare a file of this name directly inside their own
    /// directory — the entries a bare file of that name in the store root would belong to, if
    /// only it were one level down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Multi-file entries only, and that is the whole point of it.</b> A single-file entry's
    /// file lives in the root by design, so a file matching one is installed rather than
    /// misplaced and <see cref="LocalModelStore.ListInstalled(ModelCatalog)"/> already pairs the
    /// two. A multi-file entry's files are only ever looked for under its directory, so a copy
    /// sitting in the root matches nothing, and the tab called it a file "no entry accounts for"
    /// while the entry above it read Not installed — one model, described twice, contradictorily,
    /// with a Delete button under one description and a Download button under the other.
    /// </para>
    /// <para>
    /// A list rather than one entry, because a name can be claimed twice: the two 26B answering
    /// entries ship the same drafting head under the same name. That collision is also why a
    /// multi-file entry gets a directory in the first place, so this cannot be flattened by
    /// letting such files count as installed where they lie.
    /// </para>
    /// <para>
    /// Files whose manifest name carries a subpath are left out. They are declared to live one
    /// level below the entry's own directory, so no bare name in the root is ever the file they
    /// describe.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ModelDescriptor> EntriesDeclaringFile(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return _byDeclaredFileName.TryGetValue(fileName, out var models) ? models : [];
    }

    public bool TryGet(string id, [NotNullWhen(true)] out ModelDescriptor? model) =>
        _byId.TryGetValue(id, out model);

    public ModelDescriptor Get(string id) =>
        TryGet(id, out var model)
            ? model
            : throw new KeyNotFoundException(
                $"Unknown model '{id}'. Known models: {string.Join(", ", Models.Select(m => m.Id))}.");

    public static ModelCatalog Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Model manifest has no 'models' array.");
        }

        var parsed = new List<ModelDescriptor>();
        foreach (var element in models.EnumerateArray())
        {
            parsed.Add(ParseModel(element));
        }

        var duplicate = parsed.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Model manifest contains duplicate id '{duplicate.Key}'.");
        }

        // Two entries that occupy the same name in the store root are one entry as far as the disk
        // is concerned: installing the second overwrites the first, and removing either takes both.
        // The check is over the storage name rather than the file name so it also catches a
        // directory colliding with a file — the two share one namespace, and on Windows they share
        // it case-insensitively.
        var collidingName = parsed
            .GroupBy(m => m.StorageName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (collidingName is not null)
        {
            throw new InvalidDataException(
                $"Model manifest has more than one entry stored as '{collidingName.Key}': " +
                $"{string.Join(", ", collidingName.Select(m => m.Id))}.");
        }

        var deferred = new List<DeferredModelPin>();
        if (root.TryGetProperty("deferred", out var deferredElement))
        {
            if (deferredElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Model manifest's 'deferred' must be an array.");
            }

            foreach (var element in deferredElement.EnumerateArray())
            {
                deferred.Add(ParseDeferred(element));
            }
        }

        // An id in both places would make the same string mean an installable model in one code
        // path and an unlicensed pin in another.
        var ids = parsed.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var collision = deferred.FirstOrDefault(d => ids.Contains(d.Id));
        if (collision is not null)
        {
            throw new InvalidDataException(
                $"'{collision.Id}' appears as both a model and a deferred pin.");
        }

        return new ModelCatalog(parsed, deferred);
    }

    private static DeferredModelPin ParseDeferred(JsonElement element)
    {
        var id = RequireString(element, "id");

        var url = RequireString(element, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"Deferred pin '{id}' must have an absolute https url.");
        }

        var sha = RequireString(element, "sha256");
        if (!IsSha256Hex(sha))
        {
            throw new InvalidDataException($"Deferred pin '{id}' has a sha256 that is not 64 hex characters.");
        }

        // A pin whose whole purpose is to be checkable later is worthless without both halves, so
        // unlike a model entry these are required rather than optional.
        var size = OptionalLong(element, "sizeBytes")
            ?? throw new InvalidDataException($"Deferred pin '{id}' must record a sizeBytes.");

        if (size <= 0)
        {
            throw new InvalidDataException($"Deferred pin '{id}' has a non-positive sizeBytes.");
        }

        return new DeferredModelPin
        {
            Id = id,
            Family = RequireString(element, "family"),
            FileName = RequireString(element, "fileName"),
            Url = uri,
            SizeBytes = size,
            Sha256 = sha.ToLowerInvariant(),
            Purpose = RequireString(element, "purpose"),
        };
    }

    private static ModelDescriptor ParseModel(JsonElement element)
    {
        var id = RequireString(element, "id");
        var (files, directory) = ParseFiles(element, id);

        return new ModelDescriptor
        {
            Id = id,
            Task = ParseTask(element, id),
            Family = RequireString(element, "family"),
            DisplayName = RequireString(element, "displayName"),
            Quantisation = RequireString(element, "quantisation"),
            Files = files,
            DirectoryName = directory,
            Verified = element.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True,
            License = RequireString(element, "license"),
            AttributionIds = ParseAttributionIds(element, id),
            Languages = ParseStringArray(element, "languages"),
            Recommended = element.TryGetProperty("recommended", out var recommended) && recommended.ValueKind == JsonValueKind.True,
            Engine = OptionalString(element, "engine"),
            Notes = OptionalString(element, "notes"),
        };
    }

    /// <summary>
    /// Reads an entry's files in either of the two shapes the manifest allows, and refuses a mix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inline</b> — <c>fileName</c>, <c>url</c>, <c>sizeBytes</c>, <c>sha256</c> on the entry
    /// itself, which is every entry that predates 2026-08-20 and is still the right shape for a
    /// model that is one file. <b>Multi</b> — a <c>directory</c> and a <c>files</c> array of the
    /// same four keys each.
    /// </para>
    /// <para>
    /// An entry carrying both is refused rather than resolved, because there is no reading of it
    /// that is obviously right and the two candidate readings — "the inline one is a member of the
    /// set" and "the inline one is a legacy leftover to ignore" — differ by a whole file. Refusing
    /// costs a release engineer one error message; guessing costs a user a model that installs and
    /// does not load.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The notices an entry owes: <c>attributionId</c> for one, <c>attributionIds</c> for several.
    /// </summary>
    /// <remarks>
    /// The same either/or as <see cref="ParseFiles"/>, refused together for the same reason — two
    /// spellings of one fact is how the two come to disagree. The plural form exists because one
    /// download can carry two upstream works under two licences; see
    /// <see cref="ModelDescriptor.AttributionIds"/>. Order is preserved, and it is the order the
    /// notices render in.
    /// </remarks>
    private static IReadOnlyList<string> ParseAttributionIds(JsonElement element, string id)
    {
        var hasInline = element.TryGetProperty("attributionId", out _);
        var hasArray = element.TryGetProperty("attributionIds", out var array);

        if (hasInline && hasArray)
        {
            throw new InvalidDataException(
                $"Model '{id}' has both an 'attributionId' and an 'attributionIds' array. Use one: an entry " +
                "owing a single notice names it inline, an entry owing several lists them.");
        }

        if (!hasArray)
        {
            return [RequireString(element, "attributionId")];
        }

        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"Model '{id}' has an 'attributionIds' that is not a non-empty array.");
        }

        var ids = new List<string>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { Length: > 0 } value)
            {
                throw new InvalidDataException($"Model '{id}' has an 'attributionIds' entry that is not a non-empty string.");
            }

            if (ids.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                // A repeated key would render the same notice twice, which reads as though two
                // separate works happened to carry identical terms.
                throw new InvalidDataException($"Model '{id}' names attribution '{value}' more than once.");
            }

            ids.Add(value);
        }

        return ids;
    }

    private static (IReadOnlyList<ModelFile> Files, string? Directory) ParseFiles(JsonElement element, string id)
    {
        var hasInline = element.TryGetProperty("fileName", out _);
        var hasArray = element.TryGetProperty("files", out var array);

        if (hasInline && hasArray)
        {
            throw new InvalidDataException(
                $"Model '{id}' has both an inline 'fileName' and a 'files' array. Use one: a single-file " +
                "entry names its file inline, a multi-file entry lists them in 'files' with a 'directory'.");
        }

        if (!hasArray)
        {
            // Inline. No directory: these land in the store root, exactly where they always have.
            // <b>A single-file entry keeps the bare-name rule.</b> It has no directory of its own,
            // so it lands in the store root and its name becomes <c>StorageName</c> — a subpath there
            // would mean the store looking for the entry under a name that is not the one it wrote.
            // The widening of 2026-08-27 is for entries that DO have a directory to be beneath.
            return ([ParseFile(element, id, "fileName", allowSubpath: false)], null);
        }

        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"Model '{id}' has a 'files' that is not a non-empty array.");
        }

        var files = new List<ModelFile>();
        foreach (var file in array.EnumerateArray())
        {
            files.Add(ParseFile(file, id, "fileName", allowSubpath: true));
        }

        var duplicate = files
            .GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            // Two entries writing the same name means whichever downloads last wins, and the
            // digest that was checked is not the one on disk.
            throw new InvalidDataException($"Model '{id}' lists '{duplicate.Key}' more than once.");
        }

        var directory = OptionalString(element, "directory")
            ?? throw new InvalidDataException(
                $"Model '{id}' uses a 'files' array and must name a 'directory' to install into. " +
                "Its files would otherwise share the store root with every other entry, and names " +
                "like config.json belong to no single model.");

        if (directory.Length == 0
            || directory.Contains('/', StringComparison.Ordinal)
            || directory.Contains('\\', StringComparison.Ordinal)
            || directory is "." or ".."
            || directory != Path.GetFileName(directory))
        {
            throw new InvalidDataException($"Model '{id}' has a directory that is not a bare directory name.");
        }

        return (files, directory);
    }

    private static ModelFile ParseFile(JsonElement element, string id, string fileNameKey, bool allowSubpath)
    {
        var url = RequireString(element, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"Model '{id}' must have an absolute https url (found '{url}').");
        }

        var sha = OptionalString(element, "sha256");
        if (sha is not null && !IsSha256Hex(sha))
        {
            throw new InvalidDataException($"Model '{id}' has a sha256 that is not 64 hex characters.");
        }

        var fileName = RequireString(element, fileNameKey);
        if (!IsSafeRelativeFileName(fileName) || (!allowSubpath && fileName.Contains('/', StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                allowSubpath
                    ? $"Model '{id}' has a fileName that is not a bare file name or a safe relative " +
                      "path under the entry's directory. Separators must be '/', no segment may be " +
                      "empty, '.', '..' or rooted, and no segment may contain ':'."
                    : $"Model '{id}' is a single-file entry, so its fileName must be a bare file " +
                      "name: it is stored in the store root under that name, with no directory of " +
                      "its own to be beneath.");
        }

        return new ModelFile
        {
            FileName = fileName,
            Url = uri,
            SizeBytes = OptionalLong(element, "sizeBytes"),
            Sha256 = sha?.ToLowerInvariant(),
        };
    }

    /// <summary>
    /// <c>"task"</c> is optional and defaults to transcription; when present it must be one of the
    /// known words. A misspelling is refused rather than defaulted, because defaulting it would
    /// list a diarisation or translation model as an ASR model — the exact thing the field exists
    /// to stop.
    /// </summary>
    private static ModelTask ParseTask(JsonElement element, string id)
    {
        if (!element.TryGetProperty("task", out var value))
        {
            return ModelTask.Transcription;
        }

        // Present but not a string — null, a number, an array — is refused like a misspelling: only
        // absence means "transcription", because defaulting a broken value would list a diarisation
        // model as an ASR model, which is what the field exists to stop.
        var task = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return task switch
        {
            "transcription" => ModelTask.Transcription,
            "diarisation" => ModelTask.Diarisation,
            "translation" => ModelTask.Translation,
            "voice-activity" => ModelTask.VoiceActivity,
            "answering" => ModelTask.Answering,
            _ => throw new InvalidDataException(
                $"Model '{id}' has task {(task is null ? value.ValueKind.ToString().ToLowerInvariant() : $"'{task}'")}; known tasks are transcription, diarisation, translation, voice-activity and answering."),
        };
    }

    /// <summary>
    /// Whether a catalogue <c>fileName</c> is a bare name or a safe relative path beneath the
    /// entry's directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bare names were the only shape until 2026-08-27</b>, and widening this is the security-
    /// sensitive part of adding an entry whose upstream layout has subdirectories: a
    /// <c>fileName</c> comes out of a JSON manifest and ends up as the target of a file write, so
    /// anything this accepts is somewhere the installer can be made to write. The pyannote entry
    /// needs <c>segmentation/pytorch_model.bin</c> because its pipeline resolves its parts through
    /// its own <c>config.yaml</c>; flattening them would mean rewriting a config the installer
    /// pinned. (Its digest is not pinned — the repository is gated and Hugging Face masks the LFS
    /// object ids — but its size is, and rewriting a config would change that too.)
    /// </para>
    /// <para>
    /// <b>What is refused is everything that could leave the directory or mean two things.</b>
    /// Backslashes are out so that one manifest reads the same on every platform and so that
    /// <c>..\</c> cannot arrive spelled differently from <c>../</c>. A rooted path is out, an empty
    /// segment is out — which also rejects a trailing slash and a doubled one — and <c>.</c> and
    /// <c>..</c> are out as whole segments, which is what stops traversal. Each surviving segment
    /// must additionally be its own bare file name, so a segment carrying a drive letter or any
    /// other separator the runtime recognises is refused rather than normalised.
    /// </para>
    /// </remarks>
    internal static bool IsSafeRelativeFileName(string value)
    {
        if (value.Length == 0
            || value.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(value))
        {
            return false;
        }

        foreach (var segment in value.Split('/'))
        {
            // <b><c>:</c> is refused explicitly, because <see cref="Path.GetFileName(string)"/> does
            // not catch it.</b> That method splits on directory separators only, so <c>"a:b"</c>
            // comes back unchanged and passes the equality test below — and on Windows <c>a:b</c>
            // names an alternate data stream of <c>a</c>, not a file called <c>a:b</c>. The
            // installer would create the parent, write a stream nothing later looks for, and the
            // digest check would then fail on a file it could not find. <c>"C:x"</c> is the same
            // hazard spelled as a drive-relative path.
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.Contains(':', StringComparison.Ordinal)
                || segment != Path.GetFileName(segment))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private static string RequireString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"Model manifest entry is missing required string '{name}'.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? OptionalLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static IReadOnlyList<string> ParseStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static ModelCatalog LoadEmbedded()
    {
        var assembly = typeof(ModelCatalog).GetTypeInfo().Assembly;
        const string ResourceName = "Parakeet.Core.Models.models.json";

        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded model manifest '{ResourceName}' is missing from {assembly.GetName().Name}.");

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
