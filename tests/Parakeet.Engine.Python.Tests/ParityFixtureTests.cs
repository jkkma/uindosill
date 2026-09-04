using System.Text.Json;
using Parakeet.Core.Models;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// Holds the sidecar's translation parity fixtures to their shape, and the first of them to the
/// tokenizer fixture it came from.
/// </summary>
/// <remarks>
/// <para>
/// <c>python/uindosill_engines/translator/parity-sources.json</c> is a copy of the six marked
/// sentences in <c>tests/fixtures/translation/marian-tokenizer.json</c>, and it is a copy on
/// purpose: a sidecar that reached into <c>tests/</c> would stop working the day the tree is
/// packaged. This is what stops the copy drifting.
/// </para>
/// <para>
/// Drift would not fail loudly. The parity reference beside each sources file is the CPU's
/// translations <i>of them</i>, so changing one source without regenerating the reference makes
/// every provider fail the check for a reason that has nothing to do with the provider — and
/// changing both would quietly move what "parity" means.
/// </para>
/// <para>
/// <b>One fixture per checkpoint since 2026-09-04</b>, chosen by the sidecar on vocabulary size,
/// and each the shape the host sends its checkpoint: marked with the target token where the
/// catalogue declares one, bare where it declares none. The tests below hold every fixture to that,
/// and to naming a checkpoint the catalogue actually ships.
/// </para>
/// </remarks>
public sealed class ParityFixtureTests
{
    [Fact]
    public void TheParitySourcesAreTheTokenizerFixturesMarkedSentences()
    {
        var expected = TokenizerFixture.GetProperty("cases")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("markedSource").GetString())
            .ToArray();

        var actual = ParitySources.GetProperty("sources")
            .EnumerateArray()
            .Select(entry => entry.GetString())
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryParityReferenceHasOneTranslationPerSource()
    {
        // A reference with the wrong number of entries fails every provider on a shape mismatch,
        // which reads as "this machine is wrong" when what is wrong is the fixture.
        foreach (var (sourcesFile, referenceFile) in FixtureFiles)
        {
            var sources = Read(sourcesFile).GetProperty("sources").GetArrayLength();
            var translations = Read(referenceFile).GetProperty("translations").GetArrayLength();

            Assert.True(sources == translations, $"{sourcesFile}: {sources} sources against {translations} translations");
        }
    }

    [Fact]
    public void TheParitySourcesNameTheCheckpointTheyWereTakenFrom()
    {
        // The reference is the CPU's translations of these sentences by one checkpoint at one
        // revision. Carrying which is what lets a later reader tell a regenerated reference from a
        // reference for a different model.
        Assert.Equal(
            TokenizerFixture.GetProperty("model").GetString(),
            ParitySources.GetProperty("model").GetString());

        Assert.Equal(
            TokenizerFixture.GetProperty("revision").GetString(),
            ParitySources.GetProperty("revision").GetString());

        Assert.Equal(
            TokenizerFixture.GetProperty("vocabSize").GetInt32(),
            ParitySources.GetProperty("vocabSize").GetInt32());
    }

    [Fact]
    public void EverySourceCarriesTheTokenItsCheckpointReadsAndNothingElse()
    {
        // The host marks every source and the sidecar prepends nothing, so a fixture sentence
        // without the token is testing a path the product never takes — and the many-to-one
        // checkpoint given Spanish with no target returns fluent German rather than an error. The
        // single-direction checkpoint is the other way round: its vocabulary has no such token, and
        // one on a fixture sentence would be translated as text.
        foreach (var (sourcesFile, _) in FixtureFiles)
        {
            var fixture = Read(sourcesFile);
            var token = fixture.GetProperty("targetToken").ValueKind == JsonValueKind.String
                ? fixture.GetProperty("targetToken").GetString()
                : null;

            foreach (var source in fixture.GetProperty("sources").EnumerateArray())
            {
                var text = source.GetString()!;
                if (token is null)
                {
                    Assert.DoesNotContain(">>", text, StringComparison.Ordinal);
                }
                else
                {
                    Assert.StartsWith(token, text, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void EveryFixtureIsForACatalogueEntryAndDeclaresWhatTheCatalogueDeclares()
    {
        // The sidecar picks a fixture by vocabulary size, so two claiming one size would make the
        // check depend on file order; and a fixture whose token disagrees with the catalogue's
        // entry for the same family would hold a provider to sources the product never sends.
        var seen = new Dictionary<int, string>();

        foreach (var (sourcesFile, _) in FixtureFiles)
        {
            var fixture = Read(sourcesFile);
            var family = fixture.GetProperty("family").GetString()!;
            var vocabSize = fixture.GetProperty("vocabSize").GetInt32();

            Assert.False(seen.TryGetValue(vocabSize, out var other), $"{sourcesFile} and {other} both claim a vocabulary of {vocabSize}");
            seen[vocabSize] = sourcesFile;

            var entry = Assert.Single(ModelCatalog.Default.TranslationModels, m => m.Family == family);
            var declared = fixture.GetProperty("targetToken").ValueKind == JsonValueKind.String
                ? fixture.GetProperty("targetToken").GetString()
                : null;
            Assert.Equal(entry.TargetToken, declared);
        }

        Assert.Equal(ModelCatalog.Default.TranslationModels.Count, seen.Count);
    }

    private static JsonElement TokenizerFixture { get; } = Read("tests", "fixtures", "translation", "marian-tokenizer.json");

    private static JsonElement ParitySources { get; } =
        Read("python", "uindosill_engines", "translator", "parity-sources.json");

    /// <summary>Every sources file beside the sidecar's translator, with the reference each pairs with.</summary>
    private static IReadOnlyList<(string Sources, string Reference)> FixtureFiles { get; } =
        Directory.GetFiles(
                Path.Combine(RepositoryRoot, "python", "uindosill_engines", "translator"),
                "parity-sources*.json")
            .Order(StringComparer.Ordinal)
            .Select(path => (path, path.Replace("parity-sources", "parity-reference", StringComparison.Ordinal)))
            .ToList();

    private static JsonElement Read(params string[] parts) =>
        JsonDocument.Parse(File.ReadAllText(
            parts.Length == 1 ? parts[0] : Path.Combine([RepositoryRoot, .. parts]))).RootElement.Clone();

    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Uindosill.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new DirectoryNotFoundException($"No Uindosill.slnx above {AppContext.BaseDirectory}.");
        }
    }
}
