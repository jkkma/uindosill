using System.Globalization;
using System.Text;

namespace Parakeet.Engine.Marian;

/// <summary>
/// Encodes text into SentencePiece pieces with the Unigram model's own Viterbi search, and turns
/// pieces back into text.
/// </summary>
/// <remarks>
/// <para>
/// Unigram, not BPE: this checkpoint's <c>.spm</c> declares <c>model_type</c> 1, and the two
/// segment the same string differently. The search is the one SentencePiece runs — best total score
/// over a path of pieces, where each piece carries a log probability from the file — and its two
/// details that look like implementation and are not: the path advances one <b>character</b> at a
/// time so a node can never land inside a multi-byte character, and a character no piece covers
/// costs <c>min_score − 10</c>, which is the penalty that makes an unknown expensive without making
/// it impossible.
/// </para>
/// <para>
/// <b>Byte fallback is applied after the search, not inside it.</b> The 256 <c>&lt;0xNN&gt;</c>
/// pieces are deliberately kept out of the trie — SentencePiece keeps them out of its own — so the
/// search cannot prefer a cheap pile of bytes to a real piece; only a span the search has already
/// given up on is spelled out in bytes.
/// </para>
/// </remarks>
internal sealed class SentencePieceProcessor
{
    /// <summary>What an unknown character costs, from SentencePiece's <c>kUnkPenalty</c>.</summary>
    private const float UnknownPenalty = 10.0f;

    private readonly SentencePieceModel _model;
    private readonly SentencePieceNormalizer _normalizer;
    private readonly PieceTrie _trie;
    private readonly float _unknownScore;
    private readonly string _unknownPiece;
    private readonly string[] _bytePieces = new string[256];

    public SentencePieceProcessor(SentencePieceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        _model = model;
        _normalizer = new SentencePieceNormalizer(model);
        _trie = new PieceTrie(model.Pieces);

        var minimum = float.MaxValue;
        var unknown = "<unk>";
        for (var i = 0; i < model.Pieces.Count; i++)
        {
            var piece = model.Pieces[i];
            if (piece.Type == SentencePieceType.Normal)
            {
                minimum = Math.Min(minimum, piece.Score);
            }
            else if (piece.Type == SentencePieceType.Unknown)
            {
                unknown = piece.Piece;
            }
            else if (piece.Type == SentencePieceType.Byte)
            {
                var value = ParseBytePiece(piece.Piece);
                if (value >= 0)
                {
                    _bytePieces[value] = piece.Piece;
                }
            }
        }

        _unknownPiece = unknown;
        _unknownScore = (minimum == float.MaxValue ? 0f : minimum) - UnknownPenalty;
    }

    /// <summary>Normalises and segments <paramref name="text"/>, in the model's own pieces.</summary>
    public IReadOnlyList<string> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = _normalizer.Normalize(text);
        if (bytes.Length == 0)
        {
            return [];
        }

        var path = Viterbi(bytes);
        var pieces = new List<string>(path.Count);

        foreach (var (index, start, length) in path)
        {
            if (index >= 0)
            {
                pieces.Add(_model.Pieces[index].Piece);
                continue;
            }

            // Unknown. Spelled out in bytes when the vocabulary has them, and named as unknown when
            // it does not — either way the span is accounted for rather than dropped.
            if (_model.ByteFallback)
            {
                for (var i = start; i < start + length; i++)
                {
                    pieces.Add(_bytePieces[bytes[i]] ?? _unknownPiece);
                }
            }
            else
            {
                pieces.Add(_unknownPiece);
            }
        }

