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
    /// False when nothing affirmed speech in this segment: a fixed window, where every frame is
    /// speech by fiat, or a synthetic segment such as the warm-up's. A forced cut at the cap
    /// mid-utterance stays true — the clock placed the cut, but the detector or the gate
    /// affirmed the content. Informational only: nothing reads it today, and both kinds are
    /// decoded either way.
    /// </summary>
    public bool SpeechDetected { get; init; } = true;

    public TimeSpan Duration => AudioMath.SamplesToTime(Samples.Length, SampleRate);

    public TimeSpan End => Start + Duration;

    public override string ToString() =>
        $"#{Index} {Start:hh\\:mm\\:ss\\.fff}–{End:hh\\:mm\\:ss\\.fff} ({Samples.Length} samples)";
}

/// <summary>What segmentation actually did, so a bad threshold is visible instead of silent.</summary>
public sealed record SegmentationReport
{
    /// <summary>
    /// What decided where speech was: <see cref="StreamingSegmenter.EnergyGateName"/>, the
    /// <see cref="ISpeechDetector.Name"/> of the detector that replaced it, or
    /// <see cref="StreamingSegmenter.FixedWindowsName"/> when nothing did. A segment boundary is a
    /// fact about whichever of the two cut it, and a report that did not say which would be quoting
    /// a figure without its method.
    /// </summary>
    public string SpeechDetector { get; init; } = StreamingSegmenter.EnergyGateName;

    public int SegmentCount { get; init; }

    /// <summary>Audio seen by the segmenter.</summary>
    public TimeSpan TotalAudio { get; init; }

    /// <summary>Audio actually emitted in segments. Less than the total: silence is dropped.</summary>
    public TimeSpan SegmentedAudio { get; init; }

    /// <summary>Audio whose frames were above the speech threshold.</summary>
    public TimeSpan SpeechAudio { get; init; }

    /// <summary>
    /// Audio above <see cref="AudibleThresholdDb"/> that the adaptive gate kept out of every
    /// segment — and so out of the decoder. An energy detector cannot tell quiet speech from a
    /// fan, so this is a count of either, reported rather than guessed at;
    /// <see cref="UnsegmentedAudibleIsMaterial"/> says when it is worth a sentence.
    /// </summary>
    public TimeSpan UnsegmentedAudibleAudio { get; init; }

    /// <summary>The absolute line, in dBFS, that <see cref="UnsegmentedAudibleAudio"/> counts above.</summary>
    public float AudibleThresholdDb { get; init; } = -55f;

    /// <summary>
    /// True when <see cref="UnsegmentedAudibleAudio"/> is worth telling the user about: at least a
    /// second of it, at least a tenth of what was segmented, and segments to compare against — the
    /// empty-transcript case has its own sentence.
    /// </summary>
    /// <remarks>
    /// The bar is a design choice, not a measurement. The breath between two utterances sits above
    /// the line on most recordings and must not earn a sentence on every one of them; the first
    /// phrase lost to a gate that opened too high, or a quiet stretch after a loud one, must.
    /// </remarks>
    public bool UnsegmentedAudibleIsMaterial =>
        SegmentCount > 0
        && UnsegmentedAudibleAudio >= TimeSpan.FromSeconds(1)
        && UnsegmentedAudibleAudio.Ticks * 10 >= SegmentedAudio.Ticks;

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
