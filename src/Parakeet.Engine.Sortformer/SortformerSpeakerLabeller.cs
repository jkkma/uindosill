using Parakeet.Core.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.Sortformer;

/// <summary>How the diariser is loaded and driven.</summary>
public sealed record SortformerLabellerOptions
{
    /// <summary>Path to <c>sortformer-default.onnx</c>. Required; there is no default location.</summary>
    public required string ModelPath { get; init; }

    /// <summary>Catalogue id carried into the transcript's provenance beside the ASR model's.</summary>
    public string ModelId { get; init; } = "sortformer-4spk-v2.1";

    /// <summary>Intra-op threads for the ONNX session, or 0 to let ONNX Runtime choose.</summary>
    public int IntraOpThreads { get; init; }

    /// <inheritdoc cref="SortformerModelOptions.EnableMemoryArena"/>
    public bool EnableMemoryArena { get; init; } = true;

    /// <summary>The tuned post-processing. Changing it invalidates the measured DER.</summary>
    public SortformerPostProcessingOptions PostProcessing { get; init; } = SortformerPostProcessingOptions.Default;
}

/// <summary>
/// Speaker labelling by NVIDIA's Streaming Sortformer 4spk v2.1, through a community ONNX export of
/// its 30.4 s streaming configuration, on the CPU.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is measured at.</b> AMI test, 16 meetings, 9.06 h: DER <b>16.33%</b> at collar 0 with
/// overlap scored and <b>13.60%</b> at this project's headline collar of 0.25, against pyannote's
/// <c>only_words</c> references, with post-processing fixed on the 18 development meetings and
/// applied unchanged. <c>docs/PHASES.md</c> carries the result and what it does and does not mean.
/// </para>
/// <para>
/// <b>Four speakers, and no more.</b> The cap is architectural. Above it a fifth voice is merged
/// into one of the four rather than reported, and no measurement in this repository prices what that
/// costs — AMI cannot, at 15 of its 16 test meetings holding exactly four speakers. The published
/// figures for five-plus speakers in this configuration are 34.81% and 38.90%. Callers must say so
/// rather than present the labels as complete, which is what
/// <see cref="SpeakerLabelling.DescribeLimit"/> is for.
/// </para>
/// <para>
/// <b>It trails the audio by half a minute.</b> This is the 30.4 s input-buffer export, so
/// "streaming" here means the diariser is thirty seconds behind. Fine for transcribing a file; not a
/// live-captioning latency. NVIDIA's 1.04 s configuration is a different graph this project does not
/// hold.
/// </para>
/// </remarks>
public sealed class SortformerSpeakerLabeller : ISpeakerLabeller
{
    private readonly SortformerLabellerOptions _options;

    /// <summary>
    /// The one load this labeller will ever attempt, running or finished — including finished
    /// badly.
    /// </summary>
    /// <remarks>
    /// A <see cref="Task{TResult}"/> rather than the model itself, for two reasons the interface
    /// states and a plain field cannot deliver. It is built on the thread pool, because
    /// <see cref="ISpeakerLabeller.LoadAsync"/> says "never on a UI thread" and building an
    /// <c>InferenceSession</c> over a 453 MiB graph with full optimisation takes long enough that
    /// the window would stop redrawing — the ASR engine takes the same care for the same reason.
    /// And a faulted task is remembered, so a batch queued behind a corrupt or wrong-variant model
    /// fails every file once rather than attempting the load again for each of them.
    /// </remarks>
    private Task<SortformerModel>? _load;

    private readonly System.Threading.Lock _gate = new();

    public SortformerSpeakerLabeller(SortformerLabellerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelPath);
        options.PostProcessing.Validate();
        _options = options;

