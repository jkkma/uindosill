using Parakeet.Core.Models;

namespace Parakeet.App.Services;

/// <summary>
/// Weights small enough to travel inside the installer, found beside the application.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue's rule was that the installer carries no weights and the Models tab downloads
/// them. That is right for the two biggest entries and was absurd for the smallest: the
/// speech-detection graph is <b>2.2 MiB</b>, and asking a user to visit a tab and download it
/// bought nothing but a dead checkbox on a fresh install. Since 2026-08-23 the installer carries
/// what fits — the speech detector and the speaker labeller, 455 MiB between them — and
/// <c>scripts/package-windows.ps1</c> fetches each against the digest the catalogue already pins.
/// </para>
/// <para>
/// <b>What does not fit, and why the rule survives.</b> A GitHub release asset must be under 2 GiB.
/// The recogniser is 1.34 GiB and the translator 1.34 GiB, so either one pushes the CUDA channel
/// past that limit and both together push every channel past it. Those two stay downloads because
/// of arithmetic, not principle.
/// </para>
/// <para>
/// The catalogue entries stay for all four: a machine that already downloaded one keeps using its
/// own copy, the entry is what pins the digest, and the Models tab is still where a fresh copy
/// comes from. The store always wins over the bundle — see
/// <c>StandardEngineProvider.PathForInstalledOrBundled</c> — because a user who downloaded one
/// chose to, and because that is the copy an update refreshes.
/// </para>
/// <para>
/// The same shape as <see cref="Tools.BundledTools"/>: located rather than assumed, and absence is
/// an answer rather than an exception — a build from source carries none of this until the
/// packaging script has run, and on such a build every model arrives the way it always did.
/// </para>
/// </remarks>
public static class BundledModels
{
    /// <summary>Overrides the search, for tests and for a developer with the files elsewhere.</summary>
    public const string DirectoryEnvironmentVariable = "UINDOSILL_BUNDLED_MODELS_DIR";

    /// <summary>The directory the packaging script writes into, beside the executable.</summary>
    public const string DirectoryName = "models";

    /// <summary>
    /// Which catalogue entries the installer carries. Read by the packaging script out of this file
    /// — the ids are the pins' names, and the pins themselves stay in <c>models.json</c>, so there
    /// is one copy of every digest and this list only says which of them travel.
    /// </summary>
    public static readonly string[] BundledIds =
    [
        "silero-vad-v5.1.2",
        "sortformer-4spk-v2.1",
    ];

    /// <summary>
    /// The bundled copy of <paramref name="model"/>, or null when this build carries none.
    /// </summary>
    /// <remarks>
    /// Single-file entries only. A multi-file entry — the translator's nine — installs into a
    /// directory of its own and is not something this ships, so asking about one answers null
    /// rather than inventing a layout nothing writes.
    /// </remarks>
    public static string? PathFor(ModelDescriptor model)
    {
        if (model.IsMultiFile)
        {
            return null;
        }

        var fileName = model.StorageName;

        foreach (var directory in CandidateDirectories())
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        if (Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            yield return Path.GetFullPath(fromEnvironment);
        }

        yield return Path.Combine(AppContext.BaseDirectory, DirectoryName);
    }
}
