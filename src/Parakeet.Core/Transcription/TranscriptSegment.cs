namespace Parakeet.Core.Transcription;

/// <summary>One word with its timing and confidence, as reported by the engine.</summary>
public sealed record TranscriptWord
{
    public required string Text { get; init; }

    public required TimeSpan Start { get; init; }

    public required TimeSpan End { get; init; }

    /// <summary>Confidence in (0, 1]. Null when the engine does not report one.</summary>
    public float? Confidence { get; init; }

    public TimeSpan Duration => End - Start;

    /// <summary>Shifts the word by <paramref name="offset"/>; used to lift segment-relative
    /// times into file-relative ones.</summary>
    public TranscriptWord Shift(TimeSpan offset) => this with { Start = Start + offset, End = End + offset };
}

/// <summary>
/// A contiguous run of recognised speech. Times are relative to the start of the file.
/// </summary>
public sealed record TranscriptSegment
{
    public required TimeSpan Start { get; init; }

    public required TimeSpan End { get; init; }

    public required string Text { get; init; }

    public IReadOnlyList<TranscriptWord> Words { get; init; } = [];

    /// <summary>
    /// Index of the audio segment this text came from. Two transcript segments can share an
    /// index when one decode produced several sentences.
    /// </summary>
    public int SourceSegmentIndex { get; init; }

    public TimeSpan Duration => End - Start;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// Mean word confidence, or null when the engine reported none. Averaging is the right
    /// aggregate for "is this segment worth a second look"; it is not a probability.
    /// </summary>
    public float? MeanConfidence
    {
        get
        {
            double sum = 0;
            var n = 0;
            foreach (var word in Words)
            {
                if (word.Confidence is { } c)
                {
                    sum += c;
                    n++;
                }
            }

            return n == 0 ? null : (float)(sum / n);
        }
    }

    public TranscriptSegment Shift(TimeSpan offset) => this with
    {
        Start = Start + offset,
        End = End + offset,
        Words = Words.Count == 0 ? Words : [.. Words.Select(w => w.Shift(offset))],
    };
}