        Capabilities = new SpeakerLabellerCapabilities
        {
            EngineName = "sortformer-onnx",
            ModelId = options.ModelId,
            Backend = ComputeBackend.Cpu,

            // The model estimates the count and cannot be told it. Saying so here is what makes
            // --speaker-count report that it was ignored instead of appearing to work.
            SupportsFixedSpeakerCount = false,
            MaxSpeakers = SortformerGeometry.SpeakerCount,
        };
    }

    public SpeakerLabellerCapabilities Capabilities { get; }

    public async ValueTask LoadAsync(CancellationToken ct = default) =>
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

    private Task<SortformerModel> EnsureLoadedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Locked because `??=` is not atomic and the thing it would build twice is a session that
        // settles at 1.3 GB. Every caller today is sequential, so this contends with nothing; it is
        // here because "nobody calls it twice at once" is a property of the callers rather than of
        // this class.
        lock (_gate)
        {
            // The token is checked above but not handed to Task.Run: constructing an
            // InferenceSession cannot be interrupted part-way, so passing it would only let a
            // cancelled task be cached as this labeller's one and only load.
            return _load ??= Task.Run(() => new SortformerModel(
                _options.ModelPath,
                new SortformerModelOptions
                {
                    IntraOpThreads = _options.IntraOpThreads,
                    EnableMemoryArena = _options.EnableMemoryArena,
                    EnableMemoryPattern = _options.EnableMemoryArena,
                }));
        }
    }

    public async Task<IReadOnlyList<SpeakerTurn>> LabelAsync(
        IAudioSource audio,
        SpeakerLabellingOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var model = await EnsureLoadedAsync(ct).ConfigureAwait(false);

        progress?.Report(new TranscriptionProgress
        {
            Stage = TranscriptionStage.LabellingSpeakers,
            Total = audio.Duration,
        });

        var probabilities = await Task.Run(
            () => RunAsync(model, audio, progress, ct), ct).ConfigureAwait(false);

        return SortformerPostProcessing.ToTurns(
            probabilities, SortformerGeometry.SpeakerCount, _options.PostProcessing);
    }

    /// <summary>
    /// The streaming loop: mel frames in, one graph call and one cache update per chunk, per-frame
    /// speaker activity out.
    /// </summary>
    private async Task<float[]> RunAsync(
        SortformerModel model, IAudioSource audio, IProgress<TranscriptionProgress>? progress, CancellationToken ct)
    {
        var mel = new MelStream();
        var cache = new ArrivalOrderSpeakerCache();
        var resampler = new Resampler(audio.SampleRate);
        var resampled = new List<float>();

        var enumerator = audio.ReadAsync(ct).GetAsyncEnumerator(ct);
        var exhausted = false;

        // Pulls audio until the requested frames exist or the source ends — which is also the moment
        // the recording's length becomes knowable, and the only moment the loop needs it.
        async ValueTask<int> PrepareAsync(int first, int count)
        {
            var available = mel.Prepare(first, count);
            while (available < count && !exhausted)
            {
                ct.ThrowIfCancellationRequested();

                if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    resampled.Clear();
                    resampler.Process(enumerator.Current.Span, resampled);
                    mel.Append(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(resampled));
                }
                else
                {
                    resampled.Clear();
                    resampler.Complete(resampled);
                    mel.Append(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(resampled));
                    mel.Complete();
                    exhausted = true;
                }

                available = mel.Prepare(first, count);
            }

            return available;
        }

        var collected = new List<float>();
        var chunkPredictions = new float[SortformerGeometry.ChunkLength * SortformerGeometry.SpeakerCount];
        var start = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Ask for the widest slice a step can want. Coming up short is how the loop learns
                // it has reached the end, so the totals below are read after this and not before.
                var leftOffset = Math.Min(SortformerGeometry.ChunkLeftContext * SortformerGeometry.SubsamplingFactor, start);
                await PrepareAsync(start - leftOffset, SortformerChunkPlan.MaximumWidth).ConfigureAwait(false);

                if (mel.PaddedFrames is { } padded && start >= padded)
                {
                    break;
                }

                var step = SortformerChunkPlan.Next(start, mel.PaddedFrames, mel.ValidFrames);

                // A chunk with no audio in it has nothing for the graph to report on, and the
                // reference's loop never reaches this state because it starts from a known length.
                // A recording shorter than one 10 ms hop does reach it — the padding rounds up to
                // sixteen mel frames of which none are real — and half a second of inference to be
                // told so is a waste rather than a wrong answer.
                if (step.ChunkLengthFrames == 0)
                {
                    break;
                }

                var features = model.Features;
                features.Clear();
                for (var i = 0; i < step.MelWidth; i++)
                {
                    mel.Frame(step.MelStart + i).CopyTo(features.Slice(i * SortformerGeometry.MelBands, SortformerGeometry.MelBands));
                }

                model.Run(step.ChunkLengthFrames, cache.Cache, cache.CacheFrames, cache.Fifo, cache.FifoFrames);

                var encoderFrames = model.EncoderFrames;
                if (encoderFrames - step.LeftContextEncoderFrames - step.RightContextEncoderFrames <= 0)
                {
                    // Nothing but context left. The reference's loop cannot reach past here either.
                    break;
                }

                var written = cache.Update(
                    model.Embeddings[..(encoderFrames * SortformerGeometry.EmbeddingDimension)],
                    encoderFrames,
                    model.Predictions,
                    step.LeftContextEncoderFrames,
                    step.RightContextEncoderFrames,
                    chunkPredictions);

                for (var i = 0; i < written * SortformerGeometry.SpeakerCount; i++)
                {
                    collected.Add(chunkPredictions[i]);
                }

                progress?.Report(new TranscriptionProgress
                {
                    Stage = TranscriptionStage.LabellingSpeakers,
                    Processed = TimeSpan.FromSeconds(step.End * SortformerGeometry.WindowStride / (double)SortformerGeometry.SampleRate),
                    Total = audio.Duration,
                });

                start = step.End;
                if (mel.PaddedFrames is { } total && start >= total)
                {
                    break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        // Predictions run to the end of the last whole chunk, which overshoots the audio; the model
        // reports on padding as readily as on silence.
        var valid = mel.ValidFrames ?? 0;
        var frames = Math.Min(
            (valid + SortformerGeometry.SubsamplingFactor - 1) / SortformerGeometry.SubsamplingFactor,
            collected.Count / SortformerGeometry.SpeakerCount);

        var result = new float[frames * SortformerGeometry.SpeakerCount];
        collected.CopyTo(0, result, 0, result.Length);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        var load = _load;
        _load = null;
        if (load is null)
        {
            return;
        }

        // Awaited rather than tested for completion: disposing while a load is still running would
        // otherwise leak the session it is about to produce. A load that failed has nothing to
        // dispose and its exception is not this method's to raise.
        try
        {
            (await load.ConfigureAwait(false)).Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }
}
