using System.Text.Json;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.LlamaServer;

namespace Parakeet.App.Services;

/// <summary>Where an answer is drawn from, as the person has asked for it to be decided.</summary>
public enum AskModePreference
{
    /// <summary>The question decides, through <see cref="Parakeet.Core.Answers.QuestionRouter"/>.</summary>
    Automatic = 0,

    /// <summary>Always the parts that matched — the fast tier, and the only one with acceptable
    /// prefill on integrated graphics at length.</summary>
    Retrieval = 1,

    /// <summary>Always the whole recording, however long it is.</summary>
    WholeTranscript = 2,
}

/// <summary>The handful of choices that outlive a run of the application.</summary>
public sealed record AppSettings
{
    /// <summary>
    /// Whether the application asks GitHub Releases, once at launch, whether a newer version
    /// exists. Default on, and the one thing this product does on the network unprompted —
    /// <c>docs/PHASES.md</c> decision 4, and the README says so where a user will read it.
    /// </summary>
    public bool CheckForUpdatesOnLaunch { get; init; } = true;

    /// <summary>
    /// The compute backend the user last chose, or null when they never have.
    /// </summary>
    /// <remarks>
    /// Null is not a synonym for CPU or for Vulkan — it means "nobody has said", which is what lets
    /// <see cref="Parakeet.App.ViewModels.MainWindowViewModel"/> pick the best backend actually
    /// present on disk instead. A stored value always wins over that, including when it is the
    /// slower one: someone who selected CPU because the GPU path misbehaves on their machine has
    /// said something, and having the application quietly reinstate the GPU on the next launch
    /// would be the setting mattering least exactly when it matters most.
    /// </remarks>
    public ComputeBackend? Backend { get; init; }

    /// <summary>
    /// Whether the ask model thinks before answering. Off as shipped: thinking runs at the
    /// model's own generation speed, which on integrated graphics turns a half-minute answer
    /// into several minutes — the measured 2026-08-24 basis is in docs/UNPROVEN.md — and the
    /// grammar-constrained default answered no worse in the same session's comparison.
    /// </summary>
    public bool AskThinking { get; init; }

    /// <summary>
    /// Where answers are drawn from. <see cref="AskModePreference.Automatic"/> as shipped — the
    /// question decides, which is the register's decision 3 router — because the alternative is
    /// a person having to know that "summarise this" and "when did they mention X" are served by
    /// different tiers. The two fixed settings stay for anyone who would rather decide once.
    /// </summary>
    public AskModePreference AskMode { get; init; } = AskModePreference.Automatic;

    /// <summary>
    /// The file name — not the path — of the .gguf the Ask panel should serve, or null to take
    /// the largest present. Null is the shipped default and stays meaningful: it means "nobody
    /// has chosen", so a folder whose contents change later still resolves to something.
    /// </summary>
    /// <remarks>
    /// A name rather than a path because the folder is the application's own and may move
    /// between installs, and because a stored path to a file someone has since deleted is a
    /// setting that fails silently. A name that no longer matches anything falls back to the
    /// largest, exactly as if nothing had been chosen — the models on that disk are not this
    /// application's to keep track of between runs.
    /// </remarks>
    public string? AskModelFileName { get; init; }

    /// <summary>
    /// The catalogue id of the model that labels speakers, or null when nobody has chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two entries do this job and they are not ranked: Sortformer is fast, ships with the
    /// installer and stops at four voices; DiariZen has no such limit, is downloaded rather than
    /// bundled, is licensed for non-commercial use only and takes about as long again as the
    /// recording. Which is better depends on the recording and on who is doing the recording, so
    /// this is a choice rather than a default with an override.
    /// </para>
    /// <para>
    /// <b>Null keeps meaning "nobody has said", and it has to.</b> A stored id whose entry is not
    /// installed — removed from the Models tab, or carried over from another machine — resolves as
    /// if nothing had been chosen rather than turning speaker labelling off, because a setting that
    /// silently disables a feature is worse than one that is ignored. The same reasoning as
    /// <see cref="AskModelFileName"/>, and for the same reason: the id is stable where a path
    /// is not.
    /// </para>
    /// </remarks>
    public string? DiarisationModelId { get; init; }

