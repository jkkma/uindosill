using System.Diagnostics;
using System.Runtime.CompilerServices;
using Parakeet.Core.Audio;
using Parakeet.Core.Segmentation;

namespace Parakeet.Core.Transcription;

/// <summary>Text a decoder produced for one audio segment, timed relative to that segment.</summary>
public sealed record DecodedSegment
{
    public required string Text { get; init; }

    /// <summary>Words with segment-relative timings. Empty when the decoder reported none.</summary>
    public IReadOnlyList<TranscriptWord> Words { get; init; } = [];
}

/// <summary>
/// Everything an engine needs around the model: read the source, cut it into pieces the model
/// handles reliably, decode in batches, lift segment-relative timings onto the file timeline,
/// and report progress. Written once here, so the real engine only has to know how to decode
/// and the fake engine exercises this whole path in CI with no weights present.
/// </summary>
public abstract class SegmentingTranscriptionEngine : ITranscriptionEngine
{
    public abstract EngineCapabilities Capabilities { get; }

    /// <summary>Segments handed to one decode call. Larger batches trade latency for throughput.</summary>
    protected virtual int BatchSize => 4;

    public abstract ValueTask LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Decodes a batch. Implementations return one entry per input segment, in order, with
    /// segment-relative word timings.
    /// </summary>
    protected abstract ValueTask<IReadOnlyList<DecodedSegment>> DecodeAsync(
        IReadOnlyList<AudioSegment> batch,
        TranscriptionOptions options,
        CancellationToken ct);

    /// <summary>
    /// Called once after the whole source has been segmented. Lets an implementation surface
    /// what segmentation saw — a file that is not silent but produced no segments is a result
    /// the user has to be told about, not an empty transcript.
    /// </summary>
    protected virtual void OnSegmentationCompleted(SegmentationReport report)
    {
        LastSegmentationReport = report;
    }

    /// <summary>Report from the most recent <see cref="TranscribeAsync"/> call, if any.</summary>
    public SegmentationReport? LastSegmentationReport { get; private set; }

    /// <summary>
    /// Time spent inside <see cref="DecodeAsync"/> over the most recent <see cref="TranscribeAsync"/>
    /// call, summed across its batches — the model's own share of the pass, as distinct from the
    /// wall-clock figure a caller takes around the whole of it.
    /// </summary>
    /// <remarks>
    /// The two differ, and on a fast backend they differ materially: the container decode, the
    /// mixdown, the resampling and the segmentation all run inside <c>TranscribeAsync</c>, before
    /// each batch and serialised with it, and none of them is the model. A 600 s AAC file through
    /// Media Foundation costs about 1.8 s of read alone on a laptop, which is a rounding error
    /// against 85 s of CPU decode and most of a 3.9 s CUDA one. Until 2026-08-22 only the wall
    /// figure existed and every document called it "decode time".
    /// </remarks>
    public TimeSpan? LastDecodeDuration { get; private set; }

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        IAudioSource audio,
        TranscriptionOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        LastDecodeDuration = TimeSpan.Zero;

        await LoadAsync(ct).ConfigureAwait(false);

        var vad = options.VoiceActivity with { MaxSegmentLength = options.MaxSegmentLength };
        var segmenter = new StreamingSegmenter(audio.SampleRate, vad);

        var completed = new List<AudioSegment>();
        var batch = new List<AudioSegment>(BatchSize);
        var segmentsDecoded = 0;

        progress?.Report(new TranscriptionProgress
        {
            Stage = TranscriptionStage.Reading,
            Total = audio.Duration,
        });

        await foreach (var block in audio.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            completed.Clear();
            segmenter.Push(block.Span, completed);

            foreach (var segment in completed)
            {
                batch.Add(segment);
                if (batch.Count < BatchSize)
                {
                    continue;
                }

                await foreach (var result in DecodeBatchAsync(batch, options, audio.Duration, progress, ct).ConfigureAwait(false))
                {
                    yield return result;
                }

                segmentsDecoded += batch.Count;
                batch.Clear();
            }
        }

