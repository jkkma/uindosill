using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tidying;

/// <summary>
/// What one request to the tidying model carries. The unit is the lever on the tidy's pace —
/// a request costs its own prefill plus a decode that mostly copies its input, so the pace is
/// bounded by the request count more than by the words — and which unit ships is decided by
/// measurement (docs/PHASES.md, *Decided 2026-09-02, late evening*). The segment is the shipped
/// shape until then; the other two are behind the measurement's options.
/// </summary>
public enum TidyUnitKind
{
    /// <summary>One recogniser segment per request.</summary>
    Segment,

    /// <summary>
    /// Consecutive whole segments, closed when the run holds at least
    /// <see cref="TidyUnitShaper.JoinedRunSeconds"/> of speech or the file ends.
    /// </summary>
    JoinedRun,

    /// <summary>
    /// Pieces cut at sentence-final words by <see cref="SentenceSplitter"/>'s own rule and joined
    /// across segment boundaries until a sentence ends, capped at
    /// <see cref="TidyUnitShaper.SentenceRunCapSeconds"/> of speech.
    /// </summary>
    SentenceRun,
}

/// <summary>Whether the tidy runs beside the recogniser or after it.</summary>
public enum TidyShape
{
    /// <summary>The stage over the segment stream, fed as the recogniser produces: the shipped shape.</summary>
    Tandem,

    /// <summary>The pass over the finished transcript, every segment enqueued at once.</summary>
    Pass,
}

/// <summary>A word range of one source segment, as one unit carries it.</summary>
/// <param name="Index">The segment's index in the stage — the one <see cref="TidyStage.Enqueue"/> returned.</param>
/// <param name="Segment">The source segment, untouched.</param>
/// <param name="WordStart">First word of the range, into <see cref="TranscriptSegment.Words"/>.</param>
/// <param name="WordCount">Words in the range. Zero for a segment without verified word timings, which is always carried whole.</param>
/// <param name="Ordinal">Which piece of its segment this is, the first being 0.</param>
public sealed record TidyPiece(int Index, TranscriptSegment Segment, int WordStart, int WordCount, int Ordinal)
{
    /// <summary>True when the piece is the whole segment — every piece under <see cref="TidyUnitKind.Segment"/> and <see cref="TidyUnitKind.JoinedRun"/>.</summary>
    public bool IsWholeSegment => WordStart == 0 && WordCount == Segment.Words.Count;

    /// <summary>The words of the range, in order; empty for an untimed segment.</summary>
    public IEnumerable<TranscriptWord> Words => Segment.Words.Skip(WordStart).Take(WordCount);

    /// <summary>The range's span: first word to last, or the segment's own span when carried whole.</summary>
    public TimeSpan Speech
    {
        get
        {
            if (WordCount == 0 || IsWholeSegment)
            {
                return Segment.Duration;
            }

            var words = Segment.Words;
            return words[WordStart + WordCount - 1].End - words[WordStart].Start;
        }
    }
}

/// <summary>One request's worth of transcript: its pieces and the one segment the contract judges.</summary>
public sealed record TidyUnit
{
    /// <summary>The pieces in order, consecutive in the transcript.</summary>
    public required IReadOnlyList<TidyPiece> Pieces { get; init; }

    /// <summary>
    /// What is sent and judged: the pieces' words in order, their texts joined by single spaces,
    /// on the span from the first piece to the last. A unit of one whole segment is that segment
    /// itself, so the shipped shape goes through the contract exactly as it did before units.
    /// </summary>
    public required TranscriptSegment Composite { get; init; }

    public bool IsSingleWholeSegment => Pieces.Count == 1 && Pieces[0].IsWholeSegment;

    /// <summary>Spoken words carried, by the pieces' word ranges; for an untimed segment, the words of its text.</summary>
    public int WordCount => Composite.Words.Count > 0
        ? Composite.Words.Count
        : Composite.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Speech time carried: the sum of the pieces' spans, pauses between segments left out.</summary>
    public TimeSpan Speech
    {
        get
        {
            var total = TimeSpan.Zero;
            foreach (var piece in Pieces)
            {
                total += piece.Speech;
            }

            return total;
        }
    }

    /// <summary>The unit of one whole segment, which the contract judges as the segment itself.</summary>
    public static TidyUnit Whole(int index, TranscriptSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new TidyUnit
        {
            Pieces = [new TidyPiece(index, segment, 0, segment.Words.Count, 0)],
            Composite = segment,
        };
    }

    /// <summary>A unit of several timed pieces, or of one piece that is not its whole segment.</summary>
    public static TidyUnit Of(IReadOnlyList<TidyPiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (pieces.Count == 0)
        {
            throw new ArgumentException("A unit carries at least one piece.", nameof(pieces));
        }

        if (pieces.Count == 1 && pieces[0].IsWholeSegment)
        {
            return Whole(pieces[0].Index, pieces[0].Segment);
        }

        var words = new List<TranscriptWord>();
        foreach (var piece in pieces)
        {
            if (piece.WordCount == 0)
            {
                throw new ArgumentException("A segment without verified word timings is carried whole, never joined.", nameof(pieces));
            }

            words.AddRange(piece.Words);
        }

        var first = pieces[0];
        var last = pieces[^1];
        return new TidyUnit
        {
            Pieces = pieces,
            Composite = new TranscriptSegment
            {
                Start = first.WordStart == 0 ? first.Segment.Start : words[0].Start,
                End = last.WordStart + last.WordCount == last.Segment.Words.Count ? last.Segment.End : words[^1].End,
                Text = string.Join(' ', words.Select(w => w.Text.Trim())),
                Words = words,
                SourceSegmentIndex = first.Segment.SourceSegmentIndex,
                Speaker = first.Segment.Speaker,
            },
        };
    }
}

