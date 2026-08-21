using System.Text.Json;

namespace Parakeet.Engine.Marian;

/// <summary>
/// HuggingFace's <c>MarianTokenizer</c>, in C#.
/// </summary>
/// <remarks>
/// <para>
/// Every chrF++ figure this project publishes for translation was produced by that Python class
/// feeding these graphs, so this is a port of a specific implementation rather than of the idea of
/// SentencePiece. What it reproduces is held to
/// <c>tests/fixtures/translation/marian-tokenizer.json</c>, which records the ids the original
/// emits for six fixed sentences at a named checkpoint revision, and — from 2026-08-20 — to the
/// 8,149 sources the gate run itself tokenised.
/// </para>
/// <para>
/// <b>Three things about it are easy to get wrong and produce plausible output when got wrong.</b>
/// </para>
/// <para>
/// <b>The target token is one token.</b> <c>&gt;&gt;eng&lt;&lt;</c> is vocabulary entry 693, not a
/// punctuation sequence to be segmented: it is cut off the front of the string <i>before</i>
/// SentencePiece sees anything, and looked up whole. A tokenizer that hands it to the Unigram
/// search gets a run of plausible ids back and has silently lost the target — and this checkpoint
/// given a source with no target token returns fluent German rather than an error, which nothing
/// downstream would catch.
/// </para>
/// <para>
/// <b>The Moses punctuation normaliser is not on this path.</b> <c>MarianTokenizer</c> builds one
/// and, in transformers 4.57.6, never calls it from <c>_tokenize</c> — checked in the installed
/// source on 2026-08-20 rather than assumed, and true whether or not <c>sacremoses</c> is
/// installed. Adding one here would change the ids.
/// </para>
/// <para>
/// <b>512 is the limit, not 1024.</b> <c>tokenizer_config.json</c> declares
/// <c>model_max_length</c> 512 while <c>config.json</c> says <c>max_position_embeddings</c> 1024.
/// The discrepancy is recorded in <c>docs/UNPROVEN.md</c> rather than resolved, and 512 is the
/// number designed against, so a source past it is refused rather than truncated.
/// </para>
/// </remarks>
internal sealed class MarianTokenizer
{
    private readonly SentencePieceProcessor _source;
    private readonly SentencePieceProcessor _target;
    private readonly Dictionary<string, int> _encoder;
    private readonly Dictionary<int, string> _decoder;
    private readonly HashSet<int> _specialIds;

    private MarianTokenizer(
        SentencePieceProcessor source,
        SentencePieceProcessor target,
        Dictionary<string, int> encoder,
        Dictionary<int, string> decoder,
        int unknownId,
        int endOfSequenceId,
        int padId,
        int maxLength)
    {
        _source = source;
        _target = target;
        _encoder = encoder;
        _decoder = decoder;
        UnknownId = unknownId;
        EndOfSequenceId = endOfSequenceId;
        PadId = padId;
        MaxLength = maxLength;
        _specialIds = [unknownId, endOfSequenceId, padId];
    }

    public int UnknownId { get; }

    public int EndOfSequenceId { get; }

    public int PadId { get; }

    /// <summary>Entries in <c>vocab.json</c>, which is also the decoder's output width.</summary>
    public int VocabularySize => _encoder.Count;

    /// <summary><c>model_max_length</c>: the longest source, in these tokens, including the eos.</summary>
    public int MaxLength { get; }

    /// <summary>
    /// Loads the five tokenizer files of an exported checkpoint directory.
    /// </summary>
    /// <remarks>
    /// Five files rather than one, and all five are required: <c>source.spm</c> and
    /// <c>target.spm</c> segment the two sides, <c>vocab.json</c> is what turns a piece into the id
    /// the graph reads — the <c>.spm</c> files have their own indices and they are <b>not</b> the
    /// model's — and the two config files carry the special tokens and the length limit.
    /// </remarks>
    public static MarianTokenizer Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var source = new SentencePieceProcessor(SentencePieceModel.Load(Path.Combine(directory, "source.spm")));
        var target = new SentencePieceProcessor(SentencePieceModel.Load(Path.Combine(directory, "target.spm")));

        var vocabularyPath = Path.Combine(directory, "vocab.json");
        if (!File.Exists(vocabularyPath))
        {
            throw new FileNotFoundException($"The tokenizer vocabulary is not at {vocabularyPath}.", vocabularyPath);
        }

