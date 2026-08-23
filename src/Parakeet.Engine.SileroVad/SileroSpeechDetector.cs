using Microsoft.ML.OnnxRuntime;
using Parakeet.Audio;
using Parakeet.Core.Segmentation;

namespace Parakeet.Engine.SileroVad;

/// <summary>
/// Silero VAD on ONNX Runtime: the neural speech detector behind the segmenter's opt-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The graph and its contract.</b> Silero VAD v5 (<c>silero_vad.onnx</c>, MIT, snakers4/silero-vad)
/// takes three inputs and returns two: <c>input</c> is <c>[1, 576]</c> float32 — 64 samples of
/// context from the previous window followed by 512 new ones, at 16 kHz; <c>state</c> is
/// <c>[2, 1, 128]</c> float32, the recurrent state it handed back last time; <c>sr</c> is an int64
/// scalar naming the rate. <c>output</c> is <c>[1, 1]</c>, the probability that the window is
/// speech, and <c>stateN</c> is the state for next time. That is the wrapper upstream ships
/// (<c>utils_vad.py</c> at the pinned commit: window 512 at 16 kHz, context 64, state
/// <c>(2, batch, 128)</c>), read rather than recalled, and checked against the graph on
/// 2026-08-23 with the inputs and outputs named above. The constructor refuses a graph that does
/// not carry those names rather than running it and returning something.
/// </para>
/// <para>
/// <b>It runs on the CPU, one thread, in process.</b> The model is two megabytes and a window is
/// 32 ms of audio; the question is not whether a GPU would be faster but whether a thread pool is
/// worth spawning, and on this project's own measurement of the labelling pass — two pools
/// spinning on a GPU, most of the CPU load — it is not. Execution providers are not a setting here
/// because nothing about them has been measured for this model, and the rule this repository runs
/// on is that a provider changes the answer, not only the clock, until it is shown not to.
/// </para>
/// <para>
/// <b>Each stream resamples for itself.</b> The segmenter runs at the recording's own rate —
/// parakeet.cpp resamples inside the native library, so the transcription path never needed 16 kHz
/// before this — and a 44.1 kHz frame is 1,323 samples, which is not 512 of anything. So a stream
/// carries a <see cref="Resampler"/> to 16 kHz when the rate differs, scores every 512 resampled
/// samples as they become available, and answers with the latest probability. A frame can lag the
/// window that scores it by up to one window; the segmenter's minimum durations are many windows.
/// </para>
/// </remarks>
public sealed class SileroSpeechDetector : ISpeechDetector
{
    /// <summary>The rate the graph is fed at, whatever the recording's is.</summary>
    public const int ModelSampleRate = 16_000;

    /// <summary>New samples per inference, at <see cref="ModelSampleRate"/>: 32 ms.</summary>
    public const int WindowSamples = 512;

    /// <summary>Samples of the previous window prepended to each input, as the upstream wrapper does.</summary>
    public const int ContextSamples = 64;

    private const int StateSize = 128;

    private static readonly string[] InputNames = ["input", "state", "sr"];
    private static readonly string[] OutputNames = ["output", "stateN"];

    private readonly InferenceSession _session;
    private readonly string _modelPath;
    private bool _disposed;

    /// <summary>Loads the graph at <paramref name="modelPath"/>, or throws <see cref="SpeechDetectorException"/> saying why not.</summary>
    public SileroSpeechDetector(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        if (!File.Exists(modelPath))
        {
            throw new SpeechDetectorException($"Speech detection model not found: {modelPath}");
        }

        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };

        try
        {
            _session = new InferenceSession(modelPath, options);
        }
        catch (OnnxRuntimeException exception)
        {
            throw new SpeechDetectorException(
                $"ONNX Runtime could not load the speech detection model at {modelPath}: {exception.Message}", exception);
        }

