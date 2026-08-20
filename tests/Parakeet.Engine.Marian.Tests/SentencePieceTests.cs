using System.Text;

namespace Parakeet.Engine.Marian.Tests;

/// <summary>
/// The tokenizer's machinery, against models this file builds byte by byte.
/// </summary>
/// <remarks>
/// <para>
/// The fixture test beside this one is the one that matters — it holds the whole tokenizer to the
/// ids HuggingFace really emitted — and it needs 3.06 MB of a checkpoint no clone carries, so it is
/// skipped on every machine but a measuring one. These do not: they write their own
/// <c>ModelProto</c>, which means CI exercises the protobuf reader, the proto2 defaults, the
/// double-array trie, the Unigram search, byte fallback and detokenisation on a machine with no
/// weights at all.
/// </para>
/// <para>
/// The models here are toys, and the two things they are built to prove are not. First, that the
/// search maximises a score rather than matching greedily: the same input segments two different
/// ways under two different score tables. Second, that a byte the vocabulary does not cover is
/// spelled out rather than dropped.
/// </para>
/// </remarks>
public sealed class SentencePieceTests
{
    // ------------------------------------------------------------------ protobuf, written by hand

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value > 0 ? (byte)(b | 0x80) : b);
        }
        while (value > 0);

        return [.. bytes];
    }

    private static byte[] Tag(int field, int wire) => Varint(((ulong)field << 3) | (uint)wire);

    private static byte[] Bytes(int field, byte[] payload) =>
        [.. Tag(field, 2), .. Varint((ulong)payload.Length), .. payload];

    private static byte[] Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    private static byte[] Bool(int field, bool value) => [.. Tag(field, 0), .. Varint(value ? 1u : 0u)];

    private static byte[] Float(int field, float value) =>
        [.. Tag(field, 5), .. BitConverter.GetBytes(value)];

    private static byte[] Piece(string piece, float score, SentencePieceType type) =>
        Bytes(1, [.. Text(1, piece), .. Float(2, score), .. Tag(3, 0), .. Varint((ulong)type)]);

    /// <summary>
    /// A model: the three reserved pieces, the 256 byte pieces, and whatever else is asked for.
    /// </summary>
    /// <param name="escapeWhitespaces">
    /// Null leaves the field out of the message entirely, which is how the real checkpoint ships it
    /// — and its proto2 default is <see langword="true"/>, not false.
    /// </param>
    private static SentencePieceModel Model(
        (string Piece, float Score)[] pieces,
        byte[]? charsMap = null,
        bool addDummyPrefix = true,
        bool removeExtraWhitespaces = true,
        bool? escapeWhitespaces = null,
        bool byteFallback = true)
    {
        var message = new List<byte>();
        message.AddRange(Piece("<unk>", 0f, SentencePieceType.Unknown));
        message.AddRange(Piece("<s>", 0f, SentencePieceType.Control));
        message.AddRange(Piece("</s>", 0f, SentencePieceType.Control));

        if (byteFallback)
        {
            for (var b = 0; b < 256; b++)
            {
                message.AddRange(Piece($"<0x{b:X2}>", 0f, SentencePieceType.Byte));
            }
        }

        foreach (var (piece, score) in pieces)
        {
            message.AddRange(Piece(piece, score, SentencePieceType.Normal));
        }

        var spec = new List<byte>();
        spec.AddRange(Text(1, "nmt_nfkc"));
        spec.AddRange(Bytes(2, charsMap ?? []));
        spec.AddRange(Bool(3, addDummyPrefix));
        spec.AddRange(Bool(4, removeExtraWhitespaces));
        if (escapeWhitespaces is { } escape)
        {
            spec.AddRange(Bool(5, escape));
        }

        // trainer_spec is field 2 and this reader has no use for it. Included anyway, holding a
        // field the reader has never heard of, because skipping by wire type rather than by field
        // number is the property that keeps a future SentencePiece release from breaking the read.
        message.AddRange(Bytes(2, [.. Text(1, "unused"), .. Tag(4242, 0), .. Varint(7)]));
        message.AddRange(Bytes(3, [.. spec]));

        return SentencePieceModel.Parse([.. message]);
    }

    // ------------------------------------------------------------------ the reader

    [Fact]
    public void TheReaderTakesThePiecesTheFlagsAndNothingElse()
    {
        var model = Model([("▁a", -1f), ("b", -2f)]);

        Assert.Equal("nmt_nfkc", model.NormalizerName);
        Assert.True(model.AddDummyPrefix);
        Assert.True(model.RemoveExtraWhitespaces);
        Assert.True(model.ByteFallback);

        var normal = model.Pieces.Where(p => p.Type == SentencePieceType.Normal).ToList();
        Assert.Equal(["▁a", "b"], normal.Select(p => p.Piece));
        Assert.Equal([-1f, -2f], normal.Select(p => p.Score));
        Assert.Equal(256, model.Pieces.Count(p => p.Type == SentencePieceType.Byte));
    }

    [Fact]
    public void AnAbsentEscapeWhitespacesIsTrueRatherThanFalse()
    {
        // proto2, and the real checkpoint simply does not carry the field. Reading a missing bool
        // as false would stop ▁ ever being written, and every word-initial piece in the real
        // vocabulary begins with one — so the whole sentence would come back as unknown bytes.
        Assert.True(Model([("▁a", -1f)]).EscapeWhitespaces);
        Assert.False(Model([("▁a", -1f)], escapeWhitespaces: false).EscapeWhitespaces);
    }

    [Fact]
    public void AModelWithNoPiecesIsRefusedRatherThanReturningAnEmptyVocabulary()
    {
        var exception = Assert.Throws<InvalidDataException>(() => SentencePieceModel.Parse([.. Text(9, "nothing")]));
        Assert.Contains("no SentencePiece pieces", exception.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the normaliser

    [Fact]
    public void SpacesBecomeTheSpaceSymbolAndTheStringGetsOneOnTheFront()
    {
        var processor = new SentencePieceProcessor(Model([("▁a", -1f), ("▁b", -1f)]));

        Assert.Equal(["▁a", "▁b"], processor.Encode("a b"));

        // Leading, trailing and repeated whitespace all collapse. This is also what quietly absorbs
        // the space left behind when '>>eng<<' is cut off the front of a marked source.
        Assert.Equal(["▁a", "▁b"], processor.Encode("   a    b   "));
    }

    [Fact]
    public void TheCompiledCharacterMapIsAppliedLongestMatchFirst()
    {
        // A one-key double-array trie, written out by hand in darts-clone's own unit encoding, so
        // that CI exercises the bit layout rather than only the fixture machine doing it. The key
        // is "a" and it normalises to "b".
        //
        //   units[0]  offset 1                    -> the root sends the walk to index 1
        //   units[96] label 0x61, leaf, offset 2  -> 1 ^ 0x61 = 96, and 96 ^ 2 = 98
        //   units[98] value 0                     -> byte offset of "b" in the replacement blob
        var units = new uint[99];
        units[0] = 1u << 10;
        units[96] = 0x61 | (1u << 8) | (2u << 10);
        units[98] = 0;

        var trie = new byte[units.Length * sizeof(uint)];
        Buffer.BlockCopy(units, 0, trie, 0, trie.Length);

        byte[] charsMap = [.. BitConverter.GetBytes((uint)trie.Length), .. trie, .. "b\0"u8];

        var processor = new SentencePieceProcessor(
            Model([("▁b", -1f), ("▁a", -1f)], charsMap: charsMap));

        // "a" is rewritten to "b" before the search ever sees it, so the piece that matches is ▁b.
        Assert.Equal(["▁b"], processor.Encode("a"));
    }

    // ------------------------------------------------------------------ the search

    [Fact]
    public void TheSearchTakesTheHighestScoringSegmentationRatherThanTheLongestPiece()
    {
        // Same input, same pieces, different scores, different answer. A tokenizer that matched
        // greedily — longest piece wins — would return the same thing both times, and would be
        // wrong on one of them.
        var whole = new SentencePieceProcessor(Model([("▁ab", -1.2f), ("▁a", -0.9f), ("b", -0.5f)]));
        Assert.Equal(["▁ab"], whole.Encode("ab"));

        var split = new SentencePieceProcessor(Model([("▁ab", -2.0f), ("▁a", -0.9f), ("b", -0.5f)]));
        Assert.Equal(["▁a", "b"], split.Encode("ab"));
    }

    [Fact]
    public void ACharacterNoPieceCoversIsSpelledOutInBytes()
    {
        // Byte fallback, and it is applied to a span the search has already given up on rather than
        // offered to it as a cheap alternative — the byte pieces are deliberately kept out of the
        // trie, so the search cannot prefer a pile of them to a real piece.
        var processor = new SentencePieceProcessor(Model([("▁", -5f), ("▁a", -1f)]));

        // 'z' has no piece; ▁ does, so the ▁ is a piece and the z is two hex bytes... one, here.
        Assert.Equal(["▁", "<0x7A>"], processor.Encode("z"));

        // A multi-byte character becomes each of its UTF-8 bytes, in order.
        Assert.Equal(["▁", "<0xC3>", "<0xA9>"], processor.Encode("é"));
    }

    [Fact]
    public void WithoutByteFallbackAnUncoveredCharacterIsOneUnknown()
    {
        var processor = new SentencePieceProcessor(Model([("▁", -5f)], byteFallback: false));

        Assert.Equal(["▁", "<unk>"], processor.Encode("z"));
    }

    [Fact]
    public void ControlAndUnknownPiecesAreNeverSegmentedTo()
    {
        // '</s>' is in the vocabulary as a control piece. If it were in the trie, this input would
        // tokenise to the end-of-sequence token in the middle of a sentence.
        var processor = new SentencePieceProcessor(Model([("▁", -0.1f)]));

        var pieces = processor.Encode("</s>");

        Assert.DoesNotContain("</s>", pieces);
        Assert.DoesNotContain("<unk>", pieces);
        Assert.All(pieces, piece => Assert.True(piece == "▁" || piece.StartsWith("<0x", StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ detokenisation

    [Fact]
    public void DecodingUndoesTheSpaceSymbolAndTheLeadingSpace()
    {
        var processor = new SentencePieceProcessor(Model([("▁a", -1f)]));

        Assert.Equal("hello world", processor.Decode(["▁hello", "▁world"]));
        Assert.Equal("ab", processor.Decode(["▁a", "b"]));
        Assert.Equal(string.Empty, processor.Decode([]));
    }

    [Fact]
    public void ARunOfBytePiecesDecodesAsOneCharacterRatherThanSeveral()
    {
        // Decoded together because they are the bytes of one character. One at a time, each would
        // be an invalid UTF-8 sequence and would come back as a replacement character.
        var processor = new SentencePieceProcessor(Model([("▁a", -1f)]));

        Assert.Equal("é", processor.Decode(["<0xC3>", "<0xA9>"]));
        Assert.Equal("a é b", processor.Decode(["▁a", "▁", "<0xC3>", "<0xA9>", "▁b"]));
    }

    [Fact]
    public void EncodingAndDecodingRoundTripThroughEachOther()
    {
        var processor = new SentencePieceProcessor(
            Model([("▁", -6f), ("▁the", -1f), ("▁cat", -1f), ("▁sat", -1f)]));

        const string Sentence = "the cat sat";
        Assert.Equal(Sentence, processor.Decode(processor.Encode(Sentence)));
    }
}
