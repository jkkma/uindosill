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
    /// A speech detector to cut on in place of the energy gate, or null for the gate. The loaded
    /// model, owned by the caller and shared across a batch; the engine opens one stream on it per
    /// recording, at the recording's own rate, and closes that stream with the recording.
    /// </summary>
    /// <remarks>
    /// An object on an options record is unusual here and is the honest shape: the detector is a
    /// resource with a lifetime, not a value, and threading it through the options is what lets
    /// <see cref="SegmentingTranscriptionEngine"/> — which knows nothing about ONNX Runtime — hand
    /// it to the segmenter without a second seam. <see cref="VoiceActivityOptions.SpeechProbability"/>
    /// and its sibling are the thresholds it is read against.
    /// </remarks>
    public Segmentation.ISpeechDetector? SpeechDetector { get; init; }

    /// <summary>
    /// Diagnostics only. Leave null. See <see cref="BeamSearchOptions"/> for why.
    /// </summary>
    public BeamSearchOptions? BeamSearch { get; init; }

    /// <summary>
    /// Words below this confidence are reported as suspect by <see cref="TranscriptDocument"/>.
    /// Null disables flagging.
    /// </summary>
    public float? LowConfidenceThreshold { get; init; } = 0.45f;

    /// <summary>
    /// The voice-activity settings the audio is actually cut under: <see cref="VoiceActivity"/>
    /// re-capped to <see cref="MaxSegmentLength"/>, with the forced-split search window shrunk to
    /// fit when the cap comes in under it. The window is a cut-placement heuristic — where in a
    /// full segment's tail to look for a quiet frame — and the segmenter clamps it to the cap
    /// anyway; deriving it here is what lets a three-second cap mean a three-second cap instead
    /// of an error naming a knob nobody touched. The rescue cannot tell a defaulted window from
    /// one the caller set past the cap on purpose: both shrink, because the segmenter clamps
    /// both the same way, and the shrunk value is what the cut actually searches.
    /// </summary>
    public VoiceActivityOptions SegmentationOptions() => VoiceActivity with
    {
        MaxSegmentLength = MaxSegmentLength,
        ForcedSplitSearchWindow = VoiceActivity.ForcedSplitSearchWindow < MaxSegmentLength
            ? VoiceActivity.ForcedSplitSearchWindow
            : MaxSegmentLength - VoiceActivity.FrameLength,
    };

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

        // Validated as derived, not as handed in: the audio is cut under SegmentationOptions() —
        // this record's VoiceActivity re-capped to MaxSegmentLength — and until 2026-08-29 the
        // raw record was checked here while the derived one was checked only by the segmenter,
        // inside the decode iterator, after the model had loaded. A cap of four seconds or less
        // passed here and threw there, naming ForcedSplitSearchWindow — a knob the caller never
        // set, and one the segmenter clamps to fit regardless. The derived check covers every
        // field the raw one did; the one failure the derivation itself can produce (a cap too
        // short to hold four detector frames) is re-attributed to the cap, which is what was
        // actually set.
        try
        {
            SegmentationOptions().Validate();
        }
        catch (ArgumentOutOfRangeException exc) when (exc.ParamName == nameof(MaxSegmentLength))
        {
            // The derived record's own sentence, but attributed once: AOORE appends the parameter
            // and the value to Message, so passing the whole of it through would print both twice.
            throw new ArgumentOutOfRangeException(
                nameof(MaxSegmentLength), MaxSegmentLength, exc.Message.Split(" (Parameter", 2)[0]);
        }
    }
}
