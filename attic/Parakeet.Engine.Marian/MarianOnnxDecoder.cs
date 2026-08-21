using System.Globalization;
using Microsoft.ML.OnnxRuntime;

namespace Parakeet.Engine.Marian;

/// <summary>Session settings for the two graphs.</summary>
internal sealed record MarianSessionOptions
{
    public static MarianSessionOptions Default { get; } = new();

    /// <summary>Intra-op threads, or 0 to let ONNX Runtime choose as it does for Python.</summary>
    /// <remarks>
    /// Zero by default on purpose. Every recorded hypothesis came out of Python's onnxruntime with
    /// its own default, which is the same default this is, and pinning a different number here
    /// would change how the reductions inside a matmul are partitioned — a difference far below any
    /// decision the search makes, until it is not.
    /// </remarks>
    public int IntraOpThreads { get; init; }

    public bool EnableMemoryArena { get; init; } = true;

    public bool EnableMemoryPattern { get; init; } = true;
}

/// <summary>
/// The two ONNX graphs, and the only file in this project that knows ONNX Runtime exists.
/// </summary>
/// <remarks>
/// <para>
/// The encoder runs once per source, at batch 1, and its output is then repeated across the beams —
/// which is what HuggingFace does, and doing it the other way round (encoding a batch of six
/// identical rows) is not the same arithmetic.
/// </para>
/// <para>
/// The decoder is the <b>merged</b> export: one graph with a <c>use_cache_branch</c> switch, taking
/// the whole prefix with no cache on the first call and one token with a cache on every call after.
/// Its cross-attention keys and values are computed on that first call and then fed back unchanged
/// for the rest of the sentence, because they depend only on the source — the same thing optimum
/// does, and the reason a step costs what it costs rather than re-reading the source every token.
/// </para>
/// <para>
/// <b>The cross-attention cache is not permuted when the beams are.</b> All six beams were seeded
/// from one tiled encoder output, so all six of its rows are identical and a permutation of
/// identical rows is the rows. Skipping it saves about 12 MB of copying per step; asserting it
/// would cost more than it proves, so it is written down here instead.
/// </para>
/// </remarks>
internal sealed class MarianOnnxDecoder : IMarianDecoder
{
    private readonly MarianConfiguration _configuration;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly RunOptions _runOptions = new();

    private readonly string[] _encoderInputNames = ["input_ids", "attention_mask"];
    private readonly string[] _encoderOutputNames = ["last_hidden_state"];

    private readonly string[] _decoderInputNames;
    private readonly string[] _firstStepOutputNames;
    private readonly string[] _cachedStepOutputNames;

    // Per-layer past. Grown rather than reallocated: the self-attention cache gains a row per
    // token and reallocating twelve tensors thirty times a sentence is work the decode does not
    // need to do.
    private readonly float[][] _selfKey;
    private readonly float[][] _selfValue;
    private readonly float[][] _crossKey;
    private readonly float[][] _crossValue;
    private readonly float[][] _selfScratch;

    private float[] _encoderHidden = [];
    private long[] _encoderMask = [];
    private float[] _logits = [];
    private long[] _tokens = [];

    private int _beams;
    private int _sourceLength;
    private int _pastLength;
    private bool _disposed;