        completed.Clear();
        segmenter.Flush(completed);
        batch.AddRange(completed);

        var report = segmenter.CreateReport();
        OnSegmentationCompleted(report);

        if (batch.Count > 0)
        {
            await foreach (var result in DecodeBatchAsync(batch, options, audio.Duration, progress, ct).ConfigureAwait(false))
            {
                yield return result;
            }

            segmentsDecoded += batch.Count;
            batch.Clear();
        }

        progress?.Report(new TranscriptionProgress
        {
            Stage = TranscriptionStage.Finalising,
            Processed = audio.Duration ?? report.TotalAudio,
            Total = audio.Duration ?? report.TotalAudio,
            SegmentsCompleted = segmentsDecoded,
            SegmentsTotal = segmentsDecoded,
        });
    }

    private async IAsyncEnumerable<TranscriptSegment> DecodeBatchAsync(
        List<AudioSegment> batch,
        TranscriptionOptions options,
        TimeSpan? total,
        IProgress<TranscriptionProgress>? progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Cancellation is checked between batches, never inside one. parakeet.cpp exposes no
        // abort hook, so a decode already running has to finish; what cancellation buys is that
        // nothing further is scheduled and the result is discarded.
        ct.ThrowIfCancellationRequested();

        var snapshot = batch.ToArray();

        // Timed here, around the model and nothing else, so the document can say how much of the
        // pass was the model's: the read, the resampling and the segmentation happen between these
        // calls, not inside them.
        var decodeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var decoded = await DecodeAsync(snapshot, options, ct).ConfigureAwait(false);
        LastDecodeDuration = (LastDecodeDuration ?? TimeSpan.Zero) + System.Diagnostics.Stopwatch.GetElapsedTime(decodeStarted);

        if (decoded.Count != snapshot.Length)
        {
            throw new InvalidOperationException(
                $"Engine returned {decoded.Count} results for {snapshot.Length} segments. " +
                "A batch decode that loses or invents entries silently corrupts the timeline.");
        }

        ct.ThrowIfCancellationRequested();

        for (var i = 0; i < snapshot.Length; i++)
        {
            var segment = snapshot[i];
            var result = decoded[i];
            var text = result.Text.Trim();

            if (text.Length > 0)
            {
                yield return new TranscriptSegment
                {
                    Start = segment.Start,
                    End = segment.End,
                    Text = text,
                    SourceSegmentIndex = segment.Index,
                    Words = result.Words.Count == 0
                        ? []
                        : [.. result.Words.Select(w => w.Shift(segment.Start))],
                };
            }

            progress?.Report(new TranscriptionProgress
            {
                Stage = TranscriptionStage.Decoding,
                Processed = segment.End,
                Total = total,
                SegmentsCompleted = segment.Index + 1,
            });
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Runs a decode over a short burst of near-silent dither so the first real decode is not
    /// paying for arena allocation and graph construction.
    /// </summary>
    /// <remarks>
    /// Without this every benchmark's first number is inflated, which makes your own figures
    /// exactly as unreliable as the vendor marketing they were meant to replace. Cold load time
    /// is reported separately rather than folded in.
    /// </remarks>
    protected async ValueTask WarmUpAsync(
        int sampleRate,
        TranscriptionOptions options,
        TimeSpan? duration = null,
        CancellationToken ct = default)
    {
        var seconds = (duration ?? TimeSpan.FromMilliseconds(500)).TotalSeconds;
        var samples = new float[Math.Max(1, (int)(sampleRate * seconds))];
        AudioMath.FillDither(samples);

        var segment = new AudioSegment
        {
            Index = 0,
            SampleRate = sampleRate,
            Start = TimeSpan.Zero,
            Samples = samples,
            SpeechDetected = false,
        };

        var stopwatch = Stopwatch.StartNew();
        _ = await DecodeAsync([segment], options, ct).ConfigureAwait(false);
        WarmUpDuration = stopwatch.Elapsed;
    }

    /// <summary>How long the warm-up decode took, once it has run.</summary>
    public TimeSpan? WarmUpDuration { get; private set; }
}
