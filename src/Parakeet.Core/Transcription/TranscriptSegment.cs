namespace Parakeet.Core.Transcription;

/// <summary>One word with its timing and confidence, as reported by the engine.</summary>
public sealed record TranscriptWord
{
    public required string Text { get; init; }

    public required TimeSpan Start { get; init; }

    public required TimeSpan End { get; init; }

    /// <summary>Confidence in (0, 1]. Null when the engine does not report one.</summary>
    public float? Confidence { get; init; }

    /// <summary>
    /// Who said it, when a speaker labeller has run and attributed this word; null otherwise. A
    /// display name (<c>Speaker 1</c>) or a diariser's own label, exactly as
    /// <c>SpeakerAssignment</c> left it. Every formatter prints nothing about speakers while this
    /// is null, so a transcript made without the opt-in is byte-identical to what it always was.
    /// </summary>
    public string? Speaker { get; init; }

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
    /// index when one decode produced several sentences, or when speaker labelling cut one
    /// segment where the speaker changed.
    /// </summary>
    public int SourceSegmentIndex { get; init; }

    /// <summary>
    /// The one speaker this segment belongs to, when a labeller has run; null otherwise. After
    /// <c>SpeakerAssignment</c> a segment never spans a speaker change unless its words could not
    /// be cut apart, in which case this is the speaker of most of it and the words say the rest.
    /// </summary>
    public string? Speaker { get; init; }

    public TimeSpan Duration => End - Start;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// Whether joining <see cref="Words"/>' trimmed texts with single spaces reproduces
    /// <see cref="Text"/> exactly — the condition under which the segment may be cut between two
    /// of its words and the text carved to match. True of every segment the real engine has
    /// produced here (1,378 of 1,378 in one three-hour transcript) and of the fake; false of a
    /// segment with no words at all.
    /// </summary>
    /// <remarks>
    /// One definition, because two callers cut segments on it — <c>SpeakerAssignment</c> where the
    /// speaker changes and <c>SentenceSplitter</c> where a sentence ends — and a segment that one
    /// of them judged cuttable and the other did not would be cut in one place and whole in the
    /// other, which is a defect nothing would report.
    /// </remarks>
    public bool WordsReproduceText() => WordsReproduceText(Words, Text);

    /// <summary>The same test over a word list that is not yet the segment's — attributed copies, say.</summary>
    public static bool WordsReproduceText(IReadOnlyList<TranscriptWord> words, string text)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(text);

        return words.Count > 0
            && string.Equals(string.Join(' ', words.Select(w => w.Text.Trim())), text.Trim(), StringComparison.Ordinal);
    }

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