    public MarianOnnxDecoder(string directory, MarianConfiguration configuration, MarianSessionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(configuration);
        options ??= MarianSessionOptions.Default;

        _configuration = configuration;

        var encoderPath = Path.Combine(directory, "encoder_model.onnx");
        var decoderPath = Path.Combine(directory, "decoder_model_merged.onnx");
        foreach (var path in new[] { encoderPath, decoderPath })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"The translation graph is not at {path}.", path);
            }
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

        // Opened together and cleaned up together. The decoder is 845 MiB and the likelier of the
        // two to fail — a truncated download, a graph an older ONNX Runtime will not take — and
        // without this the encoder's session, and the 520 MiB it has already mapped, would be left
        // to a finalizer that native handles do not have.
        InferenceSession? encoder = null;
        InferenceSession? decoder = null;
        try
        {
            encoder = new InferenceSession(encoderPath, sessionOptions);
            decoder = new InferenceSession(decoderPath, sessionOptions);
        }
        catch
        {
            encoder?.Dispose();
            decoder?.Dispose();
            _runOptions.Dispose();
            throw;
        }

        _encoder = encoder;
        _decoder = decoder;

        var layers = configuration.DecoderLayers;
        _selfKey = new float[layers][];
        _selfValue = new float[layers][];
        _crossKey = new float[layers][];
        _crossValue = new float[layers][];
        _selfScratch = new float[layers * 2][];
        for (var layer = 0; layer < layers; layer++)
        {
            _selfKey[layer] = [];
            _selfValue[layer] = [];
            _crossKey[layer] = [];
            _crossValue[layer] = [];
            _selfScratch[(layer * 2) + 0] = [];
            _selfScratch[(layer * 2) + 1] = [];
        }

        var inputs = new List<string> { "encoder_attention_mask", "input_ids", "encoder_hidden_states" };
        var firstOutputs = new List<string> { "logits" };
        var cachedOutputs = new List<string> { "logits" };
        for (var layer = 0; layer < layers; layer++)
        {
            inputs.Add(Past(layer, "decoder", "key"));
            inputs.Add(Past(layer, "decoder", "value"));
            inputs.Add(Past(layer, "encoder", "key"));
            inputs.Add(Past(layer, "encoder", "value"));

            firstOutputs.Add(Present(layer, "decoder", "key"));
            firstOutputs.Add(Present(layer, "decoder", "value"));
            firstOutputs.Add(Present(layer, "encoder", "key"));
            firstOutputs.Add(Present(layer, "encoder", "value"));

            cachedOutputs.Add(Present(layer, "decoder", "key"));
            cachedOutputs.Add(Present(layer, "decoder", "value"));
        }

        inputs.Add("use_cache_branch");

        _decoderInputNames = [.. inputs];
        _firstStepOutputNames = [.. firstOutputs];
        _cachedStepOutputNames = [.. cachedOutputs];

        try
        {
            Verify();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int VocabularySize => _configuration.VocabularySize;

    /// <summary>How many tokens of self-attention cache the last <see cref="Step"/> left behind.</summary>
    public int PastLength => _pastLength;

    public void Begin(IReadOnlyList<int> sourceIds, int beams)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(beams, 1);

        if (sourceIds.Count == 0)
        {
            throw new ArgumentException("A source with no tokens has nothing to encode.", nameof(sourceIds));
        }

        _beams = beams;
        _sourceLength = sourceIds.Count;
        _pastLength = 0;

        var ids = new long[sourceIds.Count];
        var mask = new long[sourceIds.Count];
        for (var i = 0; i < sourceIds.Count; i++)
        {
            ids[i] = sourceIds[i];
            mask[i] = 1;
        }

        // Batch 1, then repeated: the encoder never sees six copies of the same sentence.
        using var encoderIds = Wrap(ids, [1, sourceIds.Count]);
        using var encoderMask = Wrap(mask, [1, sourceIds.Count]);
        using var encoded = _encoder.Run(
            _runOptions, _encoderInputNames, [encoderIds, encoderMask], _encoderOutputNames);

        var hidden = encoded[0].GetTensorDataAsSpan<float>();
        var width = _sourceLength * _configuration.ModelDimension;

        Grow(ref _encoderHidden, beams * width);
        for (var beam = 0; beam < beams; beam++)
        {
            hidden.CopyTo(_encoderHidden.AsSpan(beam * width, width));
        }

        Grow(ref _encoderMask, beams * _sourceLength);
        for (var beam = 0; beam < beams; beam++)
        {
            for (var i = 0; i < _sourceLength; i++)
            {
                _encoderMask[(beam * _sourceLength) + i] = 1;
            }
        }

        Grow(ref _logits, beams * _configuration.VocabularySize);
        Grow(ref _tokens, beams);

        var crossWidth = beams * _configuration.DecoderAttentionHeads * _sourceLength * _configuration.HeadDimension;
        for (var layer = 0; layer < _configuration.DecoderLayers; layer++)
        {
            Grow(ref _crossKey[layer], crossWidth);
            Grow(ref _crossValue[layer], crossWidth);
        }
    }

    public ReadOnlySpan<float> Step(ReadOnlySpan<long> tokens)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_beams == 0)
        {
            throw new InvalidOperationException("Begin has not been called: there is no source to decode against.");
        }

        if (tokens.Length != _beams)
        {
            throw new ArgumentException(
                $"Expected one token per beam ({_beams}), got {tokens.Length}.", nameof(tokens));
        }

        tokens.CopyTo(_tokens);

        var useCache = _pastLength > 0;
        var heads = _configuration.DecoderAttentionHeads;
        var head = _configuration.HeadDimension;
        var layers = _configuration.DecoderLayers;

        var pastSelf = new long[] { _beams, heads, _pastLength, head };
        var pastCross = new long[] { _beams, heads, useCache ? _sourceLength : 0, head };
        var selfElements = _beams * heads * _pastLength * head;
        var crossElements = useCache ? _beams * heads * _sourceLength * head : 0;

        var owned = new List<OrtValue>(_decoderInputNames.Length);
        var useCacheBranch = new[] { useCache };

        try
        {
            var values = new OrtValue[_decoderInputNames.Length];
            var at = 0;

            values[at++] = Track(owned, Wrap(_encoderMask.AsMemory(0, _beams * _sourceLength), [_beams, _sourceLength]));
            values[at++] = Track(owned, Wrap(_tokens.AsMemory(0, _beams), [_beams, 1]));
            values[at++] = Track(owned, Wrap(
                _encoderHidden.AsMemory(0, _beams * _sourceLength * _configuration.ModelDimension),
                [_beams, _sourceLength, _configuration.ModelDimension]));

            for (var layer = 0; layer < layers; layer++)
            {
                values[at++] = Track(owned, Wrap(_selfKey[layer].AsMemory(0, selfElements), pastSelf));
                values[at++] = Track(owned, Wrap(_selfValue[layer].AsMemory(0, selfElements), pastSelf));
                values[at++] = Track(owned, Wrap(_crossKey[layer].AsMemory(0, crossElements), pastCross));
                values[at++] = Track(owned, Wrap(_crossValue[layer].AsMemory(0, crossElements), pastCross));
            }

            values[at] = Track(owned, Wrap(useCacheBranch.AsMemory(), [1]));

            var outputNames = useCache ? _cachedStepOutputNames : _firstStepOutputNames;
            using var outputs = _decoder.Run(_runOptions, _decoderInputNames, values, outputNames);

            // [beams, 1, vocab]: one position in, so the last position is the only position.
            outputs[0].GetTensorDataAsSpan<float>().CopyTo(_logits);

            var next = _pastLength + 1;
            var nextSelfElements = _beams * heads * next * head;
            var index = 1;
            for (var layer = 0; layer < layers; layer++)
            {
                Grow(ref _selfKey[layer], nextSelfElements);
                Grow(ref _selfValue[layer], nextSelfElements);
                Grow(ref _selfScratch[(layer * 2) + 0], nextSelfElements);
                Grow(ref _selfScratch[(layer * 2) + 1], nextSelfElements);

                outputs[index++].GetTensorDataAsSpan<float>().CopyTo(_selfKey[layer]);
                outputs[index++].GetTensorDataAsSpan<float>().CopyTo(_selfValue[layer]);

                if (!useCache)
                {
                    outputs[index++].GetTensorDataAsSpan<float>().CopyTo(_crossKey[layer]);
                    outputs[index++].GetTensorDataAsSpan<float>().CopyTo(_crossValue[layer]);
                }
            }

            _pastLength = next;
            return _logits.AsSpan(0, _beams * _configuration.VocabularySize);
        }
        finally
        {
            foreach (var value in owned)
            {
                value.Dispose();
            }
        }
    }

    public void Reorder(ReadOnlySpan<int> order)
    {
        if (order.Length != _beams)
        {
            throw new ArgumentException($"Expected one source beam per beam ({_beams}), got {order.Length}.", nameof(order));
        }

        var stride = _configuration.DecoderAttentionHeads * _pastLength * _configuration.HeadDimension;
        if (stride == 0)
        {
            return;
        }

        var identity = true;
        for (var beam = 0; beam < order.Length && identity; beam++)
        {
            identity = order[beam] == beam;
        }

        if (identity)
        {
            return;
        }

        for (var layer = 0; layer < _configuration.DecoderLayers; layer++)
        {
            Permute(_selfKey[layer], _selfScratch[(layer * 2) + 0], order, stride);
            Permute(_selfValue[layer], _selfScratch[(layer * 2) + 1], order, stride);
        }
    }

    private static void Permute(float[] buffer, float[] scratch, ReadOnlySpan<int> order, int stride)
    {
        for (var beam = 0; beam < order.Length; beam++)
        {
            buffer.AsSpan(order[beam] * stride, stride).CopyTo(scratch.AsSpan(beam * stride, stride));
        }

        scratch.AsSpan(0, order.Length * stride).CopyTo(buffer);
    }

    /// <summary>
    /// Checks the graphs are the ones this code was written against before anything is decoded.
    /// </summary>
    /// <remarks>
    /// A missing input surfaces from ONNX Runtime as a name it did not recognise, at the first
    /// step, halfway through a file. A missing input surfaces from here as a sentence naming the
    /// graph and the input, at load.
    /// </remarks>
    private void Verify()
    {
        foreach (var (session, names, what) in new[]
                 {
                     (_encoder, (IReadOnlyList<string>)_encoderInputNames, "encoder"),
                     (_decoder, _decoderInputNames, "decoder"),
                 })
        {
            foreach (var name in names)
            {
                if (!session.InputMetadata.ContainsKey(name))
                {
                    throw new InvalidDataException(
                        $"The translation {what} graph has no input '{name}'. It has " +
                        $"{string.Join(", ", session.InputMetadata.Keys)}.");
                }
            }
        }

        var logits = _decoder.OutputMetadata["logits"].Dimensions;
        var width = logits.Length > 0 ? logits[^1] : -1;
        if (width > 0 && width != _configuration.VocabularySize)
        {
            throw new InvalidDataException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The decoder writes {width} logits and config.json says the vocabulary is " +
                    $"{_configuration.VocabularySize}. One of them is not from this checkpoint."));
        }
    }

    private static string Past(int layer, string side, string part) =>
        string.Create(CultureInfo.InvariantCulture, $"past_key_values.{layer}.{side}.{part}");

    private static string Present(int layer, string side, string part) =>
        string.Create(CultureInfo.InvariantCulture, $"present.{layer}.{side}.{part}");

    private static OrtValue Track(List<OrtValue> owned, OrtValue value)
    {
        owned.Add(value);
        return value;
    }

    private static OrtValue Wrap<T>(Memory<T> buffer, long[] shape)
        where T : unmanaged =>
        OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer, shape);

    private static OrtValue Wrap<T>(T[] buffer, long[] shape)
        where T : unmanaged =>
        OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer.AsMemory(), shape);

    private static void Grow<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            buffer = new T[length];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runOptions.Dispose();
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
