using System.Text.Json;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.App.Services;

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
    /// Whether answers draw on the whole transcript instead of retrieval — the opt-in the
    /// register's decision 3 names. Off as shipped: retrieval is the fast path everywhere and
    /// the only path with acceptable prefill on integrated graphics; the whole-transcript pass
    /// is what answers global questions — summaries, main topics — at the price of reading
    /// everything first.
    /// </summary>
    public bool AskWholeTranscript { get; init; }

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
                AskWholeTranscript = ReadBool(root, "askWholeTranscript", AppSettings.Default.AskWholeTranscript),
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
                ["askWholeTranscript"] = settings.AskWholeTranscript,
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