    /// <summary>
    /// Where a mixture-of-experts ask model's experts run.
    /// <see cref="MoeExpertPlacement.Automatic"/> as shipped — the Vulkan loader is asked, and a
    /// card holds its own experts where the processor's graphics cannot.
    /// </summary>
    /// <remarks>
    /// A setting rather than a detected constant because only one end of it has been measured:
    /// on the second machine, in system memory is the difference between a 26B-class mixture
    /// running and not loading at all, and no machine here has a discrete card on the Vulkan ask
    /// path to measure the other end on. Somebody whose card the automatic rule reads wrongly —
    /// or whose driver the loader cannot answer for — needs a way to say so that does not
    /// involve rebuilding.
    /// </remarks>
    public MoeExpertPlacement AskExpertPlacement { get; init; } = MoeExpertPlacement.Automatic;

    /// <summary>
    /// The output folder the user last chose, or null when they never have — blank in the box,
    /// files beside each input.
    /// </summary>
    /// <remarks>
    /// Restored at launch only when the directory still exists, and cleared from the file when it
    /// does not: the folder a user picks is often a removable drive, and a stored path with no
    /// drive behind it would point every export at a location that cannot take one. Missing means
    /// choose again, not fail later.
    /// </remarks>
    public string? OutputDirectory { get; init; }

    public static AppSettings Default { get; } = new();
}

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as JSON beside the model weights.
/// </summary>
/// <remarks>
/// <para>
/// Beside the weights, not in the install directory: a settings file under the install root is
/// destroyed by every update, so the update check a user switched off would switch itself back on
/// at the first update — which is the setting mattering least exactly when it matters most.
/// <see cref="UserDataPaths.RootDirectory"/> is the one copy of that path.
/// </para>
/// <para>
/// Every failure here is swallowed and answered with the defaults. A settings file is not worth a
/// window that will not open: an unreadable, truncated or hand-edited file has to degrade to "as
/// shipped" rather than to a crash on the first frame.
/// </para>
/// </remarks>
public sealed class AppSettingsStore
{
    /// <summary>Overrides the file's location, for portable installs and tests.</summary>
    public const string PathEnvironmentVariable = "UINDOSILL_SETTINGS_PATH";

    public AppSettingsStore(string? path = null) =>
        Path = path
            ?? Environment.GetEnvironmentVariable(PathEnvironmentVariable)
            ?? DefaultPath();

    public string Path { get; }

    /// <summary><c>%LOCALAPPDATA%\Uindosill\settings.json</c> on Windows.</summary>
    public static string DefaultPath() =>
        System.IO.Path.Combine(UserDataPaths.RootDirectory(), "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return AppSettings.Default;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(Path));
            var root = document.RootElement;

            return new AppSettings
            {
                CheckForUpdatesOnLaunch = ReadBool(root, "checkForUpdatesOnLaunch", AppSettings.Default.CheckForUpdatesOnLaunch),
                AskThinking = ReadBool(root, "askThinking", AppSettings.Default.AskThinking),
                AskMode = ReadAskMode(root),
                AskModelFileName = ReadString(root, "askModelFileName"),
                DiarisationModelId = ReadString(root, "diarisationModelId"),
                AskExpertPlacement = ReadExpertPlacement(root),
                Backend = ReadBackend(root),
                OutputDirectory = ReadString(root, "outputDirectory"),
            };
        }
#pragma warning disable CA1031 // Any unreadable file means "as shipped", never a failure to start.
        catch (Exception)
#pragma warning restore CA1031
        {
            return AppSettings.Default;
        }
    }

    /// <summary>
    /// Reads the file, applies <paramref name="change"/> to it, and writes it back.
    /// </summary>
    /// <remarks>
    /// The only way callers should write a single setting, and the reason is a bug this file
    /// invited the moment it held more than one: <c>Save(new AppSettings { OneField = value })</c>
    /// compiles, reads correctly, and silently resets every other field to its default. Both
    /// call sites did exactly that when there was only one setting and it was harmless. Take the
    /// current settings and modify them, and it cannot happen.
    /// </remarks>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Save(change(Load()));
    }

    /// <summary>Returns false when the file could not be written; the caller carries on regardless.</summary>
    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var values = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["checkForUpdatesOnLaunch"] = settings.CheckForUpdatesOnLaunch,
                ["askThinking"] = settings.AskThinking,

                // The name, never the enum's number — same rule as the backend, and for the same
                // reason: reordering the enum would silently turn one user's saved choice into
                // another's.
                ["askMode"] = settings.AskMode.ToString().ToLowerInvariant(),

                // The name for the same reason again. Always written, never omitted: unlike the
                // backend, "nobody has said" and the shipped default are the same behaviour here
                // — automatic asks the loader either way — so there is no second shape to keep.
                ["askExpertPlacement"] = settings.AskExpertPlacement.ToString().ToLowerInvariant(),
            };

            // Omitted rather than written as null when nobody has chosen, so "never chosen" and
            // "chosen and then unset" are the same file rather than two shapes Load has to agree
            // about. Written as the name, never the enum's number: the numbers are an
            // implementation detail, and reordering the enum would silently turn one user's saved
            // CUDA into Vulkan.
            if (settings.Backend is { } backend)
            {
                values["backend"] = backend.ToString().ToLowerInvariant();
            }

            // Omitted when blank for the same reason as the backend: "never chosen" and "cleared"
            // are one shape.
            if (settings.OutputDirectory is { Length: > 0 } outputDirectory)
            {
                values["outputDirectory"] = outputDirectory;
            }

            if (settings.AskModelFileName is { Length: > 0 } askModel)
            {
                values["askModelFileName"] = askModel;
            }

            // Same shape again: absent means nobody has chosen a diariser, which is not the
            // same as choosing none. EngineProvider reads it that way.
            if (settings.DiarisationModelId is { Length: > 0 } diarisationModel)
            {
                values["diarisationModelId"] = diarisationModel;
            }

            var json = JsonSerializer.Serialize(values);

            // Written beside the target and moved into place, rather than over it.
            // File.WriteAllText truncates first, so a process that dies between the truncate and the
            // flush leaves an empty file — and Load turns an unreadable file into the shipped
            // defaults, which would silently switch the update check back on. This application has a
            // documented way of dying abruptly (gotcha 19), so that is not a hypothetical.
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, Path, overwrite: true);
            return true;
        }
