using Microsoft.ML.OnnxRuntime;

namespace Parakeet.Engine.Sortformer;

/// <summary>
/// The ONNX graph, and the only file in this project that knows ONNX Runtime exists.
/// </summary>
/// <remarks>
/// <para>
/// One call consumes 3 048 mel frames — about 30 seconds of new audio plus 3.2 s of lookahead — and
/// returns per-frame speaker activity over <c>[cache | FIFO | chunk]</c>, the chunk's pre-encoder
/// embeddings, and how many of those embeddings are real. It does <b>not</b> update the cache or the
/// FIFO: they go in as inputs and the host decides what goes back. See
/// <see cref="ArrivalOrderSpeakerCache"/> for the half the graph does not do.
/// </para>
/// <para>
/// Every shape is static, so every buffer is allocated once, wrapped in an <c>OrtValue</c> once —
/// which pins it for the session's lifetime — and refilled in place. What the graph is fed and what
/// it writes back never move.
/// </para>
/// </remarks>
internal sealed class SortformerModel : IDisposable
{
    private static readonly string[] InputNames =
    [
        "chunk", "chunk_lengths", "spkcache", "spkcache_lengths", "fifo", "fifo_lengths",
    ];

    private static readonly string[] OutputNames =
    [
        "spkcache_fifo_chunk_preds", "chunk_pre_encode_embs", "chunk_pre_encode_lengths",
    ];

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly List<OrtValue> _owned = [];

    private readonly float[] _chunk = new float[SortformerGeometry.MelFramesPerCall * SortformerGeometry.MelBands];
    private readonly long[] _chunkLengths = new long[1];
    private readonly float[] _cache = new float[SortformerGeometry.SpeakerCacheLength * SortformerGeometry.EmbeddingDimension];
    private readonly long[] _cacheLengths = new long[1];
    private readonly float[] _fifo = new float[SortformerGeometry.FifoLength * SortformerGeometry.EmbeddingDimension];
    private readonly long[] _fifoLengths = new long[1];

    private readonly float[] _predictions = new float[SortformerGeometry.PredictionRows * SortformerGeometry.SpeakerCount];
    private readonly float[] _embeddings = new float[SortformerGeometry.EncoderFramesPerCall * SortformerGeometry.EmbeddingDimension];
    private readonly long[] _embeddingLengths = new long[1];

    private readonly OrtValue[] _inputs;
    private readonly OrtValue[] _outputs;

