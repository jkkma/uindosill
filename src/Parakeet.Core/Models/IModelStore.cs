namespace Parakeet.Core.Models;

/// <summary>Where weights live on disk.</summary>
public interface IModelStore
{
    /// <summary>Directory holding the models. Created on demand, never inside the install directory.</summary>
    string RootDirectory { get; }

    string PathFor(ModelDescriptor model);

    bool IsInstalled(ModelDescriptor model);

    IReadOnlyList<InstalledModel> ListInstalled();

    /// <summary>Deletes the file if present. Returns false when there was nothing to delete.</summary>
    bool Remove(ModelDescriptor model);
}

/// <summary>
/// Stores models under the user's local application data.
/// </summary>
/// <remarks>
/// Deliberately not the install directory: models there are destroyed by every update and
/// uninstall, and 670 MB re-downloaded on each patch is a product defect. Deliberately not a
/// hardcoded <c>%USERPROFILE%\.cache</c> either — that path ignores folder redirection and
/// roaming profiles, which is how managed Windows fleets are actually configured.
/// </remarks>
public sealed class LocalModelStore : IModelStore
{
    /// <summary>Environment variable that overrides the location, for portable installs and tests.</summary>
    public const string DirectoryEnvironmentVariable = "UINDOSILL_MODELS_DIR";

    public LocalModelStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory
            ?? Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable)
            ?? DefaultRootDirectory();
    }

    public string RootDirectory { get; }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Uindosill\models</c> on Windows, and the XDG equivalent elsewhere, always
    /// resolved through the platform API so redirected and roaming profiles keep working.
    /// </summary>
    public static string DefaultRootDirectory()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrEmpty(localAppData))
        {
            // A profile with no local application data is broken, but falling back to the
            // current directory would scatter 670 MB blobs wherever the app happened to start.
            localAppData = Path.Combine(Path.GetTempPath(), "Uindosill");
        }

        return Path.Combine(localAppData, "Uindosill", "models");
    }

    public string PathFor(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(RootDirectory, model.FileName);
    }

    public bool IsInstalled(ModelDescriptor model) => File.Exists(PathFor(model));

    public IReadOnlyList<InstalledModel> ListInstalled() => ListInstalled(ModelCatalog.Default);

    public IReadOnlyList<InstalledModel> ListInstalled(ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (!Directory.Exists(RootDirectory))
        {
            return [];
        }

        var byFileName = catalog.Models.ToDictionary(m => m.FileName, StringComparer.OrdinalIgnoreCase);
        var installed = new List<InstalledModel>();

        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*.gguf", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            byFileName.TryGetValue(name, out var descriptor);

            installed.Add(new InstalledModel
            {
                Id = descriptor?.Id ?? Path.GetFileNameWithoutExtension(name),
                Path = path,
                SizeBytes = new FileInfo(path).Length,
                Descriptor = descriptor,
            });
        }

        return [.. installed.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public bool Remove(ModelDescriptor model)
    {
        var path = PathFor(model);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public void EnsureDirectoryExists() => Directory.CreateDirectory(RootDirectory);
}