#pragma warning disable CA1031 // A read-only profile must not stop the application working.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// The stored backend name, or null for absent, unreadable or unrecognised.
    /// </summary>
    /// <remarks>
    /// An unknown name — a future backend read by an older build, or a hand-edited file — becomes
    /// null and therefore "pick the best one present", rather than throwing or defaulting to CPU.
    /// Same rule as the rest of this file: an unreadable setting degrades to as-shipped.
    /// </remarks>
    private static ComputeBackend? ReadBackend(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("backend", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "cpu" => ComputeBackend.Cpu,
            "vulkan" => ComputeBackend.Vulkan,
            "cuda" => ComputeBackend.Cuda,
            _ => null,
        };
    }

    /// <summary>
    /// The stored ask mode, falling back to the boolean this setting replaced.
    /// </summary>
    /// <remarks>
    /// <c>askWholeTranscript</c> was the 2026-08-25 shipped shape and lived for one day. A stored
    /// <c>true</c> was a deliberate choice and is honoured as the fixed whole-transcript setting;
    /// a stored <c>false</c> carries no choice at all — it was the default nobody had to touch —
    /// so it becomes <see cref="AskModePreference.Automatic"/> rather than pinning a user to
    /// retrieval on the strength of a value they never set.
    /// </remarks>
    private static AskModePreference ReadAskMode(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("askMode", out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.Trim().ToLowerInvariant() switch
            {
                "automatic" => AskModePreference.Automatic,
                "retrieval" => AskModePreference.Retrieval,
                "wholetranscript" => AskModePreference.WholeTranscript,
                _ => AppSettings.Default.AskMode,
            };
        }

        return ReadBool(root, "askWholeTranscript", false)
            ? AskModePreference.WholeTranscript
            : AppSettings.Default.AskMode;
    }

    /// <summary>
    /// The stored expert placement. Absent, unreadable or a name this build does not know all
    /// degrade to as-shipped, exactly as the backend and the ask mode do — a future name read by
    /// an older build must not pin anyone to a placement they never chose.
    /// </summary>
    private static MoeExpertPlacement ReadExpertPlacement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("askExpertPlacement", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return AppSettings.Default.AskExpertPlacement;
        }

        return value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "automatic" => MoeExpertPlacement.Automatic,
            "device" => MoeExpertPlacement.Device,
            "systemmemory" => MoeExpertPlacement.SystemMemory,
            _ => AppSettings.Default.AskExpertPlacement,
        };
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } read
            ? read
            : null;

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
