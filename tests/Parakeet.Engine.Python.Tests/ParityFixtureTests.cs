using System.Text.Json;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// Holds the sidecar's translation parity sources against the tokenizer fixture they came from.
/// </summary>
/// <remarks>
/// <para>
/// <c>python/uindosill_engines/translator/parity-sources.json</c> is a copy of the six marked
/// sentences in <c>tests/fixtures/translation/marian-tokenizer.json</c>, and it is a copy on
/// purpose: a sidecar that reached into <c>tests/</c> would stop working the day the tree is
/// packaged. This is what stops the copy drifting.
/// </para>
/// <para>
/// Drift would not fail loudly. The parity reference beside those sources is the CPU's translations
/// <i>of them</i>, so changing one source without regenerating the reference makes every provider
/// fail the check for a reason that has nothing to do with the provider — and changing both would
/// quietly move what "parity" means.
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
    public void TheParityReferenceHasOneTranslationPerSource()
    {
        // A reference with the wrong number of entries fails every provider on a shape mismatch,
        // which reads as "this machine is wrong" when what is wrong is the fixture.
        var sources = ParitySources.GetProperty("sources").GetArrayLength();
        var translations = ParityReference.GetProperty("translations").GetArrayLength();

        Assert.Equal(sources, translations);
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
    }

    [Fact]
    public void EverySourceCarriesTheTargetToken()
    {
        // The host marks every source and the sidecar prepends nothing, so a fixture sentence
        // without the token is testing a path the product never takes — and this checkpoint given
        // Spanish with no target returns fluent German rather than an error.
        var token = TokenizerFixture.GetProperty("targetToken").GetString()!;

        foreach (var source in ParitySources.GetProperty("sources").EnumerateArray())
        {
            Assert.StartsWith(token, source.GetString()!, StringComparison.Ordinal);
        }
    }

    private static JsonElement TokenizerFixture { get; } = Read("tests", "fixtures", "translation", "marian-tokenizer.json");

    private static JsonElement ParitySources { get; } =
        Read("python", "uindosill_engines", "translator", "parity-sources.json");

    private static JsonElement ParityReference { get; } =
        Read("python", "uindosill_engines", "translator", "parity-reference.json");

    private static JsonElement Read(params string[] parts) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine([RepositoryRoot, .. parts]))).RootElement.Clone();

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
