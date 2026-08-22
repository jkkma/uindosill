using System.Globalization;

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

/// <summary>
/// One merge <see cref="SpeakerTurns.FoldDownTo(IReadOnlyList{SpeakerTurn}, int, out IReadOnlyList{SpeakerFold})"/>
/// made, with the evidence it made it on: which label was absorbed into which, how long the two
/// spent talking over each other, and how far behind the next-closest pair was.
/// </summary>
/// <remarks>
/// <para>
/// A record rather than the sentence it renders, because the same fold is owed to three audiences
/// that cannot share a string: the command line prints it, the window puts it in a warning, and a
/// saved transcript carries it as provenance a reader queries months later. <see cref="Describe"/>
/// is the one place the sentence is built, so the line a user reads and the numbers an archived
/// JSON holds cannot drift apart.
/// </para>
/// <para>
/// <b>Both labels are the labeller's own, before any display renaming.</b> The fold runs on the
/// diariser's cluster ids and <see cref="SpeakerTurns.RenameByFirstAppearance"/> runs after it, so
/// <see cref="Dropped"/> names something that no longer exists by the time the transcript is
/// written — its turns are under <see cref="Kept"/>. Renaming half the pair would be the only
/// alternative, since a label that was merged away never earns a display name, and a record that
/// mixed two vocabularies would be worse than one that states which vocabulary it is in.
/// </para>
/// </remarks>
public sealed record SpeakerFold
{
    /// <summary>The label that was merged away. Its turns now carry <see cref="Kept"/>.</summary>
    public required string Dropped { get; init; }

    /// <summary>The label that survived — whichever of the pair held more speech.</summary>
    public required string Kept { get; init; }

    /// <summary>Seconds the two labels were active at the same instant.</summary>
    public required double OverlapSeconds { get; init; }

    /// <summary>
    /// The next-closest pair's overlap in seconds, or null when the pair merged was the only one
    /// there was. This is the margin, and it — not <see cref="OverlapSeconds"/> on its own — is
    /// what says whether the fold had a real choice to make: two hosts of a three-hour recording
    /// overlap for minutes however the labels are cut, so an alarming absolute can still be the
    /// clearest pair in the file by a factor of two.
    /// </summary>
    public double? RunnerUpSeconds { get; init; }

