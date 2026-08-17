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

    private ModelCatalog(IReadOnlyList<ModelDescriptor> models, IReadOnlyList<DeferredModelPin> deferred)
    {
        Models = models;
        Deferred = deferred;
        _byId = models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        _byTask = Enum.GetValues<ModelTask>().ToDictionary(
            task => task,
            task => (IReadOnlyList<ModelDescriptor>)[.. models.Where(m => m.Task == task)]);
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

        var fileName = RequireString(element, "fileName");
        if (fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal)
            || fileName != Path.GetFileName(fileName))
        {
            throw new InvalidDataException($"Model '{id}' has a fileName that is not a bare file name.");
        }

        return new ModelDescriptor
        {
            Id = id,
            Task = ParseTask(element, id),
            Family = RequireString(element, "family"),
            DisplayName = RequireString(element, "displayName"),
            Quantisation = RequireString(element, "quantisation"),
            FileName = fileName,
            Url = uri,
            SizeBytes = OptionalLong(element, "sizeBytes"),
            Sha256 = sha?.ToLowerInvariant(),
            Verified = element.TryGetProperty("verified", out var verified) && verified.ValueKind == JsonValueKind.True,
            License = RequireString(element, "license"),
            AttributionId = RequireString(element, "attributionId"),
            Languages = ParseStringArray(element, "languages"),
            Recommended = element.TryGetProperty("recommended", out var recommended) && recommended.ValueKind == JsonValueKind.True,
            Notes = OptionalString(element, "notes"),
        };
    }

    /// <summary>
    /// <c>"task"</c> is optional and defaults to transcription; when present it must be one of the
    /// two known words. A misspelling is refused rather than defaulted, because defaulting it
    /// would list a diarisation model as an ASR model — the exact thing the field exists to stop.
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
            _ => throw new InvalidDataException(
                $"Model '{id}' has task {(task is null ? value.ValueKind.ToString().ToLowerInvariant() : $"'{task}'")}; known tasks are transcription and diarisation."),
        };
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