/// <summary>
/// Cuts the segment stream into units of one kind, in arrival order, and says at each segment
/// how many pieces that segment will be carried in — so the stage knows how many outcomes to
/// wait for before the segment's own outcome can be assembled.
/// </summary>
/// <remarks>
/// <para>
/// A segment without verified word timings cannot be joined or cut: it closes whatever unit is
/// open and travels as a unit of its own, under every kind. An empty segment never reaches the
/// shaper; the stage passes it through without a request.
/// </para>
/// <para>
/// Under <see cref="TidyUnitKind.SentenceRun"/>, whether a segment's last word ends a sentence
/// depends on the word after it — the splitter's rule reads the next word's first character — and
/// that word is in the next segment, so the decision waits for it: the open unit is closed when
/// the next segment arrives and its first word says the previous one ended a sentence, or when
/// the stream ends. The 30-second cap is measured in speech carried, the pieces' spans summed,
/// so that a speaker who never ends a sentence in the recogniser's punctuation cannot make one
/// request grow without bound.
/// </para>
/// </remarks>
public sealed class TidyUnitShaper
{
    /// <summary>Speech a joined run holds before it closes.</summary>
    public const double JoinedRunSeconds = 15;

    /// <summary>Speech a sentence-run may hold before it closes on the cap rather than a sentence end.</summary>
    public const double SentenceRunCapSeconds = 30;

    private readonly TidyUnitKind _kind;
    private readonly List<TidyPiece> _open = [];
    private TimeSpan _openSpeech;

    public TidyUnitShaper(TidyUnitKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown unit kind.");
        }

        _kind = kind;
    }

    public TidyUnitKind Kind => _kind;

    /// <summary>
    /// Takes the next non-empty segment and returns the units it completed, in order — none,
    /// one, or several under <see cref="TidyUnitKind.SentenceRun"/>. <paramref name="pieces"/>
    /// is how many pieces the segment was cut into, every one of which is in a unit returned now
    /// or later.
    /// </summary>
    public IReadOnlyList<TidyUnit> Push(int index, TranscriptSegment segment, out int pieces)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var completed = new List<TidyUnit>();

        if (_kind == TidyUnitKind.Segment || !segment.WordsReproduceText())
        {
            Close(completed);
            completed.Add(TidyUnit.Whole(index, segment));
            pieces = 1;
            return completed;
        }

        if (_kind == TidyUnitKind.JoinedRun)
        {
            _open.Add(new TidyPiece(index, segment, 0, segment.Words.Count, 0));
            _openSpeech += segment.Duration;
            if (_openSpeech >= TimeSpan.FromSeconds(JoinedRunSeconds))
            {
                Close(completed);
            }

            pieces = 1;
            return completed;
        }

        // Sentence-runs. First the decision the previous segment's last word was waiting for.
        var words = segment.Words;
        if (_open.Count > 0)
        {
            var previous = _open[^1];
            var lastWord = previous.Segment.Words[previous.WordStart + previous.WordCount - 1].Text;
            if (SentenceSplitter.EndsSentence(lastWord, words[0].Text))
            {
                Close(completed);
            }
        }

        var pieceStart = 0;
        var ordinal = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var last = i == words.Count - 1;
            var pieceSpan = words[i].End - words[pieceStart].Start;
            var endsSentence = !last && SentenceSplitter.EndsSentence(words[i].Text, words[i + 1].Text);
            var capped = _openSpeech + pieceSpan >= TimeSpan.FromSeconds(SentenceRunCapSeconds);

            if (!endsSentence && !capped && !last)
            {
                continue;
            }

            var piece = new TidyPiece(index, segment, pieceStart, i + 1 - pieceStart, ordinal++);
            _open.Add(piece);
            _openSpeech += piece.Speech;
            pieceStart = i + 1;

            if (endsSentence || capped)
            {
                Close(completed);
            }

            // A trailing piece stays open: whether its last word ended a sentence is the next
            // segment's first word's to say.
        }

        pieces = ordinal;
        return completed;
    }

    /// <summary>The stream has ended: the unit still open, if any.</summary>
    public IReadOnlyList<TidyUnit> Flush()
    {
        var completed = new List<TidyUnit>();
        Close(completed);
        return completed;
    }

    private void Close(List<TidyUnit> into)
    {
        if (_open.Count == 0)
        {
            return;
        }

        into.Add(TidyUnit.Of([.. _open]));
        _open.Clear();
        _openSpeech = TimeSpan.Zero;
    }
}

/// <summary>What one unit cost and came to, for the pace measurement; one per request the stage made.</summary>
public sealed record TidyUnitTrace
{
    /// <summary>The request's number in the stage, from 0.</summary>
    public required int Ordinal { get; init; }

    /// <summary>The stage indices of the segments the unit carried pieces of.</summary>
    public required IReadOnlyList<int> Segments { get; init; }

    public required int Pieces { get; init; }

    /// <summary>Spoken words carried.</summary>
    public required int Words { get; init; }

    /// <summary>Speech time carried.</summary>
    public required TimeSpan Speech { get; init; }

    /// <summary>When the unit closed and was queued, on the stage's clock.</summary>
    public required TimeSpan EnqueuedAt { get; init; }

    /// <summary>When a worker sent it to the model.</summary>
    public required TimeSpan StartedAt { get; init; }

    /// <summary>When its outcome came back through the contract.</summary>
    public required TimeSpan LandedAt { get; init; }

    public required bool Accepted { get; init; }

    public string? Refusal { get; init; }
}
