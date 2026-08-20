using System.Text;

namespace Parakeet.Engine.Marian;

/// <summary>
/// SentencePiece's text normalisation, as the <c>.spm</c> file itself specifies it.
/// </summary>
/// <remarks>
/// <para>
/// A translation model reads the normalised form of its input and nothing else, so this runs before
/// every encode and its output is what the Viterbi pass sees. The rules are not chosen here: they
/// are compiled into the checkpoint as a Darts trie mapping byte sequences to replacement strings —
/// <c>nmt_nfkc</c> in this case — plus three flags. Reimplementing the rule set instead of reading
/// the table would be a second definition of the same thing, and the two would diverge the first
/// time a checkpoint moved.
/// </para>
/// <para>
/// <b>The three flags do more than they look like.</b> <c>escape_whitespaces</c> is what turns a
/// space into <c>▁</c>, and every piece in this vocabulary that starts a word begins with one, so
/// getting it wrong does not produce slightly different tokens — it produces a sentence with no
/// word boundaries in it at all. <c>add_dummy_prefix</c> puts a <c>▁</c> in front of the whole
/// string so the first word looks like every other word. <c>remove_extra_whitespaces</c> collapses
/// runs and trims both ends, which is also what quietly absorbs the space left behind when
/// <c>&gt;&gt;eng&lt;&lt;</c> is cut off the front of a marked source.
/// </para>
/// </remarks>
internal sealed class SentencePieceNormalizer
{
    /// <summary>U+2581 LOWER ONE EIGHTH BLOCK, which is SentencePiece's space.</summary>
    public const string SpaceSymbol = "▁";

    private static readonly byte[] SpaceSymbolBytes = Encoding.UTF8.GetBytes(SpaceSymbol);

    /// <summary>U+FFFD, what a malformed UTF-8 sequence normalises to.</summary>
    private static readonly byte[] ReplacementCharacter = Encoding.UTF8.GetBytes("�");

    private readonly DoubleArrayTrie _trie;
    private readonly byte[] _normalized;
    private readonly bool _addDummyPrefix;
    private readonly bool _removeExtraWhitespaces;
    private readonly bool _escapeWhitespaces;

    public SentencePieceNormalizer(SentencePieceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _addDummyPrefix = model.AddDummyPrefix;
        _removeExtraWhitespaces = model.RemoveExtraWhitespaces;
        _escapeWhitespaces = model.EscapeWhitespaces;

        var map = model.PrecompiledCharsMap;
        if (map.Length < sizeof(uint))
        {
            // A model with no table normalises nothing, which is a legitimate configuration; an
            // undersized one is a truncated file and is worth saying so about.
            if (map.Length != 0)
            {
                throw new InvalidDataException("The SentencePiece character map is shorter than its own header.");
            }

            _trie = DoubleArrayTrie.Empty;
            _normalized = [];
            return;
        }

        // [uint32 trie size][trie][null-terminated replacement strings]
        var trieBytes = BitConverter.ToUInt32(map, 0);
        if (trieBytes % sizeof(uint) != 0 || sizeof(uint) + trieBytes > (uint)map.Length)
        {
            throw new InvalidDataException(
                $"The SentencePiece character map declares a {trieBytes}-byte trie that does not fit its " +
                $"{map.Length} bytes.");
        }

        var units = new uint[trieBytes / sizeof(uint)];
        Buffer.BlockCopy(map, sizeof(uint), units, 0, (int)trieBytes);
        _trie = new DoubleArrayTrie(units);
        _normalized = map[(int)(sizeof(uint) + trieBytes)..];
    }

    /// <summary>
    /// Normalises <paramref name="text"/> and returns it as UTF-8.
    /// </summary>
    /// <remarks>
    /// Bytes rather than a string because that is what the encoder needs: the Viterbi pass advances
    /// one UTF-8 character at a time over bytes, and byte fallback splits a span it could not cover
    /// into the bytes it is made of. Handing back a string would mean encoding it again immediately.
    /// </remarks>
    public byte[] Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var input = Encoding.UTF8.GetBytes(text);
        if (input.Length == 0)
        {
            return [];
        }

        var output = new List<byte>(input.Length + SpaceSymbolBytes.Length);
        var at = 0;

        // Heading space, dropped before anything else so that the dummy prefix is the only leading
        // whitespace there is. This is what eats the space after '>>eng<<' once the code is cut.
        if (_removeExtraWhitespaces)
        {
            while (at < input.Length)
            {
                var (replacement, consumed) = NormalizePrefix(input, at);
                if (!IsSingleSpace(replacement))
                {
                    break;
                }

                at += consumed;
            }
        }

        if (at >= input.Length)
        {
            return [];
        }

        if (_addDummyPrefix)
        {
            AppendSpace(output);
        }

        var previousWasSpace = _removeExtraWhitespaces;
        while (at < input.Length)
        {
            var (replacement, consumed) = NormalizePrefix(input, at);
            at += consumed;

            var start = 0;

            // A replacement that begins with spaces, following something that ended with one,
            // contributes nothing: this is where runs of whitespace collapse.
            while (previousWasSpace && start < replacement.Length && replacement[start] == (byte)' ')
            {
                start++;
            }

            if (start < replacement.Length)
            {
                for (var i = start; i < replacement.Length; i++)
                {
                    if (_escapeWhitespaces && replacement[i] == (byte)' ')
                    {
                        output.AddRange(SpaceSymbolBytes);
                    }
                    else
                    {
                        output.Add(replacement[i]);
                    }
                }

                previousWasSpace = replacement[^1] == (byte)' ';
            }

            if (!_removeExtraWhitespaces)
            {
                previousWasSpace = false;
            }
        }

