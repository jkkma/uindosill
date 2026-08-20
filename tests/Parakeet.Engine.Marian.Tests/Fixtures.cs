using System.Text.Json;

namespace Parakeet.Engine.Marian.Tests;

/// <summary>
/// Finds the committed translation fixture, and the exported checkpoint when there is one.
/// </summary>
/// <remarks>
/// <para>
/// The two are deliberately different kinds of thing. <c>tests/fixtures/translation/</c> is in the
/// repository and CI reads it; the checkpoint is 1.34 GiB that no clone has, so anything needing it
/// is skipped rather than failed, and says which directory it looked in.
/// </para>
/// <para>
/// <b>That makes the fixture test a measurement rather than a CI check, and it is worth being exact
/// about why.</b> The ids in the fixture cannot be reproduced without <c>source.spm</c> and
/// <c>vocab.json</c> — 3.06 MB of the checkpoint, which this repository has never carried and whose
/// redistribution is a licence question rather than a size one. Everything about the tokenizer that
/// does <i>not</i> need them — the protobuf reader, the trie, the Unigram search, byte fallback,
/// the language-code rule — is tested hermetically beside it, so what the skip costs is the check
/// against HuggingFace's real ids and nothing else.
/// </para>
/// </remarks>
internal static class Fixtures
{
    public static string Directory { get; } = Find("tests", "fixtures", "translation");

    public static string? Repository { get; } = FindRepository();

    public static JsonDocument TokenizerFixture() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, "marian-tokenizer.json")));

    /// <summary>
    /// The exported checkpoint directory, or null unless a run explicitly asked for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in rather than discovered, and the reason is the test counts.</b> This originally
    /// searched <c>runs/translation-onnx/fp32-merged</c> and used it when it was there, which meant
    /// the seven checkpoint tests ran on a measuring machine and skipped on CI — so
    /// <c>scripts/check-test-counts.py</c> could not be told one true number for how many pass.
    /// Documents cannot record a count that depends on what happens to be installed, so the suite
    /// gives the same answer everywhere and these are asked for by name:
    /// </para>
    /// <code>
    /// UINDOSILL_TRANSLATION_MODEL=runs/translation-onnx/fp32-merged dotnet test
    /// </code>
    /// <para>
    /// A path that is set but not a directory returns null rather than throwing: the point is to
    /// skip cleanly on a machine that has not got it, and a typo should read as "not here" rather
    /// than as a broken suite.
    /// </para>
    /// </remarks>
    public static string? Checkpoint()
    {
        var declared = Environment.GetEnvironmentVariable("UINDOSILL_TRANSLATION_MODEL");
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        var path = Path.IsPathRooted(declared) || Repository is null
            ? declared
            : Path.Combine(Repository, declared);

        return System.IO.Directory.Exists(path) ? path : null;
    }

    /// <summary>The tokenizer's five files, without the two 1.3 GiB graphs beside them.</summary>
    public static string? TokenizerCheckpoint()
    {
        var checkpoint = Checkpoint();
        if (checkpoint is null)
        {
            return null;
        }

        foreach (var file in new[] { "source.spm", "target.spm", "vocab.json", "tokenizer_config.json" })
        {
            if (!File.Exists(Path.Combine(checkpoint, file)))
            {
                return null;
            }
        }

        return checkpoint;
    }

    private static string Find(params string[] parts)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine([directory, .. parts]);
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"{string.Join('/', parts)} was not found above the test binary.");
    }

    private static string? FindRepository()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (System.IO.Directory.Exists(Path.Combine(directory, "tests", "fixtures", "translation")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
