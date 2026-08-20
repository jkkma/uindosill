namespace Parakeet.Engine.Marian;

/// <summary>What a piece is, as SentencePiece's own <c>ModelProto</c> spells it.</summary>
/// <remarks>
/// The numbers are the protobuf enum's and are load-bearing rather than decorative: only
/// <see cref="Normal"/>, <see cref="UserDefined"/> and <see cref="Unused"/> go into the trie the
/// encoder walks, and <see cref="Byte"/> deliberately does not — byte fallback is applied to a span
/// the encoder has already given up on, not offered to it as a cheap alternative.
/// </remarks>
internal enum SentencePieceType
{
    Normal = 1,
    Unknown = 2,
    Control = 3,
    UserDefined = 4,
    Unused = 5,
    Byte = 6,
}

/// <summary>One entry of a SentencePiece vocabulary.</summary>
internal readonly record struct SentencePiece(string Piece, float Score, SentencePieceType Type);

/// <summary>
/// A <c>.spm</c> file, read far enough to encode with and no further.
/// </summary>
/// <remarks>
/// <para>
/// This is a hand-written reader for the three protobuf fields that matter — the pieces, and the
/// normaliser's flags and character map — rather than a generated one, because generating it would
/// mean a protobuf package in <c>Directory.Packages.props</c>, which is pinned to the toolchain the
/// translation gate was scored against. The field numbers are not guessed: they were read off
/// <c>source.spm</c> itself on 2026-08-20, and the reader refuses a file whose shape it does not
/// recognise rather than quietly returning an empty vocabulary.
/// </para>
/// <para>
/// <b>Two defaults are proto2 defaults and absence is meaningful.</b> <c>escape_whitespaces</c> is
/// simply not present in this checkpoint's <c>normalizer_spec</c>, and its proto2 default is
/// <see langword="true"/>; reading a missing bool as false would stop <c>▁</c> ever being written
/// and every piece in the vocabulary begins with one. <c>add_dummy_prefix</c> is the same shape of
/// trap and happens to be present here.
/// </para>
/// </remarks>
internal sealed class SentencePieceModel
{
    private SentencePieceModel(
        IReadOnlyList<SentencePiece> pieces,
        string normalizerName,
        byte[] precompiledCharsMap,
        bool addDummyPrefix,
        bool removeExtraWhitespaces,
        bool escapeWhitespaces)
    {
        Pieces = pieces;
        NormalizerName = normalizerName;
        PrecompiledCharsMap = precompiledCharsMap;
        AddDummyPrefix = addDummyPrefix;
        RemoveExtraWhitespaces = removeExtraWhitespaces;
        EscapeWhitespaces = escapeWhitespaces;
    }

    public IReadOnlyList<SentencePiece> Pieces { get; }

    /// <summary><c>nmt_nfkc</c> for this checkpoint. Recorded so a changed rule set is visible.</summary>
    public string NormalizerName { get; }

    /// <summary>The compiled normalisation table: a Darts trie and the strings it points into.</summary>
    public byte[] PrecompiledCharsMap { get; }

    public bool AddDummyPrefix { get; }

    public bool RemoveExtraWhitespaces { get; }

    public bool EscapeWhitespaces { get; }

    /// <summary>
    /// True when the vocabulary carries the 256 <c>&lt;0xNN&gt;</c> pieces, which is how a
    /// byte-fallback model is recognised without reading <c>trainer_spec</c>.
    /// </summary>
    /// <remarks>
    /// Inferred rather than read because the flag and the pieces have to agree for byte fallback to
    /// mean anything: a model claiming the flag with no byte pieces would emit ids that are not in
    /// its own vocabulary. The pieces are the thing that has to be there, so the pieces are what is
    /// asked.
    /// </remarks>
    public bool ByteFallback { get; private set; }