    /// <summary>
    /// The sentence a user reads, in the invariant culture. These seconds are quoted back in bug
    /// reports and printed beside run summaries that are invariant too, so a decimal separator
    /// taken from the operator's locale would leave one run's own output disagreeing with itself.
    /// </summary>
    public string Describe()
    {
        var margin = RunnerUpSeconds is not { } runnerUp
            ? "no other pair to compare it with"
            : OverlapSeconds > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"the next-closest pair overlapped {runnerUp:F1} s, {runnerUp / OverlapSeconds:F1}x more")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"the next-closest pair overlapped {runnerUp:F1} s");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"'{Dropped}' into '{Kept}' (they talked over each other for {OverlapSeconds:F1} s; {margin})");
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
    public static TimeSpan FromSeconds(double seconds) => Parakeet.Core.Audio.AudioMath.SecondsToTime(seconds);

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
            names[speaker] = string.Format(CultureInfo.InvariantCulture, format, names.Count + 1);
        }

        return [.. turns.Select(t => t with { Speaker = names[t.Speaker] })];
    }

    /// <summary>
    /// Folds the label set down to at most <paramref name="cap"/> speakers by repeatedly merging the
    /// two that talk over each other least. Returns <paramref name="turns"/> unchanged when it
    /// already holds <paramref name="cap"/> or fewer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this repairs.</b> The streaming diariser holds about 3.5 seconds of audio per speaker
    /// in a cache it re-selects every step, with no long-term anchor, so over a long recording one
    /// person's identity can drift far enough to claim a second slot — measured 2026-08-20 on this
    /// project's own podcasts, where two hosts came back as three substantial clusters and three
    /// speakers as four. The failure is always **over**-segmentation, and over-segmentation is the
    /// kind that can be repaired afterwards: two labels can be merged, where one label cannot be
    /// split back into two people.
    /// </para>
    /// <para>
    /// <b>Least collision is the criterion, and it is the only evidence available.</b> No speaker
    /// embeddings exist here — the ONNX graph returns per-frame activity for four slots and nothing
    /// that identifies a voice — so the question "are these two labels the same person" has to be
    /// answered from the timeline. Two labels that are really one person are **never active at the
    /// same instant**, because the drifted identity replaced the original rather than joining it;
    /// two different people in conversation collide constantly, on back-channel alone. So the pair
    /// with the least simultaneous speech is the pair to merge.
    /// </para>
    /// <para>
    /// <b>Its failure mode, stated rather than hidden, and it is not hypothetical.</b> Two real
    /// speakers who never once overlap look exactly like one drifted speaker to this rule. On the 18
    /// AMI development meetings the least-colliding pair of genuinely different speakers overlaps by
    /// 0.0 s in <c>IS1008a</c> — one meeting in eighteen where an automatic version of this would
    /// merge two people. So it <b>never fires on its own</b>: it runs only when a caller supplies a
    /// cap, the cap is the user's own <see cref="SpeakerLabellingOptions.SpeakerCount"/>, and
    /// nothing here tries to infer one from the audio. The rest of that distribution is 2.8 s to
    /// 57.6 s, so the signal is real — it is just not clean enough to act on unasked.
    /// </para>
    /// <para>
    /// <b>It is a no-op wherever the model was already within the cap</b>, which on the 18 AMI
    /// development meetings is every one of them: all four speakers, cap four, nothing merged, DER
    /// unchanged. That is what makes it safe to ship against a passed gate.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SpeakerTurn> FoldDownTo(IReadOnlyList<SpeakerTurn> turns, int cap) =>
        FoldDownTo(turns, cap, out _);

    /// <summary>
    /// As <see cref="FoldDownTo(IReadOnlyList{SpeakerTurn}, int)"/>, and also reports each merge it
    /// made with the seconds the two labels spent talking over each other.
    /// </summary>
    /// <remarks>
    /// That number is the merge's own evidence, and a caller owes it to the user. Near zero is the
    /// signature this repair exists for: one person's identity drifted to a second label, so the two
    /// are complementary and never simultaneous. A large number means the pair really did converse,
    /// which is what two different people look like — the merge still happens, because the caller
    /// asked for a count and the count wins, but nobody should be told it was well founded.
    /// </remarks>
    public static IReadOnlyList<SpeakerTurn> FoldDownTo(
        IReadOnlyList<SpeakerTurn> turns, int cap, out IReadOnlyList<SpeakerFold> merges)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);

        merges = [];
        var labels = Speakers(turns);
        if (labels.Count <= cap)
        {
            return turns;
        }

        var made = new List<SpeakerFold>();
        var current = turns.ToList();
        var remaining = labels.ToList();

        while (remaining.Count > cap)
        {
            var bestCollision = double.MaxValue;
            var runnerUp = double.MaxValue;
            string? left = null;
            string? right = null;

            for (var i = 0; i < remaining.Count; i++)
            {
                for (var j = i + 1; j < remaining.Count; j++)
                {
                    var collision = SimultaneousSeconds(current, remaining[i], remaining[j]);

                    // Ties go to the pair that is smaller by total speech, so the fold is
                    // deterministic and prefers absorbing a minor label over merging two major ones.
                    if (collision < bestCollision
                        || (collision == bestCollision
                            && SpeechSeconds(current, remaining[i]) + SpeechSeconds(current, remaining[j])
                               < SpeechSeconds(current, left!) + SpeechSeconds(current, right!)))
                    {
                        runnerUp = bestCollision;
                        bestCollision = collision;
                        left = remaining[i];
                        right = remaining[j];
                    }
                    else if (collision < runnerUp)
                    {
                        runnerUp = collision;
                    }
                }
            }

            if (left is null || right is null)
            {
                break;
            }

            // The survivor is whichever has more speech, so the label that ends up on the transcript
            // is the one most of the words already belonged to.
            var keep = SpeechSeconds(current, left) >= SpeechSeconds(current, right) ? left : right;
            var drop = keep == left ? right : left;

            // The MARGIN is the evidence, not the absolute. Two hosts of a three-hour podcast overlap
            // for a couple of minutes however you cut them, so 131.8 s reads alarming on its own and
            // is in fact the clearest pair in the file by a factor of two. What says whether the fold
            // had a real choice to make is how far the next-best pair was behind it — so it is
            // carried beside the absolute rather than folded into a verdict here, and every surface
            // that reports the fold reports both.
            made.Add(new SpeakerFold
            {
                Dropped = drop,
                Kept = keep,
                OverlapSeconds = bestCollision,
                RunnerUpSeconds = double.IsPositiveInfinity(runnerUp) || runnerUp == double.MaxValue
                    ? null
                    : runnerUp,
            });
            current = [.. current.Select(t => t.Speaker == drop ? t with { Speaker = keep } : t)];
            remaining.Remove(drop);
        }

        merges = made;

        // Merging can leave two turns of the same speaker overlapping or touching, which no clean
        // label set should carry into a transcript.
        return Merge(current);
    }

    private static double SpeechSeconds(IEnumerable<SpeakerTurn> turns, string speaker)
    {
        var total = 0.0;
        foreach (var turn in turns)
        {
            if (turn.Speaker == speaker)
            {
                total += turn.Duration.TotalSeconds;
            }
        }

        return total;
    }

    /// <summary>Seconds during which both labels are active at once.</summary>
    private static double SimultaneousSeconds(IReadOnlyList<SpeakerTurn> turns, string one, string other)
    {
        var mine = turns.Where(t => t.Speaker == one).OrderBy(t => t.Start).ToList();
        var theirs = turns.Where(t => t.Speaker == other).OrderBy(t => t.Start).ToList();

        var total = 0.0;
        var j = 0;
        foreach (var turn in mine)
        {
            while (j > 0 && theirs[j - 1].End > turn.Start)
            {
                j--;
            }

            while (j < theirs.Count && theirs[j].End <= turn.Start)
            {
                j++;
            }

            for (var k = j; k < theirs.Count && theirs[k].Start < turn.End; k++)
            {
                var from = turn.Start > theirs[k].Start ? turn.Start : theirs[k].Start;
                var to = turn.End < theirs[k].End ? turn.End : theirs[k].End;
                if (to > from)
                {
                    total += (to - from).TotalSeconds;
                }
            }
        }

        return total;
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
