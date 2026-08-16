using System.Buffers;
using System.Diagnostics;
using System.Text;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Transcription;
using Parakeet.Engine.ParakeetCpp.Interop;

namespace Parakeet.Engine.ParakeetCpp;

public sealed class ParakeetNativeException : Exception
{
    public ParakeetNativeException(string message)
        : base(message)
    {
    }

    public ParakeetNativeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ParakeetNativeException()
    {
    }
}

public sealed record ParakeetCppOptions
{
    public required string ModelPath { get; init; }

    /// <summary>
    /// Vulkan by default: one graphics driver, three vendors, no 553 MB CUDA runtime download.
    /// </summary>
    public ComputeBackend Backend { get; init; } = ComputeBackend.Vulkan;

    /// <summary>
    /// Fall back to another backend when the requested one is not present. Not a single chain: a
    /// CUDA request falls back to CPU only, never to Vulkan, because asking for CUDA is deliberate
    /// and silently substituting the other GPU tier would hide that it is not running. Nothing ever
    /// falls back <em>into</em> CUDA. See <c>ParakeetNativeLibrary.BackendOrder</c>.
    /// </summary>
    public bool AllowBackendFallback { get; init; } = true;

    /// <summary>Directory holding the native library, overriding the search order.</summary>
    public string? NativeDirectory { get; init; }

    /// <summary>
    /// Set <c>GGML_VK_DISABLE_BFLOAT16</c> before the model is loaded, for Vulkan devices whose
    /// bf16 cooperative-matrix shaders will not build.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, and deliberately not inferred. The symptom is total — the load entry point
    /// returns NULL and the process then dies in Vulkan teardown — but the ABI exposes no way to
    /// ask a device about bf16 before loading a model, and a failed load cannot be retried in the
    /// same process. So the choice has to be made before there is any evidence to make it with,
    /// and turning it on for every Vulkan device would change the configuration every measured
    /// Vulkan figure in docs/UNPROVEN.md was taken under.
    /// </para>
    /// <para>
    /// Measured on an AMD Radeon 880M, driver 32.0.13022.3006, which reports <c>bf16: 0</c> and
    /// <c>KHR_coopmat</c>: with this set the same model loads and decodes, and it is faster than
    /// <c>GGML_VK_DISABLE_COOPMAT</c>, which also works but gives up the matrix cores. Ignored for
    /// backends other than Vulkan, and never overrides a value already in the environment.
    /// </para>
    /// </remarks>
    public bool DisableVulkanBFloat16 { get; init; }

    /// <summary>Run a throwaway decode at load so the first measured decode is not the first decode.</summary>
    public bool WarmUp { get; init; } = true;

    /// <summary>Segments per native batch call.</summary>
    public int BatchSize { get; init; } = 4;

    public string? ModelId { get; init; }

    public string? Quantisation { get; init; }

    /// <summary>Sample rate used for the warm-up burst.</summary>
    public int WarmUpSampleRate { get; init; } = 16_000;
}

/// <summary>
/// The real engine: parakeet.cpp behind its flat C ABI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> Calls on one context are serialised by a semaphore. This is not caution
/// for its own sake: <c>src/parakeet_capi.cpp</c> contains no mutex, no lock guard and no
/// thread-local state, and the context holds one shared model plus a mutable last-error
/// string, so two concurrent decodes on one context race on both. Parallelism, when it is
/// wanted, comes from the engine's own thread count, not from calling it from several threads.
/// </para>
/// <para>
/// <b>Cancellation.</b> The ABI exposes no abort hook, so a decode already in flight runs to
/// completion. Cancelling stops the next batch from being scheduled and discards what comes
/// back. The capability flag says so rather than pretending otherwise.
/// </para>
/// </remarks>
public sealed class ParakeetCppEngine : SegmentingTranscriptionEngine
{
    private readonly ParakeetCppOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ParakeetContextHandle? _context;
    private EngineCapabilities _capabilities;
    private bool _disposed;

    public ParakeetCppEngine(ParakeetCppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchSize, 1);

