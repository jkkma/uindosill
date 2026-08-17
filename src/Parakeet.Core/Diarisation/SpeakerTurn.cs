namespace Parakeet.Core.Diarisation;

/// <summary>
/// One stretch of speech attributed to one speaker, timed relative to the start of the file. The
/// unit both sides of the measurement share: a hand label exported from Audacity becomes a turn,
/// a diariser's output is a list of turns, and the scorer compares two such lists.
/// </summary>
/// <remarks>
/// Turns of different speakers may overlap — that is what crosstalk looks like, and the scorer
/// counts it rather than averaging it away. Turns of the <em>same</em> speaker overlapping is a
/// labelling error (a person cannot talk over themselves); the Audacity converter merges those,
/// and the scorer keeps them and says so, because pyannote.metrics — the reference implementation
/// this scorer is validated against — counts them twice and a validated number has to be the
/// same number.
/// </remarks>
public sealed record SpeakerTurn
{
    public required TimeSpan Start { get; init; }

    public required TimeSpan End { get; init; }

    /// <summary>The label as the source gave it: a name from a hand label, a cluster id from a diariser.</summary>
    public required string Speaker { get; init; }

    public TimeSpan Duration => End - Start;

    /// <summary>Shifts the turn by <paramref name="offset"/>, as <c>TranscriptWord.Shift</c> does for words.</summary>
    public SpeakerTurn Shift(TimeSpan offset) => this with { Start = Start + offset, End = End + offset };

    /// <summary>
    /// Rejects what no scorer can interpret: an end before its start, or a nameless speaker. A
    /// zero-length turn is allowed and contributes nothing — Audacity's point labels come through
    /// as one, and refusing a whole file for a stray click is the wrong trade.
    /// </summary>
    public void Validate()
    {
        if (End < Start)
        {
            throw new ArgumentException($"Speaker turn ends ({End}) before it starts ({Start}).");
        }

        if (string.IsNullOrWhiteSpace(Speaker))
        {
            throw new ArgumentException($"Speaker turn at {Start} has no speaker label.");
        }
    }
}

/// <summary>Helpers over a list of turns that more than one caller needs.</summary>
public static class SpeakerTurns
{
    /// <summary>
    /// Seconds to a <see cref="TimeSpan"/>, rounded to the nearest tick. <c>TimeSpan.FromSeconds</c>
    /// truncates instead, so a value that arithmetic left a hair under its decimal — an RTTM end
    /// computed as onset plus duration, say <c>10.200 + 8.100 = 18.299999…</c> — comes back one
    /// tick short, and a few hundred turns of that is a microsecond the scorer disagrees with
    /// pyannote.metrics by.
    /// </summary>
    public static TimeSpan FromSeconds(double seconds) =>
        TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));

    /// <summary>The distinct speaker labels, in order of first appearance by start time.</summary>
    public static IReadOnlyList<string> Speakers(IEnumerable<SpeakerTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var seen = new List<string>();
        foreach (var turn in turns.OrderBy(t => t.Start).ThenBy(t => t.End))
        {
            if (!seen.Contains(turn.Speaker, StringComparer.Ordinal))
            {
                seen.Add(turn.Speaker);
            }
        }

        return seen;
    }

    /// <summary>
    /// Replaces each raw label with a display name — <c>Speaker 1</c>, <c>Speaker 2</c>, … — numbered
    /// by first appearance, so the first voice heard is always Speaker 1 whatever a diariser's
    /// cluster ids happen to be. Deterministic for the same input; two files never share a numbering.
    /// </summary>
    public static IReadOnlyList<SpeakerTurn> RenameByFirstAppearance(
        IReadOnlyList<SpeakerTurn> turns, string format = "Speaker {0}")
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var speaker in Speakers(turns))
        {
            names[speaker] = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, names.Count + 1);
        }

        return [.. turns.Select(t => t with { Speaker = names[t.Speaker] })];
    }

    /// <summary>
    /// Merges turns of the same speaker that overlap, touch, or are separated by a gap of at most
    /// <paramref name="bridge"/>. With a zero bridge only overlaps and touches merge, which is the
    /// least a clean label set needs; the measurement plan's "bridge intra-speaker pauses under
    /// 0.3–0.5 s" is a larger value applied on purpose and recorded with the fixture.
    /// </summary>
    public static IReadOnlyList<SpeakerTurn> Merge(IEnumerable<SpeakerTurn> turns, TimeSpan bridge = default)
    {
        ArgumentNullException.ThrowIfNull(turns);
        if (bridge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bridge), bridge, "The bridge cannot be negative.");
        }

        var merged = new List<SpeakerTurn>();
        foreach (var group in turns.GroupBy(t => t.Speaker, StringComparer.Ordinal))
        {
            SpeakerTurn? current = null;
            foreach (var turn in group.OrderBy(t => t.Start).ThenBy(t => t.End))
            {
                if (current is null)
                {
                    current = turn;
                    continue;
                }

                if (turn.Start <= current.End + bridge)
                {
                    if (turn.End > current.End)
                    {
                        current = current with { End = turn.End };
                    }

                    continue;
                }

                merged.Add(current);
                current = turn;
            }

            if (current is not null)
            {
                merged.Add(current);
            }
        }

        return [.. merged.OrderBy(t => t.Start).ThenBy(t => t.End).ThenBy(t => t.Speaker, StringComparer.Ordinal)];
    }
}
