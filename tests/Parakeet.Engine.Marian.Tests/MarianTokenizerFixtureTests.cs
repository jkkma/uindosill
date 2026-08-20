using System.Text.Json;

namespace Parakeet.Engine.Marian.Tests;

/// <summary>
/// The C# tokenizer against the ids HuggingFace's <c>MarianTokenizer</c> actually emitted.
/// </summary>
/// <remarks>
/// <para>
/// <c>tests/fixtures/translation/marian-tokenizer.json</c> was committed on 2026-08-20 with nothing
/// reading it, so that the decode loop would be written against a fixed target rather than against
/// whatever the first C# implementation happened to produce. This is the thing that reads it.
/// </para>
/// <para>
/// Six sentences is a start and not a proof, which is why the same tokenizer is also held to the
/// 8,149 sources the gate run tokenised — see <c>scripts/measure-translation-agreement.ps1</c>.
/// A fixture proves the shape; a corpus proves the tail.
/// </para>
/// </remarks>
public sealed class MarianTokenizerFixtureTests
{
    [Fact]
    public void ReproducesTheRecordedIds()
    {
        var checkpoint = Fixtures.TokenizerCheckpoint();
        Assert.SkipWhen(
            checkpoint is null,
            "No exported checkpoint: this needs source.spm, target.spm and vocab.json, which are 3.06 MB of a " +
            "1.34 GiB artefact this repository does not carry. Set UINDOSILL_TRANSLATION_MODEL to a directory " +
            "holding them.");

        var tokenizer = MarianTokenizer.Load(checkpoint!);
        using var fixture = Fixtures.TokenizerFixture();
        var root = fixture.RootElement;

        // The fixture names its own vocabulary and special tokens. Checking them first means a
        // moved checkpoint reports as a moved checkpoint rather than as thousands of wrong ids.
        Assert.Equal(root.GetProperty("vocabSize").GetInt32(), tokenizer.VocabularySize);
        Assert.Equal(root.GetProperty("eosTokenId").GetInt32(), tokenizer.EndOfSequenceId);
        Assert.Equal(root.GetProperty("padTokenId").GetInt32(), tokenizer.PadId);
        Assert.Equal(root.GetProperty("unkTokenId").GetInt32(), tokenizer.UnknownId);
        Assert.Equal(root.GetProperty("modelMaxLength").GetInt32(), tokenizer.MaxLength);

        var cases = root.GetProperty("cases").EnumerateArray().ToList();
        Assert.NotEmpty(cases);

        foreach (var entry in cases)
        {
            var marked = entry.GetProperty("markedSource").GetString()!;
            var expectedIds = entry.GetProperty("inputIds").EnumerateArray().Select(v => v.GetInt32()).ToList();
            var expectedTokens = entry.GetProperty("tokens").EnumerateArray().Select(v => v.GetString()!).ToList();

            var ids = tokenizer.Encode(marked);
            var tokens = tokenizer.Tokenize(marked);

            Assert.Equal(expectedTokens, tokens);
            Assert.Equal(expectedIds, ids);
        }
    }

    /// <summary>
    /// The trap the fixture's own README calls out first: <c>&gt;&gt;eng&lt;&lt;</c> is one token.
    /// </summary>
    /// <remarks>
    /// Asserted separately from the round trip because a tokenizer that takes it apart still
    /// produces plausible ids, and the sentence still translates — into German, which is this
    /// checkpoint's first declared target and not what anybody asked for.
    /// </remarks>
    [Fact]
    public void TargetTokenIsOneToken()
    {
        var checkpoint = Fixtures.TokenizerCheckpoint();
        Assert.SkipWhen(checkpoint is null, "Set UINDOSILL_TRANSLATION_MODEL; see the round-trip test.");

        var tokenizer = MarianTokenizer.Load(checkpoint!);
        using var fixture = Fixtures.TokenizerFixture();

        var targetToken = fixture.RootElement.GetProperty("targetToken").GetString()!;
        var first = fixture.RootElement.GetProperty("cases")[0];

        var marked = tokenizer.Encode(first.GetProperty("markedSource").GetString()!);
        var bare = tokenizer.Encode(first.GetProperty("source").GetString()!);

        Assert.Equal(bare.Count + 1, marked.Count);
        Assert.Equal(693, marked[0]);
        Assert.Equal(targetToken, tokenizer.IdToToken(marked[0]));
        Assert.Equal(bare, marked.Skip(1).ToList());
    }

    /// <summary>Decoding the recorded ids returns the recorded string.</summary>
    [Fact]
    public void DecodesBackToTheRecordedText()
    {
        var checkpoint = Fixtures.TokenizerCheckpoint();
        Assert.SkipWhen(checkpoint is null, "Set UINDOSILL_TRANSLATION_MODEL; see the round-trip test.");

        var tokenizer = MarianTokenizer.Load(checkpoint!);
        using var fixture = Fixtures.TokenizerFixture();

        foreach (var entry in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var ids = entry.GetProperty("inputIds").EnumerateArray().Select(v => v.GetInt32()).ToList();
            Assert.Equal(entry.GetProperty("decoded").GetString(), tokenizer.Decode(ids));
        }
    }
}