        return pieces;
    }

    /// <summary>
    /// The best-scoring segmentation of <paramref name="bytes"/>, as spans over it.
    /// </summary>
    /// <remarks>
    /// Index −1 marks a span no piece covered. The forward pass records, for every character
    /// boundary, the best-scoring way to arrive there and which span arrived; the backward pass
    /// walks that chain from the end. Every boundary is reachable because a boundary with no piece
    /// ending on it still gets the unknown span, so the walk cannot strand.
    /// </remarks>
    private List<(int Index, int Start, int Length)> Viterbi(byte[] bytes)
    {
        var size = bytes.Length;
        var bestScore = new float[size + 1];
        var bestStart = new int[size + 1];
        var bestIndex = new int[size + 1];
        Array.Fill(bestStart, -1);
        Array.Fill(bestIndex, -1);

        var at = 0;
        while (at < size)
        {
            var scoreSoFar = bestScore[at];
            var characterLength = Math.Min(Math.Max(Utf8.CharacterLength(bytes[at]), 1), size - at);
            var coveredWholeCharacter = false;

            foreach (var (index, length) in _trie.Matches(bytes, at))
            {
                if (_model.Pieces[index].Type == SentencePieceType.Unused)
                {
                    continue;
                }

                var end = at + length;
                var candidate = _model.Pieces[index].Score + scoreSoFar;
                if (bestStart[end] == -1 || candidate > bestScore[end])
                {
                    bestScore[end] = candidate;
                    bestStart[end] = at;
                    bestIndex[end] = index;
                }

                if (length == characterLength)
                {
                    coveredWholeCharacter = true;
                }
            }

            if (!coveredWholeCharacter)
            {
                var end = at + characterLength;
                var candidate = _unknownScore + scoreSoFar;
                if (bestStart[end] == -1 || candidate > bestScore[end])
                {
                    bestScore[end] = candidate;
                    bestStart[end] = at;
                    bestIndex[end] = -1;
                }
            }

            at += characterLength;
        }

        var path = new List<(int, int, int)>();
        var position = size;
        while (position > 0)
        {
            var start = bestStart[position];
            if (start < 0)
            {
                throw new InvalidOperationException(
                    "The SentencePiece search left a gap, which cannot happen while every character has an " +
                    "unknown fallback. The vocabulary or the normaliser is not the one this was written against.");
            }

            path.Add((bestIndex[position], start, position - start));
            position = start;
        }

        path.Reverse();
        return path;
    }

    /// <summary>Turns pieces back into text, undoing <c>▁</c> and byte fallback.</summary>
    /// <remarks>
    /// A run of <c>&lt;0xNN&gt;</c> pieces is decoded together rather than one at a time: they are
    /// the bytes of one character, and decoding each on its own would produce a replacement
    /// character per byte instead of the character they spell.
    /// </remarks>
    public string Decode(IEnumerable<string> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        var text = new StringBuilder();
        var pending = new List<byte>();

        void FlushBytes()
        {
            if (pending.Count > 0)
            {
                text.Append(Encoding.UTF8.GetString([.. pending]));
                pending.Clear();
            }
        }

        foreach (var piece in pieces)
        {
            var value = ParseBytePiece(piece);
            if (value >= 0 && _model.ByteFallback)
            {
                pending.Add((byte)value);
                continue;
            }

            FlushBytes();
            text.Append(piece);
        }

        FlushBytes();
        return text.Replace(SentencePieceNormalizer.SpaceSymbol, " ").ToString().Trim();
    }

    /// <summary>The byte a <c>&lt;0xNN&gt;</c> piece stands for, or −1 when it is not one.</summary>
    private static int ParseBytePiece(string piece)
    {
        if (piece.Length != 6
            || !piece.StartsWith("<0x", StringComparison.Ordinal)
            || piece[5] != '>')
        {
            return -1;
        }

        return int.TryParse(piece.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;
    }
}

/// <summary>
/// The pieces a Unigram search may use, keyed by their bytes.
/// </summary>
/// <remarks>
/// <para>
/// A byte trie, because the search asks the same question at every position — "which pieces start
/// here, and how long is each" — and a dictionary of whole strings would answer it by building a
/// candidate substring per length per position.
/// </para>
/// <para>
/// <b>Control, unknown and byte pieces are not in it.</b> That is SentencePiece's rule and it
/// matters: a search that could spend <c>&lt;/s&gt;</c> or <c>&lt;0x41&gt;</c> as ordinary pieces
/// would emit them in the middle of a sentence, where the first ends the sequence and the second
/// spells a letter the vocabulary already has a piece for.
/// </para>
/// </remarks>
internal sealed class PieceTrie
{
    private readonly Dictionary<int, int> _transitions = [];
    private readonly List<int> _values = [-1];

    public PieceTrie(IReadOnlyList<SentencePiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            if (piece.Type is not (SentencePieceType.Normal or SentencePieceType.UserDefined or SentencePieceType.Unused))
            {
                continue;
            }

            var node = 0;
            foreach (var b in Encoding.UTF8.GetBytes(piece.Piece))
            {
                var key = (node << 8) | b;
                if (!_transitions.TryGetValue(key, out var next))
                {
                    next = _values.Count;
                    _values.Add(-1);
                    _transitions[key] = next;
                }

                node = next;
            }

            // First writer wins, which mirrors SentencePiece refusing a duplicate piece outright.
            if (_values[node] < 0)
            {
                _values[node] = index;
            }
        }
    }

    /// <summary>Every piece that matches at <paramref name="at"/>, shortest first.</summary>
    public IEnumerable<(int Index, int Length)> Matches(byte[] bytes, int at)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var node = 0;
        for (var i = at; i < bytes.Length; i++)
        {
            if (!_transitions.TryGetValue((node << 8) | bytes[i], out node))
            {
                yield break;
            }

            var value = _values[node];
            if (value >= 0)
            {
                yield return (value, i - at + 1);
            }
        }
    }
}
