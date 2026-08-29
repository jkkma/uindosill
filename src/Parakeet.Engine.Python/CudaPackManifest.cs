using System.Text.Json;
using System.Text.Json.Serialization;
using Parakeet.Core.Models;

namespace Parakeet.Engine.Python;

/// <summary>One byte-range part of the pack's archive.</summary>
public sealed record CudaPackPart
{
    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("sizeBytes")]
    public required long SizeBytes { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

/// <summary>
/// What the CUDA pack is, pinned: its parts, their digests, and the torch build it carries.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <c>models.json</c> on purpose.</b> The pack is not a model — it does not go in
/// the model store, it is not offered in the Models tab, and its "is it installed" question is
/// answered by <see cref="PythonRuntime.IsCudaPack"/> rather than by <c>IModelStore</c>. Putting it
/// in the catalogue would have got it a row in a tab about weights and a delete button that removed
/// the wrong thing.
/// </para>
/// <para>
/// <b>It borrows the catalogue's shape all the same</b>, because the download is genuinely the same
/// problem: several pinned files, each with a size and a digest, fetched with resume into a staging
/// directory and moved into place all-or-nothing. <see cref="AsDescriptor"/> is the adapter, and it
/// is what lets <see cref="ModelInstaller"/> do the fetching rather than a second downloader being
/// written here.
/// </para>
/// </remarks>
public sealed record CudaPackManifest
{
    /// <summary>The name of the assembled archive, for the message when its digest disagrees.</summary>
    [JsonPropertyName("archiveName")]
    public required string ArchiveName { get; init; }

    [JsonPropertyName("archiveBytes")]
    public required long ArchiveBytes { get; init; }

    /// <summary>The digest of the whole reassembled archive, checked after concatenation.</summary>
    /// <remarks>
    /// Checked <b>as well as</b> the per-part digests rather than instead of them, and the pair is
    /// not redundant: the parts say each byte range arrived intact, and this says they were put
    /// back in the right order and none was missed. A reassembly bug is invisible to the first
    /// check and caught by the second.
    /// </remarks>
    [JsonPropertyName("archiveSha256")]
    public required string ArchiveSha256 { get; init; }

    /// <summary>How much disk the unpacked pack needs, for the message before it is started.</summary>
    [JsonPropertyName("unpackedBytes")]
    public required long UnpackedBytes { get; init; }

    /// <summary>
    /// False until the digests have been read back off an uploaded release asset.
    /// </summary>
    /// <remarks>
    /// The catalogue's own word, meaning the same thing: guessing is allowed, pretending is not.
    /// <see cref="CudaPackInstaller"/> refuses an unverified manifest unless the caller opts in.
    /// </remarks>
    [JsonPropertyName("verified")]
    public bool Verified { get; init; }

    /// <summary>The torch version the pack carries, which must be the bundle's pin in a CUDA build.</summary>
    [JsonPropertyName("torchVersion")]
    public required string TorchVersion { get; init; }

    [JsonPropertyName("packages")]
    public IReadOnlyDictionary<string, string> Packages { get; init; } =
        new Dictionary<string, string>();

    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }

    [JsonPropertyName("parts")]
    public required IReadOnlyList<CudaPackPart> Parts { get; init; }

    /// <summary>
    /// The version part of <see cref="TorchVersion"/> with the build local dropped: `2.13.0+cu130`
    /// becomes `2.13.0`, which is what `python/requirements-bundle.txt` pins.
    /// </summary>
    public string TorchVersionWithoutBuild =>
        TorchVersion.Split('+', 2)[0];

    /// <summary>Every part's size added up, which is what a user is about to download.</summary>
    public long TotalDownloadBytes => Parts.Sum(p => p.SizeBytes);

    /// <summary>
    /// The pack as something <see cref="ModelInstaller"/> can fetch: one multi-file entry whose
    /// files are the parts.
    /// </summary>
    /// <remarks>
    /// <paramref name="directoryName"/> is the subdirectory of whatever store the caller hands the
    /// installer — deliberately not the pack's own directory, because what lands there is an
    /// archive in pieces rather than a pack, and a half-assembled download must not be mistaken by
    /// <see cref="PythonRuntime.IsCudaPack"/> for an installed one. That is also why the default
    /// carries a suffix rather than being <c>python-cuda</c>.
    /// </remarks>
    public ModelDescriptor AsDescriptor(string directoryName = CudaPackInstaller.PartsDirectoryName) =>
        new()
        {
            Id = "python-cuda-pack",
            Family = "python-cuda",
            DisplayName = "Graphics acceleration (NVIDIA)",
            DirectoryName = directoryName,
            Verified = Verified,

            // **Three fields the catalogue requires and this entry has no honest value for.** The
            // pack is not weights: it has no quantisation, and its licence is the three upstream
            // packages' own rather than one this entry could name. They are filled in rather than
            // left blank because the descriptor is a borrowed shape — the pack is never in the
            // catalogue, never listed in the Models tab, and nothing reads these — and inventing a
            // plausible licence string here would be the more dangerous of the two mistakes.
            Quantisation = "none",
            License = "See the upstream packages: PyTorch (BSD-3-Clause) and its bundled NVIDIA " +
                      "CUDA libraries, whose terms are NVIDIA's.",
            AttributionIds = [],

            Files = [.. Parts.Select(p => new ModelFile
            {
                FileName = p.FileName,
                Url = new Uri($"{BaseUrl.TrimEnd('/')}/{p.FileName}"),
                SizeBytes = p.SizeBytes,
                Sha256 = p.Sha256,
            })],
        };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads the pinned manifest that ships beside this assembly.</summary>
    /// <remarks>
    /// A file rather than constants in code, so that the release step which produces the parts can
    /// write the digests back into the repository as data — the same reason <c>models.json</c> is a
    /// file. Read once and cached: it is a few hundred bytes and every call site is a UI refresh.
    /// </remarks>
    public static CudaPackManifest Shipped => _shipped ??= LoadShipped();

    private static CudaPackManifest? _shipped;

    private static CudaPackManifest LoadShipped()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "cuda-pack.json");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses a manifest, throwing with the reason rather than returning null.</summary>
    public static CudaPackManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<CudaPackManifest>(json, ReadOptions)
            ?? throw new PythonSidecarException("The CUDA pack manifest is empty.");

        if (manifest.Parts.Count == 0)
        {
            throw new PythonSidecarException("The CUDA pack manifest declares no parts.");
        }

        // A manifest whose parts do not add up to the archive it claims is one somebody edited by
        // hand, and the failure it would otherwise produce is a digest mismatch after a 1.8 GB
        // download. Cheaper to say so before the first byte.
        var total = manifest.Parts.Sum(p => p.SizeBytes);
        if (total != manifest.ArchiveBytes)
        {
            throw new PythonSidecarException(
                $"The CUDA pack manifest's parts total {total:N0} bytes and it claims an archive of " +
                $"{manifest.ArchiveBytes:N0}. One of the two is wrong, and neither is worth a download.");
        }

        return manifest;
    }
}