        // The contract, held rather than assumed: a graph with different names would run and
        // return something, and nothing downstream could tell it from the real one.
        foreach (var name in InputNames)
        {
            if (!_session.InputMetadata.ContainsKey(name))
            {
                var inputs = string.Join(", ", _session.InputMetadata.Keys);
                _session.Dispose();
                throw new SpeechDetectorException(
                    $"{modelPath} is not the Silero VAD graph this build drives: it has no '{name}' input (its inputs are {inputs}).");
            }
        }

        foreach (var name in OutputNames)
        {
            if (!_session.OutputMetadata.ContainsKey(name))
            {
                var outputs = string.Join(", ", _session.OutputMetadata.Keys);
                _session.Dispose();
                throw new SpeechDetectorException(
                    $"{modelPath} is not the Silero VAD graph this build drives: it has no '{name}' output (its outputs are {outputs}).");
            }
        }

        _modelPath = modelPath;
    }

    /// <summary>The ONNX Runtime version behind this, from the managed assembly, for reports.</summary>
    public static string RuntimeVersion
    {
        get
        {
            var version = typeof(InferenceSession).Assembly.GetName().Version;
            return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public string Name => $"silero-vad ({Path.GetFileName(_modelPath)}) on ONNX Runtime {RuntimeVersion}, cpu";

    public ISpeechDetectorStream Open(int sampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        return new Stream(this, sampleRate);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }

    private float Score(float[] input, float[] state)
    {
        // Per call rather than held: a pinned handle per 32 ms window is nothing beside the model,
        // and a long-lived OrtValue over a managed array is a lifetime to get wrong.
        using var inputValue = OrtValue.CreateTensorValueFromMemory(
            OrtMemoryInfo.DefaultInstance, input.AsMemory(), [1L, ContextSamples + WindowSamples]);
        using var stateValue = OrtValue.CreateTensorValueFromMemory(
            OrtMemoryInfo.DefaultInstance, state.AsMemory(), [2L, 1L, StateSize]);
        using var rateValue = OrtValue.CreateTensorValueFromMemory(
            OrtMemoryInfo.DefaultInstance, new long[] { ModelSampleRate }.AsMemory(), Array.Empty<long>());
        using var runOptions = new RunOptions();

        using var outputs = _session.Run(
            runOptions,
            InputNames,
            [inputValue, stateValue, rateValue],
            OutputNames);

        var probability = outputs[0].GetTensorDataAsSpan<float>()[0];
        outputs[1].GetTensorDataAsSpan<float>().CopyTo(state);
        return probability;
    }

    private sealed class Stream : ISpeechDetectorStream
    {
        private readonly SileroSpeechDetector _owner;
        private readonly Resampler? _resampler;
        private readonly List<float> _resampled = [];
        private readonly List<float> _pending = new(WindowSamples * 4);
        private readonly float[] _input = new float[ContextSamples + WindowSamples];
        private readonly float[] _state = new float[2 * StateSize];
        private float _probability;
        private bool _disposed;

        public Stream(SileroSpeechDetector owner, int sampleRate)
        {
            _owner = owner;
            _resampler = sampleRate == ModelSampleRate ? null : new Resampler(sampleRate, ModelSampleRate);
        }

        public string Name => _owner.Name;

        public float Push(ReadOnlySpan<float> samples)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ObjectDisposedException.ThrowIf(_owner._disposed, _owner);

            if (_resampler is null)
            {
                Append(samples);
            }
            else
            {
                _resampled.Clear();
                _resampler.Process(samples, _resampled);
                Append(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_resampled));
            }

            return _probability;
        }

        private void Append(ReadOnlySpan<float> samples)
        {
            foreach (var sample in samples)
            {
                _pending.Add(sample);
            }

            var consumed = 0;
            while (_pending.Count - consumed >= WindowSamples)
            {
                // Context first — the last 64 samples of the previous input — then the new window.
                Array.Copy(_input, WindowSamples, _input, 0, ContextSamples);
                _pending.CopyTo(consumed, _input, ContextSamples, WindowSamples);
                consumed += WindowSamples;

                _probability = _owner.Score(_input, _state);
            }

            if (consumed > 0)
            {
                _pending.RemoveRange(0, consumed);
            }
        }

        public void Dispose() => _disposed = true;
    }
}
