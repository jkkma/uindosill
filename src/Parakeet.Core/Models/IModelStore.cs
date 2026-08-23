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
    /// The same, against a catalogue the caller names rather than the shipped one.
    /// </summary>
    /// <remarks>
    /// Which catalogue is asked decides which files count as sideloaded, so a caller that was
    /// handed a catalogue has to be able to pass the same one here. Without it the window would
    /// list what is installed according to one catalogue while every other answer on the tab came
    /// from another.
    /// </remarks>
    IReadOnlyList<InstalledModel> ListInstalled(ModelCatalog catalog);

    /// <summary>
    /// Deletes the file, or the whole directory for a multi-file entry. Returns false when there
    /// was nothing to delete.
    /// </summary>
    bool Remove(ModelDescriptor model);

    /// <summary>
    /// Deletes a weights file in the store root that no catalogue entry claims. Returns false when
    /// there was nothing to delete, and refuses anything that is not a bare file name directly in
    /// the root or that a catalogue entry does claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sideloaded files are real and are not reachable through <see cref="Remove(ModelDescriptor)"/>,
    /// because there is no descriptor to pass it. Four quantisations withdrawn from the catalogue on
    /// 2026-08-20 are the case that makes this necessary: they were installed by an earlier build,
    /// they still occupy about 3 GiB, and until this existed the only surface that would admit they
    /// were there was <c>uindosill models</c>, which could list them and not delete them either.
    /// </para>
    /// <para>
    /// A catalogue-claimed name is refused rather than accepted as a shortcut, so that the two
    /// removal paths cannot come to disagree about what an entry consists of: a multi-file entry is
    /// a directory and its removal is the descriptor's business.
    /// </para>
    /// </remarks>
    bool RemoveSideloaded(string fileName);

    /// <summary>The same, against a catalogue the caller names. See <see cref="ListInstalled(ModelCatalog)"/>.</summary>
    bool RemoveSideloaded(string fileName, ModelCatalog catalog);
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

    /// <inheritdoc />
    public bool RemoveSideloaded(string fileName) => RemoveSideloaded(fileName, ModelCatalog.Default);

    /// <summary>
    /// As <see cref="RemoveSideloaded(string)"/>, against a catalogue the caller names.
    /// </summary>
    /// <remarks>
    /// The overload exists so a test can establish what "no entry claims this" means without
    /// depending on the shipped catalogue, which is a list that changes.
    /// </remarks>
    public bool RemoveSideloaded(string fileName, ModelCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(catalog);

        // A bare name and nothing else. Anything carrying a separator — a relative walk out of the
        // root, an absolute path elsewhere on the disk — is refused rather than normalised, because
        // this method deletes and the only safe reading of an unexpected shape is "not mine".
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return false;
        }

        // Only the two shapes the store enumerates. A stray .txt beside the weights is not this
        // method's to delete, and neither is a `.part` staging directory.
        if (!ModelExtensions.Any(extension =>
                fileName.EndsWith(extension.TrimStart('*'), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // Claimed by an entry means it is that entry's to remove, through its descriptor.
        if (catalog.Models.Any(model =>
                string.Equals(model.StorageName, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var path = Path.Combine(RootDirectory, fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public void EnsureDirectoryExists() => Directory.CreateDirectory(RootDirectory);
}
