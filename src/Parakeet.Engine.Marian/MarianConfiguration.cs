using System.Globalization;
using System.Text.Json;

namespace Parakeet.Engine.Marian;

/// <summary>
/// The shape of the graphs and the decode they were scored with, read from the exported checkpoint.
/// </summary>
/// <remarks>
/// <para>
/// Two files, read for different reasons. <c>config.json</c> describes the graph — layers, heads,
/// width, vocabulary, and the token the decoder starts from — and every one of those has to match
/// the tensors the session is handed or ONNX Runtime refuses them, so reading it is cheaper than
/// hard-coding it and safer than assuming it.
/// </para>
/// <para>
/// <b><c>generation_config.json</c> is read and then deliberately not obeyed in one place.</b> It
/// says <c>num_beams: 4</c>. Nothing this project has measured used four: the spike, the export
/// smoke and the whole 8,149-sentence gate run passed six explicitly, and greedy was measured over
/// 44 real segments to drop content beam-6 keeps. A loop that reads that field and trusts it ships
/// a decode nobody scored — so the beam count comes from <see cref="MarianDecodeSettings"/>, which
/// defaults to six and says why, and the file's number is carried here only so a change to it is
/// visible rather than silent.
/// </para>
/// </remarks>
internal sealed record MarianConfiguration
{
    public required int DecoderLayers { get; init; }

    public required int DecoderAttentionHeads { get; init; }

    public required int ModelDimension { get; init; }

    /// <summary>Width of the decoder's output, and the vocabulary the search ranges over.</summary>
    public required int VocabularySize { get; init; }

    /// <summary>
    /// The token the decoder is primed with — <b>58433</b>, which is also the pad token and is also
    /// in <c>bad_words_ids</c>.
    /// </summary>
    /// <remarks>
    /// Three roles for one id, and each has a different consequence if confused. As the start token
    /// it must be the first thing in every decoder sequence. As the pad token it is what unfilled
    /// positions of the output buffer hold. As a banned word it must be impossible for the search
    /// to <i>emit</i> — which is not a contradiction with the first two, because the ban applies to
    /// the distribution the search picks from and the start token is never picked.
    /// </remarks>
    public required int DecoderStartTokenId { get; init; }

    public required int EndOfSequenceTokenId { get; init; }

    public required int PadTokenId { get; init; }

    /// <summary>
    /// <c>max_position_embeddings</c>: 1024 here, and <b>not</b> the number to design against.
    /// </summary>
    /// <remarks>
    /// The tokenizer declares <c>model_max_length</c> 512. The discrepancy is real and recorded in
    /// <c>docs/UNPROVEN.md</c> rather than resolved, and 512 is the smaller of the two, so 512 is
    /// what a source is refused against.
    /// </remarks>
    public required int MaxPositionEmbeddings { get; init; }

    /// <summary>Tokens whose probability is driven to zero at every step: <c>bad_words_ids</c>.</summary>
    /// <remarks>
    /// One entry here — the pad token — and it is a single-token ban rather than a phrase. Only
    /// single-token entries are honoured; a multi-token entry would need the running sequence's
    /// history to decide, and refusing to load one is better than ignoring it quietly.
    /// </remarks>
    public required IReadOnlyList<int> BadWordIds { get; init; }

    /// <summary>The token forced at the last position, or null. 430 here.</summary>
    public int? ForcedEndOfSequenceTokenId { get; init; }

    /// <summary>What <c>generation_config.json</c> asks for, recorded and not obeyed. See the remarks.</summary>
    public int? DeclaredBeams { get; init; }

    /// <summary>Head width, which every past-key-value tensor is shaped by.</summary>
    public int HeadDimension => ModelDimension / DecoderAttentionHeads;

    public static MarianConfiguration Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var configPath = Path.Combine(directory, "config.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"The model configuration is not at {configPath}.", configPath);
        }

        using var config = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = config.RootElement;

        var vocabulary = Optional(root, "decoder_vocab_size") ?? Required(root, "vocab_size", configPath);

        var declaredBeams = (int?)null;
        var badWords = new List<int>();
        int? forcedEos = null;

        var generationPath = Path.Combine(directory, "generation_config.json");
        if (File.Exists(generationPath))
        {
            using var generation = JsonDocument.Parse(File.ReadAllText(generationPath));
            var gen = generation.RootElement;

            declaredBeams = Optional(gen, "num_beams");
            forcedEos = Optional(gen, "forced_eos_token_id");

            if (gen.TryGetProperty("bad_words_ids", out var bad) && bad.ValueKind == JsonValueKind.Array)
            {
                foreach (var phrase in bad.EnumerateArray())
                {
                    if (phrase.ValueKind != JsonValueKind.Array || phrase.GetArrayLength() != 1)
                    {
                        throw new InvalidDataException(
                            $"{generationPath} bans a phrase of more than one token. This decoder honours only " +
                            "single-token bans, and ignoring the rest quietly would be a decode nobody scored.");
                    }

                    badWords.Add(phrase[0].GetInt32());
                }
            }
        }

        return new MarianConfiguration
        {
            DecoderLayers = Required(root, "decoder_layers", configPath),
            DecoderAttentionHeads = Required(root, "decoder_attention_heads", configPath),
            ModelDimension = Required(root, "d_model", configPath),
            VocabularySize = vocabulary,
            DecoderStartTokenId = Required(root, "decoder_start_token_id", configPath),
            EndOfSequenceTokenId = Required(root, "eos_token_id", configPath),
            PadTokenId = Required(root, "pad_token_id", configPath),
            MaxPositionEmbeddings = Optional(root, "max_position_embeddings") ?? 512,
            BadWordIds = badWords,
            ForcedEndOfSequenceTokenId = forcedEos,
            DeclaredBeams = declaredBeams,
        };
    }

    private static int Required(JsonElement root, string name, string where) =>
        Optional(root, name)
        ?? throw new InvalidDataException($"{where} has no '{name}', and the decoder cannot be shaped without it.");

    private static int? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{DecoderLayers}L x {DecoderAttentionHeads}H x {HeadDimension}, vocab {VocabularySize}");
}