    public SortformerModel(string modelPath, SortformerModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"The diarisation model is not at {modelPath}.", modelPath);
        }

        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = options.EnableMemoryArena,
            EnableMemoryPattern = options.EnableMemoryPattern,
        };

        if (options.IntraOpThreads > 0)
        {
            sessionOptions.IntraOpNumThreads = options.IntraOpThreads;
        }

        _session = new InferenceSession(modelPath, sessionOptions);
        Verify();

        _inputs =
        [
            Wrap(_chunk, [1, SortformerGeometry.MelFramesPerCall, SortformerGeometry.MelBands]),
            Wrap(_chunkLengths, [1]),
            Wrap(_cache, [1, SortformerGeometry.SpeakerCacheLength, SortformerGeometry.EmbeddingDimension]),
            Wrap(_cacheLengths, [1]),
            Wrap(_fifo, [1, SortformerGeometry.FifoLength, SortformerGeometry.EmbeddingDimension]),
            Wrap(_fifoLengths, [1]),
        ];

        _outputs =
        [
            Wrap(_predictions, [1, SortformerGeometry.PredictionRows, SortformerGeometry.SpeakerCount]),
            Wrap(_embeddings, [1, SortformerGeometry.EncoderFramesPerCall, SortformerGeometry.EmbeddingDimension]),
            Wrap(_embeddingLengths, [1]),
        ];
    }

    /// <summary>The mel buffer the next call reads, <c>[3048 x 128]</c> row-major. Fill it, then run.</summary>
    public Span<float> Features => _chunk;

    /// <summary>Packed activity over <c>[cache | FIFO | chunk]</c>, <c>[609 x 4]</c>, after the last run.</summary>
    public ReadOnlySpan<float> Predictions => _predictions;

    /// <summary>Chunk embeddings, <c>[381 x 512]</c>, of which the first <see cref="EncoderFrames"/> are real.</summary>
    public ReadOnlySpan<float> Embeddings => _embeddings;

    /// <summary>
    /// <c>chunk_pre_encode_lengths</c>: encoder frames the chunk actually produced. The speaker
    /// cache must be given this rather than the fixed 381 the tensor is wide, because the reference
    /// takes the chunk's capacity from the tensor it is handed.
    /// </summary>
    public int EncoderFrames { get; private set; }

    /// <summary>
    /// Runs one streaming step over whatever is currently in <see cref="Features"/>.
    /// </summary>
    /// <param name="melFrames">Valid mel frames in the buffer; the rest must already be zero.</param>
    /// <param name="cache">The speaker cache, <c>[188 x 512]</c>.</param>
    /// <param name="cacheFrames">How many of its rows are real.</param>
    /// <param name="fifo">The FIFO, <c>[40 x 512]</c>.</param>
    /// <param name="fifoFrames">How many of its rows are real.</param>
    public void Run(int melFrames, ReadOnlySpan<float> cache, int cacheFrames, ReadOnlySpan<float> fifo, int fifoFrames)
    {
        cache.CopyTo(_cache);
        fifo.CopyTo(_fifo);
        _chunkLengths[0] = melFrames;
        _cacheLengths[0] = cacheFrames;
        _fifoLengths[0] = fifoFrames;

        _session.Run(_runOptions, InputNames, _inputs, OutputNames, _outputs);

        EncoderFrames = (int)_embeddingLengths[0];

        // forward_for_export leaves apply_mask_to_preds to the host. Without it the packed
        // predictions past the total valid length — whatever the graph's padding happened to
        // produce — are fed into the speaker cache as if they were real frames.
        var valid = cacheFrames + fifoFrames + EncoderFrames;
        var from = Math.Clamp(valid, 0, SortformerGeometry.PredictionRows) * SortformerGeometry.SpeakerCount;
        Array.Clear(_predictions, from, _predictions.Length - from);
    }

    /// <summary>
    /// Fails at load rather than at the first wrong number: a graph whose inputs are not the ones
    /// this host drives cannot be run correctly, and a shape mismatch here is the difference between
    /// this export and another variant of the same model.
    /// </summary>
    private void Verify()
    {
        Check(_session.InputMetadata, InputNames, "input");
        Check(_session.OutputMetadata, OutputNames, "output");

        ExpectShape(_session.InputMetadata, "chunk", [1, SortformerGeometry.MelFramesPerCall, SortformerGeometry.MelBands]);
        ExpectShape(
            _session.InputMetadata,
            "spkcache",
            [1, SortformerGeometry.SpeakerCacheLength, SortformerGeometry.EmbeddingDimension]);
        ExpectShape(_session.InputMetadata, "fifo", [1, SortformerGeometry.FifoLength, SortformerGeometry.EmbeddingDimension]);
        ExpectShape(
            _session.OutputMetadata,
            "spkcache_fifo_chunk_preds",
            [1, SortformerGeometry.PredictionRows, SortformerGeometry.SpeakerCount]);
        ExpectShape(
            _session.OutputMetadata,
            "chunk_pre_encode_embs",
            [1, SortformerGeometry.EncoderFramesPerCall, SortformerGeometry.EmbeddingDimension]);

        static void Check(IReadOnlyDictionary<string, NodeMetadata> metadata, string[] expected, string what)
        {
            foreach (var name in expected)
            {
                if (!metadata.ContainsKey(name))
                {
                    throw new InvalidOperationException(
                        $"This is not the Streaming Sortformer 4spk export: it has no {what} called '{name}'. " +
                        $"Its {what}s are {string.Join(", ", metadata.Keys)}.");
                }
            }
        }

        static void ExpectShape(IReadOnlyDictionary<string, NodeMetadata> metadata, string name, int[] expected)
        {
            var actual = metadata[name].Dimensions;
            if (actual.Length == expected.Length && actual.SequenceEqual(expected))
            {
                return;
            }

            throw new InvalidOperationException(
                $"'{name}' is [{string.Join(", ", actual)}] where this host drives " +
                $"[{string.Join(", ", expected)}]. That is a different streaming variant of the same model, " +
                "and pairing one variant's geometry with another's evicts the wrong frames while looking healthy.");
        }
    }

    private OrtValue Wrap<T>(T[] buffer, long[] shape)
        where T : unmanaged
    {
        var value = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer.AsMemory(), shape);
        _owned.Add(value);
        return value;
    }

    public void Dispose()
    {
        foreach (var value in _owned)
        {
            value.Dispose();
        }

        _owned.Clear();
        _runOptions.Dispose();
        _session.Dispose();
    }
}

/// <summary>How the ONNX session is configured.</summary>
public sealed record SortformerModelOptions
{
    /// <summary>
    /// The Python spike's settings, so a C# figure is comparable with the 74x realtime and the
    /// 1 315 MB it measured rather than differing for a reason nobody wrote down.
    /// </summary>
    public static SortformerModelOptions Default { get; } = new();

    /// <summary>Intra-op threads, or 0 to let ONNX Runtime choose. The spike used 12.</summary>
    public int IntraOpThreads { get; init; }

    /// <summary>
    /// The pooling allocator. On by default because that is what the spike measured with; turning it
    /// off is the documented lever against the 1 315 MB steady state the spike found, and what it
    /// costs in throughput here has not been measured.
    /// </summary>
    public bool EnableMemoryArena { get; init; } = true;

    /// <summary>Pre-planned allocation blocks. Same reasoning as <see cref="EnableMemoryArena"/>.</summary>
    public bool EnableMemoryPattern { get; init; } = true;
}