        if (_removeExtraWhitespaces)
        {
            var trailing = _escapeWhitespaces ? SpaceSymbolBytes : " "u8.ToArray();
            while (EndsWith(output, trailing))
            {
                output.RemoveRange(output.Count - trailing.Length, trailing.Length);
            }
        }

        return [.. output];
    }

    /// <summary>
    /// The longest table entry matching at <paramref name="at"/>, or the one character there when
    /// nothing matches.
    /// </summary>
    /// <remarks>
    /// Longest match rather than first: the table holds both single characters and multi-character
    /// sequences, and a shorter match winning would decompose a sequence the checkpoint expects to
    /// see whole. When nothing matches, exactly one UTF-8 character passes through unchanged — a
    /// malformed one becomes U+FFFD, because the alternative is a byte that no piece can cover and
    /// no downstream stage can name.
    /// </remarks>
    private (byte[] Replacement, int Consumed) NormalizePrefix(byte[] input, int at)
    {
        var (value, length) = _trie.LongestPrefix(input, at);
        if (length > 0)
        {
            return (ReadNormalized(value), length);
        }

        var characterLength = Utf8.CharacterLength(input[at]);
        if (characterLength <= 0 || at + characterLength > input.Length || !Utf8.IsWellFormed(input, at, characterLength))
        {
            return (ReplacementCharacter, 1);
        }

        return (input[at..(at + characterLength)], characterLength);
    }

    private byte[] ReadNormalized(int offset)
    {
        if (offset < 0 || offset >= _normalized.Length)
        {
            throw new InvalidDataException(
                $"The SentencePiece character map points at offset {offset}, past its {_normalized.Length} bytes " +
                "of replacements.");
        }

        var end = offset;
        while (end < _normalized.Length && _normalized[end] != 0)
        {
            end++;
        }

        return _normalized[offset..end];
    }

    private void AppendSpace(List<byte> output)
    {
        if (_escapeWhitespaces)
        {
            output.AddRange(SpaceSymbolBytes);
        }
        else
        {
            output.Add((byte)' ');
        }
    }

    private static bool IsSingleSpace(byte[] value) => value.Length == 1 && value[0] == (byte)' ';

    private static bool EndsWith(List<byte> output, byte[] suffix)
    {
        if (output.Count < suffix.Length)
        {
            return false;
        }

        for (var i = 0; i < suffix.Length; i++)
        {
            if (output[output.Count - suffix.Length + i] != suffix[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The Darts-clone double-array trie SentencePiece compiles its normalisation table into.
/// </summary>
/// <remarks>
/// A direct port of darts-clone's unit encoding, which is the only way to read the table: it is
/// stored as a flat array of 32-bit units whose bits carry a label, an offset, a leaf flag and a
/// value all at once, and there is no other representation of it in the file. The bit layout below
/// is that library's, not a choice made here.
/// </remarks>
internal sealed class DoubleArrayTrie
{
    private readonly uint[] _units;

    public DoubleArrayTrie(uint[] units)
    {
        ArgumentNullException.ThrowIfNull(units);
        _units = units;
    }

    public static DoubleArrayTrie Empty { get; } = new([]);

    /// <summary>
    /// The value and length of the longest key that matches <paramref name="input"/> at
    /// <paramref name="at"/>, or <c>(0, 0)</c> when none does.
    /// </summary>
    public (int Value, int Length) LongestPrefix(byte[] input, int at)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (_units.Length == 0)
        {
            return (0, 0);
        }

        var bestValue = 0;
        var bestLength = 0;

        var unit = _units[0];
        var id = 0u ^ Offset(unit);

        for (var i = at; i < input.Length; i++)
        {
            id ^= input[i];
            if (id >= _units.Length)
            {
                break;
            }

            unit = _units[id];
            if (Label(unit) != input[i])
            {
                break;
            }

            id ^= Offset(unit);
            if (HasLeaf(unit))
            {
                if (id >= _units.Length)
                {
                    break;
                }

                bestValue = (int)Value(_units[id]);
                bestLength = i - at + 1;
            }
        }

        return (bestValue, bestLength);
    }

    private static bool HasLeaf(uint unit) => ((unit >> 8) & 1) == 1;

    private static uint Value(uint unit) => unit & ((1u << 31) - 1);

    private static uint Label(uint unit) => unit & ((1u << 31) | 0xFF);

    private static uint Offset(uint unit) => (unit >> 10) << (int)((unit & (1u << 9)) >> 6);
}

/// <summary>UTF-8 arithmetic the encoder does often enough to want in one place.</summary>
internal static class Utf8
{
    /// <summary>
    /// How many bytes the character starting with <paramref name="lead"/> occupies, or 0 when it is
    /// not a lead byte.
    /// </summary>
    /// <remarks>
    /// SentencePiece advances the Viterbi one character at a time rather than one byte at a time,
    /// which is why this exists: a node boundary inside a multi-byte character would let the
    /// encoder split a character in half and then ask the vocabulary about the halves.
    /// </remarks>
    public static int CharacterLength(byte lead) => lead switch
    {
        < 0x80 => 1,
        < 0xC0 => 0,
        < 0xE0 => 2,
        < 0xF0 => 3,
        < 0xF8 => 4,
        _ => 0,
    };

    public static bool IsWellFormed(byte[] bytes, int at, int length)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        for (var i = at + 1; i < at + length; i++)
        {
            if (i >= bytes.Length || (bytes[i] & 0xC0) != 0x80)
            {
                return false;
            }
        }

        return true;
    }
}
