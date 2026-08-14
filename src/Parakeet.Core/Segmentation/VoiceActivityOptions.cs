namespace Parakeet.Core.Segmentation;

/// <summary>
/// Settings for cutting audio into decodable pieces.
/// </summary>
/// <remarks>
/// This is not a quality knob. Parakeet degrades on long single-pass audio and glues text
/// across chunk boundaries well before the point where it collapses, so a file-transcription
/// product that hands the model a whole recording produces quietly wrong output on exactly
/// the inputs it exists to serve.
/// </remarks>
public sealed record VoiceActivityOptions
{
    public static VoiceActivityOptions Default { get; } = new();

    /// <summary>
    /// Fixed-window mode: every frame is treated as speech, so segments grow to the cap and
    /// are cut at the quietest nearby frame. The escape hatch for material the energy
    /// detector mishandles — quiet speech, heavy background music, whispering.
    /// </summary>
    public static VoiceActivityOptions Disabled { get; } = new() { Enabled = false };

    public bool Enabled { get; init; } = true;

    /// <summary>Analysis frame length. 30 ms is short enough to place a boundary accurately.</summary>
    public TimeSpan FrameLength { get; init; } = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// How far a frame must sit above the running noise floor to count as speech.
    /// </summary>
    public float SpeechMarginDb { get; init; } = 8f;

    /// <summary>
    /// Absolute floor below which nothing counts as speech, whatever the adaptive threshold
    /// says. Without it, a digitally silent file with a dither floor at -90 dBFS reads as
    /// continuous speech.
    /// </summary>
    public float AbsoluteThresholdDb { get; init; } = -55f;

    /// <summary>
    /// A frame at or above this level is speech regardless of the adaptive threshold.
    /// </summary>
    /// <remarks>
    /// This ceiling is not a refinement, it is the fix for a failure that loses whole files. An
    /// adaptive floor with no upper bound learns from whatever it is fed: hand it a recording
    /// that starts at full level with no leading silence — a clip already trimmed to the
    /// speech, which is the common case — and it concludes that loud is the noise floor, sets
    /// the threshold above the speech, and returns nothing for the entire recording. No error,
    /// no warning, an empty transcript.
    /// </remarks>
    public float AbsoluteSpeechDb { get; init; } = -35f;

    /// <summary>Speech must persist this long before a segment opens (rejects clicks).</summary>
    public TimeSpan MinSpeechDuration { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Silence must persist this long before a segment closes (survives stop consonants).</summary>
    public TimeSpan MinSilenceDuration { get; init; } = TimeSpan.FromMilliseconds(420);

    /// <summary>Audio kept before the detected onset, so word onsets are not clipped.</summary>
    public TimeSpan PaddingBefore { get; init; } = TimeSpan.FromMilliseconds(240);

    /// <summary>Audio kept after the detected offset.</summary>
    public TimeSpan PaddingAfter { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Hard cap on one segment. Thirty seconds, per the brief: chunk-boundary text gluing is
    /// reported even at 2.5-minute chunks.
    /// </summary>
    public TimeSpan MaxSegmentLength { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A segment shorter than this is still emitted — dropping it would drop speech — but it
    /// is merged with the following one when they are adjacent.
    /// </summary>
    public TimeSpan MinSegmentLength { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// When the cap is reached, the cut is placed at the quietest frame within this window
    /// before the cap, which usually lands between words instead of through one.
    /// </summary>
    public TimeSpan ForcedSplitSearchWindow { get; init; } = TimeSpan.FromSeconds(4);

    public void Validate()
    {
        if (FrameLength <= TimeSpan.Zero || FrameLength > TimeSpan.FromMilliseconds(200))
        {
            throw new ArgumentOutOfRangeException(nameof(FrameLength), FrameLength, "Frame length must be in (0, 200] ms.");
        }

        if (MaxSegmentLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSegmentLength), MaxSegmentLength, "Segment cap must be positive.");
        }

        if (MaxSegmentLength < FrameLength * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSegmentLength), MaxSegmentLength, "Segment cap must hold at least four frames.");
        }

        // Refused rather than warned about. Chunk-boundary text gluing on Parakeet is reported
        // at 2.5-minute chunks and the model degrades outright past roughly 24 minutes, so a cap
        // in this range does not produce a slower transcript — it produces a wrong one.
        if (MaxSegmentLength > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSegmentLength),
                MaxSegmentLength,
                "Segment caps beyond five minutes are past the point where Parakeet is known to degrade. " +
                "The product cap is 30 seconds.");
        }

        if (PaddingBefore < TimeSpan.Zero || PaddingAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PaddingBefore), "Padding cannot be negative.");
        }

        if (ForcedSplitSearchWindow < TimeSpan.Zero || ForcedSplitSearchWindow >= MaxSegmentLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ForcedSplitSearchWindow), ForcedSplitSearchWindow, "Split search window must be shorter than the segment cap.");
        }

        if (AbsoluteSpeechDb <= AbsoluteThresholdDb)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AbsoluteSpeechDb),
                AbsoluteSpeechDb,
                "The definitely-speech level must sit above the nothing-below-here level.");
        }
    }
}