    public static SentencePieceModel Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The SentencePiece model is not at {path}.", path);
        }

        return Parse(File.ReadAllBytes(path), path);
    }

    public static SentencePieceModel Parse(byte[] bytes, string what = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var pieces = new List<SentencePiece>();
        var normalizerName = string.Empty;
        var charsMap = Array.Empty<byte>();

        // proto2 defaults. Absent is not false: see the type's remarks.
        var addDummyPrefix = true;
        var removeExtraWhitespaces = true;
        var escapeWhitespaces = true;

        var reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field, wire)
            {
                case (1, ProtoReader.LengthDelimited):
                    pieces.Add(ParsePiece(reader.ReadLengthDelimited(), what));
                    break;

                case (3, ProtoReader.LengthDelimited):
                    var spec = new ProtoReader(reader.ReadLengthDelimited());
                    while (spec.TryReadTag(out var specField, out var specWire))
                    {
                        switch (specField, specWire)
                        {
                            case (1, ProtoReader.LengthDelimited):
                                normalizerName = ProtoReader.Utf8(spec.ReadLengthDelimited());
                                break;
                            case (2, ProtoReader.LengthDelimited):
                                charsMap = spec.ReadLengthDelimited().ToArray();
                                break;
                            case (3, ProtoReader.Varint):
                                addDummyPrefix = spec.ReadVarint() != 0;
                                break;
                            case (4, ProtoReader.Varint):
                                removeExtraWhitespaces = spec.ReadVarint() != 0;
                                break;
                            case (5, ProtoReader.Varint):
                                escapeWhitespaces = spec.ReadVarint() != 0;
                                break;
                            default:
                                spec.SkipField(specWire);
                                break;
                        }
                    }

                    break;

                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (pieces.Count == 0)
        {
            throw new InvalidDataException(
                $"{what} carries no SentencePiece pieces. Either it is not a SentencePiece model or its layout " +
                "has moved, and both are things to find out about rather than encode around.");
        }

        var model = new SentencePieceModel(
            pieces, normalizerName, charsMap, addDummyPrefix, removeExtraWhitespaces, escapeWhitespaces)
        {
            ByteFallback = pieces.Any(p => p.Type == SentencePieceType.Byte),
        };

        return model;
    }

    private static SentencePiece ParsePiece(ReadOnlySpan<byte> bytes, string what)
    {
        string? piece = null;
        var score = 0f;
        var type = SentencePieceType.Normal;

        var reader = new ProtoReader(bytes);
        while (reader.TryReadTag(out var field, out var wire))
        {
            switch (field, wire)
            {
                case (1, ProtoReader.LengthDelimited):
                    piece = ProtoReader.Utf8(reader.ReadLengthDelimited());
                    break;
                case (2, ProtoReader.Fixed32):
                    score = BitConverter.Int32BitsToSingle(reader.ReadFixed32());
                    break;
                case (3, ProtoReader.Varint):
                    type = (SentencePieceType)reader.ReadVarint();
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        return new SentencePiece(
            piece ?? throw new InvalidDataException($"{what} has a piece with no text."), score, type);
    }
}

/// <summary>
/// Enough of the protobuf wire format to walk a <c>ModelProto</c>: tags, varints, fixed32 and
/// length-delimited fields, with everything else skipped by its wire type.
/// </summary>
/// <remarks>
/// Skipping by wire type rather than by field number is what lets this ignore <c>trainer_spec</c>
/// — a message with dozens of fields this code has no use for — without knowing anything about its
/// contents, and what keeps a future SentencePiece release adding a field from breaking the read.
/// </remarks>
internal ref struct ProtoReader(ReadOnlySpan<byte> bytes)
{
    public const int Varint = 0;
    public const int Fixed64 = 1;
    public const int LengthDelimited = 2;
    public const int Fixed32 = 5;

    private readonly ReadOnlySpan<byte> _bytes = bytes;
    private int _position;

    public bool TryReadTag(out int field, out int wire)
    {
        if (_position >= _bytes.Length)
        {
            field = 0;
            wire = 0;
            return false;
        }

        var tag = ReadVarint();
        field = (int)(tag >> 3);
        wire = (int)(tag & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (_position >= _bytes.Length)
            {
                throw new InvalidDataException("Truncated varint in a SentencePiece model.");
            }

            var b = _bytes[_position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidDataException("Over-long varint in a SentencePiece model.");
            }
        }
    }

    public int ReadFixed32()
    {
        if (_position + 4 > _bytes.Length)
        {
            throw new InvalidDataException("Truncated fixed32 in a SentencePiece model.");
        }

        var value = BitConverter.ToInt32(_bytes[_position..(_position + 4)]);
        _position += 4;
        return value;
    }

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        var length = (int)ReadVarint();
        if (length < 0 || _position + length > _bytes.Length)
        {
            throw new InvalidDataException("Truncated length-delimited field in a SentencePiece model.");
        }

        var slice = _bytes.Slice(_position, length);
        _position += length;
        return slice;
    }

    public void SkipField(int wire)
    {
        switch (wire)
        {
            case Varint:
                ReadVarint();
                break;
            case Fixed64:
                _position += 8;
                break;
            case LengthDelimited:
                ReadLengthDelimited();
                break;
            case Fixed32:
                _position += 4;
                break;
            default:
                throw new InvalidDataException($"Unknown protobuf wire type {wire} in a SentencePiece model.");
        }

        if (_position > _bytes.Length)
        {
            throw new InvalidDataException("Truncated field in a SentencePiece model.");
        }
    }

    public static string Utf8(ReadOnlySpan<byte> bytes) => System.Text.Encoding.UTF8.GetString(bytes);
}
