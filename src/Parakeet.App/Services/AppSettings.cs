using System.Text.Json;
using Parakeet.Core.Models;

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
            };
        }
#pragma warning disable CA1031 // Any unreadable file means "as shipped", never a failure to start.
        catch (Exception)
#pragma warning restore CA1031
        {
            return AppSettings.Default;
        }
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

            var json = JsonSerializer.Serialize(new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["checkForUpdatesOnLaunch"] = settings.CheckForUpdatesOnLaunch,
            });

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

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
