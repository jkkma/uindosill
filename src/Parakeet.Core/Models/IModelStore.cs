namespace Parakeet.Core.Models;

/// <summary>Where weights live on disk.</summary>
public interface IModelStore
{
    /// <summary>Directory holding the models. Created on demand, never inside the install directory.</summary>
    string RootDirectory { get; }

    /// <summary>
    /// Where this entry lives: the file for a single-file entry, the directory for a multi-file one.
    /// What an engine is pointed at either way.
    /// </summary>
    string PathFor(ModelDescriptor model);

    /// <summary>Where one file of an entry lives.</summary>
    string PathFor(ModelDescriptor model, ModelFile file);

    /// <summary>
    /// True when every file of the entry is on disk.
    /// </summary>
    /// <remarks>
    /// Presence, not integrity: this is called on every window refresh and hashing 350 MB to answer
    /// it would be a stall the user pays for repeatedly. Verification belongs to
    /// <see cref="ModelInstaller"/>, which does it once, at install, over the bytes it just wrote.
    /// For a multi-file entry the honest answer is nonetheless per file rather than per directory —
    /// an install only ever appears complete, so a directory missing a file means someone deleted
    /// one by hand, and reporting that as installed would send it to an engine that cannot load it.
    /// </remarks>
    bool IsInstalled(ModelDescriptor model);

    IReadOnlyList<InstalledModel> ListInstalled();

    /// <summary>
    /// Deletes the file, or the whole directory for a multi-file entry. Returns false when there
    /// was nothing to delete.
    /// </summary>
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

    /// <summary>File patterns a model can arrive as: GGUF for transcription, ONNX for diarisation.</summary>
    private static readonly string[] ModelExtensions = ["*.gguf", "*.onnx"];

    public LocalModelStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory
            ?? Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable)
            ?? DefaultRootDirectory();
    }

    public string RootDirectory { get; }

    /// <summary>
    /// <c>%LOCALAPPDATA%\Uindosill\models</c> on Windows, and the XDG equivalent elsewhere, always
    /// resolved through the platform API so redirected and roaming profiles keep working. The
    /// parent is <see cref="UserDataPaths.RootDirectory"/>, which the settings file shares.
    /// </summary>
    public static string DefaultRootDirectory() =>
        Path.Combine(UserDataPaths.RootDirectory(), "models");

    public string PathFor(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(RootDirectory, model.StorageName);
    }

    public string PathFor(ModelDescriptor model, ModelFile file)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(file);

        return model.IsMultiFile
            ? Path.Combine(RootDirectory, model.DirectoryName!, file.FileName)
            : Path.Combine(RootDirectory, file.FileName);
    }

    public bool IsInstalled(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Files.All(file => File.Exists(PathFor(model, file)));
    }

    public IReadOnlyList<InstalledModel> ListInstalled() => ListInstalled(ModelCatalog.Default);

    public IReadOnlyList<InstalledModel> ListInstalled(ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (!Directory.Exists(RootDirectory))
        {
            return [];
        }

        var singleFileEntries = catalog.Models
            .Where(m => !m.IsMultiFile)
            .ToDictionary(m => m.StorageName, StringComparer.OrdinalIgnoreCase);
        var installed = new List<InstalledModel>();

        // Two extensions, not one. The transcription weights are GGUF and the diarisation model is
        // a single ONNX graph, and a store that enumerates only `*.gguf` reports a diariser the
        // user has installed as missing — `models list` would not show it and `models remove` would
        // find nothing to remove, while `transcribe --speakers` loaded it perfectly well.
        //
        // TopDirectoryOnly is also what keeps a multi-file entry from being reported nine times:
        // its graphs live one level down, and they are listed below as the one thing they are.
        foreach (var path in ModelExtensions
            .SelectMany(extension => Directory.EnumerateFiles(RootDirectory, extension, SearchOption.TopDirectoryOnly))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            singleFileEntries.TryGetValue(name, out var descriptor);

            installed.Add(new InstalledModel
            {
                Id = descriptor?.Id ?? Path.GetFileNameWithoutExtension(name),
                Path = path,
                SizeBytes = new FileInfo(path).Length,
                Descriptor = descriptor,
            });
        }

        // Directories are listed from the catalogue rather than from the disk, which is the
        // opposite of the loop above and deliberate. A bare file in the store root is
        // self-describing — it is a model, sideloaded if nothing in the catalogue claims it — while
        // a directory is only a model because an entry says which files make it one. There is no
        // such thing as a sideloaded multi-file model, and a stray directory (a `.part` staging
        // directory, most likely) must not be reported as one.
        foreach (var model in catalog.Models.Where(m => m.IsMultiFile).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
        {
            var directory = PathFor(model);
            if (!Directory.Exists(directory) || !IsInstalled(model))
            {
                continue;
            }

            installed.Add(new InstalledModel
            {
                Id = model.Id,
                Path = directory,
                SizeBytes = model.Files.Sum(file => new FileInfo(PathFor(model, file)).Length),
                Descriptor = model,
            });
        }

        return [.. installed.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)];
    }

    public bool Remove(ModelDescriptor model)
    {
        var path = PathFor(model);

        if (model.IsMultiFile)
        {
            // The whole directory, not just the files the manifest lists. Whatever else is in there
            // arrived with this entry — an `ort_config.json` a future revision adds, say — and
            // leaving it behind would make the next install look partial and the disk usage a lie.
            if (!Directory.Exists(path))
            {
                return false;
            }

            Directory.Delete(path, recursive: true);
            return true;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public void EnsureDirectoryExists() => Directory.CreateDirectory(RootDirectory);
}