        var encoder = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var vocabulary = JsonDocument.Parse(File.ReadAllText(vocabularyPath)))
        {
            foreach (var entry in vocabulary.RootElement.EnumerateObject())
            {
                encoder[entry.Name] = entry.Value.GetInt32();
            }
        }

        // Last writer wins on a duplicated id, exactly as building a reverse dict in Python does.
        var decoder = new Dictionary<int, string>(encoder.Count);
        foreach (var (piece, id) in encoder)
        {
            decoder[id] = piece;
        }

        var (unknown, endOfSequence, pad, maxLength) = ReadConfiguration(directory);

        return new MarianTokenizer(
            source,
            target,
            encoder,
            decoder,
            Require(encoder, unknown, vocabularyPath),
            Require(encoder, endOfSequence, vocabularyPath),
            Require(encoder, pad, vocabularyPath),
            maxLength);
    }

    /// <summary>
    /// Tokenises one marked source into the ids the encoder graph reads, ending with the eos.
    /// </summary>
    public IReadOnlyList<int> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var ids = new List<int>();
        var (code, rest) = RemoveLanguageCode(text);

        if (code is not null)
        {
            ids.Add(_encoder.TryGetValue(code, out var codeId) ? codeId : UnknownId);
        }

        foreach (var piece in _source.Encode(rest))
        {
            ids.Add(_encoder.TryGetValue(piece, out var id) ? id : UnknownId);
        }

        ids.Add(EndOfSequenceId);
        return ids;
    }

    /// <summary>The pieces <see cref="Encode"/> produced ids for, for a fixture to compare.</summary>
    public IReadOnlyList<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var tokens = new List<string>();
        var (code, rest) = RemoveLanguageCode(text);
        if (code is not null)
        {
            tokens.Add(code);
        }

        tokens.AddRange(_source.Encode(rest));
        tokens.Add(IdToToken(EndOfSequenceId));
        return tokens;
    }

    /// <summary>
    /// Turns decoder output back into text, dropping the special tokens.
    /// </summary>
    /// <remarks>
    /// The <b>target</b> SentencePiece model detokenises, which is what <c>MarianTokenizer</c> does
    /// unless a caller explicitly asks for the source one. It matters for byte fallback and for
    /// nothing else here, but "which side is this" is exactly the sort of thing that is invisible
    /// until it is wrong.
    /// </remarks>
    public string Decode(IReadOnlyList<int> ids, bool skipSpecialTokens = true)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var tokens = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            if (skipSpecialTokens && _specialIds.Contains(id))
            {
                continue;
            }

            tokens.Add(IdToToken(id));
        }

        return _target.Decode(tokens);
    }

    public string IdToToken(int id) => _decoder.TryGetValue(id, out var token) ? token : IdToToken(UnknownId);

    /// <summary>
    /// Splits a leading <c>&gt;&gt;xxx&lt;&lt;</c> off the front, exactly as
    /// <c>MarianTokenizer.remove_language_code</c> does.
    /// </summary>
    /// <remarks>
    /// The rule is the Python one to the letter: the string must start with <c>&gt;&gt;</c>, and
    /// the code runs to the <b>first</b> <c>&lt;&lt;</c> anywhere after it. It is not a regular
    /// expression over the whole string and it does not check that what is between them is a
    /// language — it is a prefix rule, and reimplementing it as anything smarter would tokenise
    /// some strings differently from the reference.
    /// </remarks>
    private static (string? Code, string Text) RemoveLanguageCode(string text)
    {
        if (!text.StartsWith(">>", StringComparison.Ordinal))
        {
            return (null, text);
        }

        var end = text.IndexOf("<<", StringComparison.Ordinal);
        return end < 0 ? (null, text) : (text[..(end + 2)], text[(end + 2)..]);
    }

    private static (string Unknown, string EndOfSequence, string Pad, int MaxLength) ReadConfiguration(string directory)
    {
        var unknown = "<unk>";
        var endOfSequence = "</s>";
        var pad = "<pad>";
        var maxLength = 512;

        var path = Path.Combine(directory, "tokenizer_config.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The tokenizer configuration is not at {path}.", path);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        unknown = ReadToken(root, "unk_token") ?? unknown;
        endOfSequence = ReadToken(root, "eos_token") ?? endOfSequence;
        pad = ReadToken(root, "pad_token") ?? pad;

        if (root.TryGetProperty("model_max_length", out var declared)
            && declared.ValueKind == JsonValueKind.Number
            && declared.TryGetInt32(out var value)
            && value > 0)
        {
            maxLength = value;
        }

        return (unknown, endOfSequence, pad, maxLength);
    }

    /// <summary>
    /// A special token as written either way round: a bare string, or the dictionary form
    /// <c>{"content": "&lt;/s&gt;", ...}</c> that a saved <c>AddedToken</c> becomes.
    /// </summary>
    private static string? ReadToken(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("content", out var content) => content.GetString(),
            _ => null,
        };
    }

    private static int Require(Dictionary<string, int> encoder, string token, string where) =>
        encoder.TryGetValue(token, out var id)
            ? id
            : throw new InvalidDataException($"'{token}' is not in {where}, and this tokenizer cannot work without it.");
}
