using Parakeet.Core.Audio;

namespace Parakeet.Core.Segmentation;

/// <summary>
/// A piece of audio small enough to hand to the model in one call, with the absolute time
/// it starts at so decoded word timings can be lifted back onto the file's timeline.
/// </summary>
public sealed class AudioSegment
{
    public required int Index { get; init; }

    public required int SampleRate { get; init; }

    /// <summary>Offset of this segment from the start of the source.</summary>
    public required TimeSpan Start { get; init; }

    /// <summary>Mono float32 samples. Contiguous, so it can be pinned for interop.</summary>
    public required ReadOnlyMemory<float> Samples { get; init; }

    /// <summary>
    /// False when the segment was emitted by a forced cut or a fixed window rather than by
    /// detected speech. Only used for reporting; both kinds are decoded.
    /// </summary>
    public bool SpeechDetected { get; init; } = true;

    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);

    public TimeSpan End => Start + Duration;

    public override string ToString() =>
        $"#{Index} {Start:hh\\:mm\\:ss\\.fff}–{End:hh\\:mm\\:ss\\.fff} ({Samples.Length} samples)";
}

/// <summary>What segmentation actually did, so a bad threshold is visible instead of silent.</summary>
public sealed record SegmentationReport
{
    public int SegmentCount { get; init; }

    /// <summary>Audio seen by the segmenter.</summary>
    public TimeSpan TotalAudio { get; init; }

    /// <summary>Audio actually emitted in segments. Less than the total: silence is dropped.</summary>
    public TimeSpan SegmentedAudio { get; init; }

    /// <summary>Audio whose frames were above the speech threshold.</summary>
    public TimeSpan SpeechAudio { get; init; }

    /// <summary>Every sample seen was exactly zero.</summary>
    public bool IsDigitalSilence { get; init; }

    public float PeakDb { get; init; } = AudioMath.SilenceFloorDb;

    public float NoiseFloorDb { get; init; } = AudioMath.SilenceFloorDb;

    public bool AnySpeechDetected => SegmentCount > 0;

    /// <summary>
    /// True when the file clearly contains sound but the detector found no speech in it.
    /// The caller must say so out loud and offer the fixed-window path: an empty transcript
    /// with no explanation is the failure this product exists to avoid.
    /// </summary>
    public bool LooksLikeMissedSpeech => !AnySpeechDetected && !IsDigitalSilence && PeakDb > -50f;
}
