using Parakeet.Core.Segmentation;

namespace Parakeet.Core.Transcription;

/// <summary>Which decoder head to run.</summary>
public enum DecoderMode
{
    /// <summary>Let the engine pick by model architecture. Almost always right.</summary>
    Default = 0,

    /// <summary>Force the CTC head.</summary>
    Ctc = 1,

    /// <summary>Force the transducer (TDT/RNN-T) head.</summary>
    Transducer = 2,
}

/// <summary>
/// Beam search settings. Deliberately not surfaced to end users: measured across 80 real
/// production captures, beam search on Parakeet TDT changed 19 transcripts and every
/// single change was a loss (dropped closing sentences, dropped list items, and a word
/// invented over near-silence that greedy correctly returned empty). Greedy is the product
/// behaviour; this exists so the difference can be reproduced in diagnostics.
/// </summary>
public sealed record BeamSearchOptions
{
    public required int BeamSize { get; init; }

    public int NBest { get; init; } = 1;

    /// <summary>Match NeMo's default score/sequence-length ranking.</summary>
    public bool ScoreNormalisation { get; init; } = true;
}

public sealed record TranscriptionOptions
{
    public static TranscriptionOptions Default { get; } = new();

    public DecoderMode Decoder { get; init; } = DecoderMode.Default;

    /// <summary>
    /// Locale hint for prompt-conditioned multilingual models ("en", "de", "auto").
    /// Null uses the model default. Ignored by models without a language prompt.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Decode threads. Null asks <see cref="DecodeThreadPlanner"/> for a sane default;
    /// past roughly eight threads the returns flatten while UI responsiveness suffers.
    /// </summary>
    /// <remarks>
    /// Honoured only by engines whose <see cref="EngineCapabilities.SupportsThreadCount"/> is
    /// true. The parakeet.cpp ABI has no thread parameter, so on that engine this is recorded
    /// and reported but does not reach the decoder.
    /// </remarks>
    public int? ThreadCount { get; init; }

    /// <summary>
    /// Hard cap on the audio handed to one decode call. Not a tuning knob: Parakeet
    /// degrades on long single-pass audio, so a file-transcription product that does not
    /// segment produces quietly wrong output on exactly the inputs it exists to serve.
    /// </summary>
    public TimeSpan MaxSegmentLength { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Ask for per-word timestamps. Required for SRT/VTT and confidence flagging.</summary>
    public bool WordTimestamps { get; init; } = true;

    /// <summary>Voice-activity settings used to cut the audio into decodable segments.</summary>
    public VoiceActivityOptions VoiceActivity { get; init; } = VoiceActivityOptions.Default;

    /// <summary>
    /// Diagnostics only. Leave null. See <see cref="BeamSearchOptions"/> for why.
    /// </summary>
    public BeamSearchOptions? BeamSearch { get; init; }

    /// <summary>
    /// Words below this confidence are reported as suspect by <see cref="TranscriptDocument"/>.
    /// Null disables flagging.
    /// </summary>
    public float? LowConfidenceThreshold { get; init; } = 0.45f;

    public void Validate()
    {
        if (MaxSegmentLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSegmentLength), MaxSegmentLength, "Maximum segment length must be positive.");
        }

        if (MaxSegmentLength > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSegmentLength),
                MaxSegmentLength,
                "Segments longer than five minutes are past the point where Parakeet is known to degrade; " +
                "the product cap is 30 seconds.");
        }

        if (ThreadCount is { } threads && threads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ThreadCount), threads, "Thread count must be at least one.");
        }

        if (LowConfidenceThreshold is { } threshold && (threshold < 0f || threshold > 1f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(LowConfidenceThreshold), threshold, "Confidence threshold must be within [0, 1].");
        }

        if (BeamSearch is { } beam)
        {
            if (beam.BeamSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(BeamSearch), beam.BeamSize, "Beam size must be at least one.");
            }

            if (beam.NBest < 1 || beam.NBest > beam.BeamSize)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(BeamSearch), beam.NBest, "N-best must satisfy 1 <= nbest <= beam size.");
            }
        }

        VoiceActivity.Validate();
    }
}