        _options = options;
        _capabilities = new EngineCapabilities
        {
            EngineName = "parakeet.cpp",
            ModelId = options.ModelId,
            Quantisation = options.Quantisation,
            Backend = options.Backend,
            SupportsWordTimestamps = true,
            SupportsBatchDecode = true,
            SupportsLanguageSelection = true,
            SupportsDecodeCancellation = false,

            // Verified against include/parakeet_capi.h at ABI v6 and include/parakeet.h: no
            // entry point takes a thread count, and the C++ surface exposes none, so ggml
            // decides. Capping decode threads at eight is still the right policy — it just
            // cannot be applied from here until upstream takes an n_threads parameter.
            SupportsThreadCount = false,
            MaxSingleDecodeLength = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>
    /// The ggml knob that disables bf16 kernels in the Vulkan backend, and with them the bf16
    /// cooperative-matrix shader variants that some devices cannot build.
    /// </summary>
    public const string VulkanDisableBFloat16Variable = "GGML_VK_DISABLE_BFLOAT16";

    public override EngineCapabilities Capabilities => _capabilities;

    protected override int BatchSize => _options.BatchSize;

    /// <summary>How long loading the model took, measured separately from any decode.</summary>
    public TimeSpan? ColdLoadDuration { get; private set; }

    /// <summary>
    /// True when this engine set <see cref="VulkanDisableBFloat16Variable"/> itself. False when the
    /// option was off, the backend was not Vulkan, the variable was already set by someone else, or
    /// the C runtime would not take it — so it records what happened rather than what was asked for.
    /// </summary>
    public bool VulkanBFloat16WorkaroundApplied { get; private set; }

    public override async ValueTask LoadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_context is not null)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_context is not null)
            {
                return;
            }

            if (!File.Exists(_options.ModelPath))
            {
                throw new FileNotFoundException(
                    $"Model file not found: {_options.ModelPath}", _options.ModelPath);
            }

            ApplyVulkanWorkarounds();

            ParakeetNativeLibrary.Configure(_options.Backend, _options.AllowBackendFallback, _options.NativeDirectory);

            // Loading a multi-hundred-megabyte model blocks; keeping it off the caller's thread
            // is the difference between a busy cursor and a hung window.
            var stopwatch = Stopwatch.StartNew();
            var (handle, abi) = await Task.Run(
                () =>
                {
                    var version = ParakeetNativeLibrary.EnsureLoadedAndCompatible();
                    var context = NativeMethods.parakeet_capi_load(_options.ModelPath);
                    return (context, version);
                },
                ct).ConfigureAwait(false);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new ParakeetNativeException(DescribeLoadFailure());
            }

            _context = handle;
            ColdLoadDuration = stopwatch.Elapsed;

            _capabilities = _capabilities with
            {
                Backend = ParakeetNativeLibrary.LoadedBackend ?? _options.Backend,
                NativeAbiVersion = abi,
            };
        }
        finally
        {
            _gate.Release();
        }

        if (_options.WarmUp)
        {
            await WarmUpAsync(_options.WarmUpSampleRate, TranscriptionOptions.Default, ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies the opted-in Vulkan knobs before anything reads them. Must run before the first
    /// native call: ggml reads these during device initialisation, once per process.
    /// </summary>
    private void ApplyVulkanWorkarounds()
    {
        if (_options.Backend != ComputeBackend.Vulkan || !_options.DisableVulkanBFloat16)
        {
            return;
        }

        // A value already in the environment wins. Someone who set it deliberately — including
        // to "0" to rule it out while diagnosing something else — should not have it rewritten by
        // an option whose whole purpose is to set the same variable.
        if (NativeEnvironment.IsSet(VulkanDisableBFloat16Variable))
        {
            return;
        }

        VulkanBFloat16WorkaroundApplied = NativeEnvironment.Set(VulkanDisableBFloat16Variable, "1");
    }

    /// <summary>
    /// The load entry point returns NULL with no message, so everything useful about a failure has
    /// to be assembled from what was asked for. On Vulkan that includes the one knob known to turn
    /// a total load failure into a working device.
    /// </summary>
    private string DescribeLoadFailure()
    {
        var builder = new StringBuilder();
        builder.Append("parakeet.cpp could not load '").Append(_options.ModelPath)
            .AppendLine("'. The file may be truncated, may not be a GGUF conversion of a Parakeet " +
                        "checkpoint, or may be a quantisation this build does not support. " +
                        "(The load entry point returns NULL without a message on failure.)");

        if (_options.Backend != ComputeBackend.Vulkan)
        {
            return builder.ToString();
        }

        if (VulkanBFloat16WorkaroundApplied)
        {
            builder.AppendLine(
                $"This ran with {VulkanDisableBFloat16Variable}=1 already applied, so the bf16 " +
                "cooperative-matrix path is not the cause here.");
            return builder.ToString();
        }

        builder.AppendLine(
            "On Vulkan this is also what a device whose bf16 cooperative-matrix shaders will not " +
            $"build looks like — the same model then loads with {VulkanDisableBFloat16Variable}=1. " +
            "Measured on an AMD Radeon 880M; see docs/UNPROVEN.md. Retrying in this process is not " +
            "possible, because the Vulkan device does not survive the failed load: set the variable " +
            "in the environment, or pass the option, and start again. Loading on the cpu backend " +
            "distinguishes a device problem from a bad model file.");

        return builder.ToString();
    }

    protected override async ValueTask<IReadOnlyList<DecodedSegment>> DecodeAsync(
        IReadOnlyList<AudioSegment> batch,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (batch.Count == 0)
        {
            return [];
        }

        var context = _context
            ?? throw new InvalidOperationException("LoadAsync must complete before a decode is attempted.");

        var sampleRate = batch[0].SampleRate;
        foreach (var segment in batch)
        {
            if (segment.SampleRate != sampleRate)
            {
                throw new ArgumentException(
                    "Every segment in a batch must share one sample rate; the ABI takes a single rate for the batch.",
                    nameof(batch));
            }
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => DecodeBatch(context, batch, options, sampleRate), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static unsafe IReadOnlyList<DecodedSegment> DecodeBatch(
        ParakeetContextHandle context,
        IReadOnlyList<AudioSegment> batch,
        TranscriptionOptions options,
        int sampleRate)
    {
        var total = 0L;
        foreach (var segment in batch)
        {
            total += segment.Samples.Length;
        }

        if (total > int.MaxValue)
        {
            throw new ArgumentException("Batch exceeds the int sample count the ABI accepts.", nameof(batch));
        }

        // The ABI's stated precondition, which it does not validate: the sum of the per-clip
        // lengths must equal the number of floats in the concatenated buffer. A larger sum
        // reads out of bounds, so the buffer is built here and the lengths derived from it.
        var pool = ArrayPool<float>.Shared;
        var samples = pool.Rent((int)total);
        var lengths = ArrayPool<int>.Shared.Rent(batch.Count);

        try
        {
            var offset = 0;
            for (var i = 0; i < batch.Count; i++)
            {
                var source = batch[i].Samples.Span;
                source.CopyTo(samples.AsSpan(offset));
                lengths[i] = source.Length;
                offset += source.Length;
            }

            string? json;
            fixed (float* samplePointer = samples)
            fixed (int* lengthPointer = lengths)
            {
                var raw = NativeMethods.parakeet_capi_transcribe_pcm_batch_json_lang(
                    context,
                    samplePointer,
                    lengthPointer,
                    batch.Count,
                    sampleRate,
                    (int)options.Decoder,
                    options.Language);

                json = NativeString.Consume(raw);
            }

            if (json is null)
            {
                var error = NativeString.Borrow(NativeMethods.parakeet_capi_last_error(context));
                throw new ParakeetNativeException(
                    error.Length > 0
                        ? $"parakeet.cpp batch decode failed: {error}"
                        : "parakeet.cpp batch decode returned NULL and reported no error.");
            }

            var clips = ParakeetJson.ParseBatch(json);
            if (clips.Count != batch.Count)
            {
                throw new ParakeetNativeException(
                    $"Batch decode returned {clips.Count} documents for {batch.Count} clips. " +
                    "Mismatched results would attach text to the wrong point in the timeline.");
            }

            var decoded = new List<DecodedSegment>(clips.Count);
            foreach (var clip in clips)
            {
                decoded.Add(new DecodedSegment
                {
                    Text = clip.Text,
                    Words = options.WordTimestamps ? clip.Words : [],
                });
            }

            return decoded;
        }
        finally
        {
            pool.Return(samples);
            ArrayPool<int>.Shared.Return(lengths);
        }
    }

    /// <summary>
    /// Runs TDT beam search over one clip and returns the raw ranked-hypotheses JSON.
    /// </summary>
    /// <remarks>
    /// Diagnostics only, and deliberately not reachable from <see cref="DecodeAsync"/>. Beam
    /// search on Parakeet TDT is a measured regression: across 80 real production captures it
    /// changed 19 transcripts and every change lost something — a closing sentence, an item
    /// from a list of three, and one near-silent capture that greedy correctly returned empty
    /// came back with an invented word. This exists so that result can be reproduced, not so
    /// the product can offer it.
    /// </remarks>
    public async Task<string> RunBeamSearchDiagnosticAsync(
        AudioSegment segment,
        BeamSearchOptions beam,
        string? language = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(beam);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var context = _context
            ?? throw new InvalidOperationException("LoadAsync must complete before a decode is attempted.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => RunBeamSearch(context, segment, beam, language),
                ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static unsafe string RunBeamSearch(
        ParakeetContextHandle context, AudioSegment segment, BeamSearchOptions beam, string? language)
    {
        var samples = segment.Samples.ToArray();

        fixed (float* pointer = samples)
        {
            var raw = NativeMethods.parakeet_capi_transcribe_pcm_nbest_json(
                context,
                pointer,
                samples.Length,
                segment.SampleRate,
                beam.BeamSize,
                beam.NBest,
                beam.ScoreNormalisation ? 1 : 0,
                language);

            return NativeString.Consume(raw)
                ?? throw new ParakeetNativeException(
                    $"Beam search failed: {NativeString.Borrow(NativeMethods.parakeet_capi_last_error(context))}");
        }
    }

    public override ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _context?.Dispose();
        _context = null;
        _gate.Dispose();

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
