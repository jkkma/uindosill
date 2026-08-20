using System.Globalization;

namespace Parakeet.Core.Diarisation;

/// <summary>
/// How a diarisation score is computed. Named on every line of output because the conventions
/// differ enough to invert a comparison: a "collar 0" overlap-scored number can be triple the
/// same system's collar-0.25 number.
/// </summary>
public sealed record DiarisationScoringOptions
{
    public static DiarisationScoringOptions Default { get; } = new();

    /// <summary>
    /// The no-score zone around every reference turn boundary, as a <em>total</em> width centred
    /// on the boundary — pyannote.metrics semantics, where <c>collar=0.25</c> forgives 0.125 s
    /// either side. NIST md-eval's <c>-c 0.25</c> and NeMo's <c>collar=0.25</c> are half-widths, so
    /// their "0.25" is this option's 0.5. The default is 0.25 s total: the convention of arXiv
    /// 2509.26177, the one external comparison the measurement plan keeps meaningful.
    /// </summary>
    public TimeSpan Collar { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Leave regions where two or more reference turns overlap out of the score. Off by default —
    /// the audio this product is built for is overlap-heavy, and the whole point of the harness is
    /// to measure that rather than average it away.
    /// </summary>
    /// <remarks>
    /// "Two or more reference turns", not "two or more reference speakers": pyannote.metrics'
    /// <c>skip_overlap</c> extrudes every pairwise overlap of reference tracks whatever their
    /// labels, so a speaker's turns overlapping themselves are skipped too. That is a different
    /// rule from the overlap-region <em>breakdown</em>, which pyannote's <c>get_overlap</c> defines
    /// over distinct labels; both rules are copied faithfully and both are validated.
    /// </remarks>
    public bool SkipOverlap { get; init; }

    public void Validate()
    {
        if (Collar < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Collar), Collar, "The collar cannot be negative.");
        }
    }

    /// <summary>The convention in one phrase, for a run report or a table header.</summary>
    public string Describe() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"collar {Collar.TotalSeconds:0.###} s ({Collar.TotalSeconds / 2:0.####} s either side of each reference boundary), overlap {(SkipOverlap ? "skipped" : "included")}");
}

/// <summary>
/// The four durations a diarisation error rate is made of, in seconds. DER is
/// <c>(missed + false alarm + confusion) / reference speech</c>, which is the definition everyone
/// uses and the reason it can exceed one: a false alarm counts against a denominator it is not in.
/// </summary>
public sealed record DiarisationErrorComponents
{
    public static DiarisationErrorComponents Zero { get; } = new()
    {
        ReferenceSpeech = 0,
        Missed = 0,
        FalseAlarm = 0,
        Confusion = 0,
    };

    /// <summary>
    /// Total reference speech inside the scored region, counting each speaker separately where
    /// they overlap — the denominator.
    /// </summary>
    public required double ReferenceSpeech { get; init; }

    /// <summary>Reference speech the hypothesis attributed to nobody.</summary>
    public required double Missed { get; init; }

    /// <summary>Hypothesis speech where the reference has nobody talking.</summary>
    public required double FalseAlarm { get; init; }

    /// <summary>Speech attributed to somebody, but the wrong somebody, under the optimal mapping.</summary>
    public required double Confusion { get; init; }

    public double Correct => ReferenceSpeech - Missed - Confusion;

    public double Errors => Missed + FalseAlarm + Confusion;

    /// <summary>
    /// The error rate as a fraction, or <see cref="double.NaN"/> when there is no reference
    /// speech to score against — a rate over nothing is not zero.
    /// </summary>
    public double Rate => ReferenceSpeech > 0 ? Errors / ReferenceSpeech : double.NaN;

    public double MissedRate => ReferenceSpeech > 0 ? Missed / ReferenceSpeech : double.NaN;

    public double FalseAlarmRate => ReferenceSpeech > 0 ? FalseAlarm / ReferenceSpeech : double.NaN;

    public double ConfusionRate => ReferenceSpeech > 0 ? Confusion / ReferenceSpeech : double.NaN;

    public DiarisationErrorComponents Add(DiarisationErrorComponents other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new DiarisationErrorComponents
        {
            ReferenceSpeech = ReferenceSpeech + other.ReferenceSpeech,
            Missed = Missed + other.Missed,
            FalseAlarm = FalseAlarm + other.FalseAlarm,
            Confusion = Confusion + other.Confusion,
        };
    }
}

/// <summary>One scored file: a hypothesis against its reference, under a named convention.</summary>
public sealed record DiarisationScore
{
    public required DiarisationScoringOptions Options { get; init; }

    /// <summary>The headline components over the whole scored region.</summary>
    public required DiarisationErrorComponents Overall { get; init; }

    /// <summary>
    /// The same components over reference-overlap regions only — where two or more distinct
    /// reference speakers talk at once — under the <em>same</em> speaker mapping as
    /// <see cref="Overall"/>. Additive with the rest of the file: this plus the non-overlap
    /// remainder is <see cref="Overall"/> exactly. It is the number that says how the system does
    /// on crosstalk, which the headline dilutes with every second of one person talking.
    /// </summary>
    public required DiarisationErrorComponents OverlapRegions { get; init; }

    /// <summary>
    /// Hypothesis label to reference label, for the pairs the optimal one-to-one mapping put
    /// together and that actually co-occur. Hypothesis speakers absent from this dictionary matched
    /// nobody and score entirely as false alarm or confusion.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Mapping { get; init; }

    public required IReadOnlyList<string> ReferenceSpeakers { get; init; }

    public required IReadOnlyList<string> HypothesisSpeakers { get; init; }

    /// <summary>Length of the scored region after the collar and any skipped overlap are removed.</summary>
    public required double ScoredSeconds { get; init; }

    /// <summary>Things about the input worth telling a reader before they trust the number.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Diarisation error rate, computed the way pyannote.metrics computes it and validated against it
/// on committed fixture pairs (<c>tests/fixtures/diarisation/scorer/</c>). The scoring region is
/// the union of the reference and hypothesis extents with a collar cut out around every reference
/// boundary; both sides are cut into the elementary intervals every boundary defines; per interval
/// the reference and hypothesis speaker counts decide missed and false-alarm speech and the
/// mapped labels decide confusion; and the mapping between hypothesis and reference speakers is
/// the one that maximises co-occurring speech, found by exhaustive search — greedy mapping is not
/// DER, and at the speaker counts this product meets exhaustive search is cheap.
/// </summary>
public static class DiarisationErrorRate
{
    /// <summary>
    /// Below this an interval is treated as empty, as pyannote.core treats a segment: its precision
    /// is one microsecond, and slivers under it are floating-point noise from collar arithmetic
    /// rather than speech.
    /// </summary>
    public const double EpsilonSeconds = 1e-6;

    /// <summary>
    /// The exhaustive mapping search gives up above this many partial assignments visited. With
    /// the bound it applies the search finishes in a few thousand nodes for any real pair; hitting
    /// this means a hypothesis with a pathological number of speakers, and the message says so.
    /// </summary>
    private const long MappingSearchBudget = 20_000_000;

    public static DiarisationScore Score(
        IReadOnlyList<SpeakerTurn> reference,
        IReadOnlyList<SpeakerTurn> hypothesis,
        DiarisationScoringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);
        options ??= DiarisationScoringOptions.Default;
        options.Validate();

        foreach (var turn in reference)
        {
            turn.Validate();
        }

        foreach (var turn in hypothesis)
        {
            turn.Validate();
        }

        var warnings = new List<string>();
        var referenceSpeakers = SpeakerTurns.Speakers(reference);
        var hypothesisSpeakers = SpeakerTurns.Speakers(hypothesis);
        var referencePieces = ToPieces(reference, referenceSpeakers);
        var hypothesisPieces = ToPieces(hypothesis, hypothesisSpeakers);

        WarnAboutSelfOverlap(referencePieces, referenceSpeakers, "reference", warnings);
        WarnAboutSelfOverlap(hypothesisPieces, hypothesisSpeakers, "hypothesis", warnings);

        if (referencePieces.Count == 0 && hypothesisPieces.Count == 0)
        {
            return new DiarisationScore
            {
                Options = options,
                Overall = DiarisationErrorComponents.Zero,
                OverlapRegions = DiarisationErrorComponents.Zero,
                Mapping = new Dictionary<string, string>(StringComparer.Ordinal),
                ReferenceSpeakers = referenceSpeakers,
                HypothesisSpeakers = hypothesisSpeakers,
                ScoredSeconds = 0,
                Warnings = ["Both files are empty: nothing was scored."],
            };
        }

        if (referencePieces.Count == 0)
        {
            warnings.Add("The reference has no speech: the rate is undefined and every hypothesis second is a false alarm.");
        }

        // ── the scored region ─────────────────────────────────────────────────────────────────

        var all = referencePieces.Concat(hypothesisPieces).ToList();
        var extentStart = all.Min(p => p.Start);
        var extentEnd = all.Max(p => p.End);

        var cuts = new List<(double Start, double End)>();
        var half = options.Collar.TotalSeconds / 2;
        if (half > 0)
        {
            foreach (var piece in referencePieces)
            {
                cuts.Add((piece.Start - half, piece.Start + half));
                cuts.Add((piece.End - half, piece.End + half));
            }
        }

        if (options.SkipOverlap)
        {
            // Any two reference pieces, same label or not — pyannote's extrude() rule.
            cuts.AddRange(OverlapRegions(referencePieces, distinctLabels: false));
        }

        var scored = Subtract([(extentStart, extentEnd)], cuts);
        var scoredSeconds = scored.Sum(s => s.End - s.Start);

        var croppedReference = Crop(referencePieces, scored);
        var croppedHypothesis = Crop(hypothesisPieces, scored);

        // ── elementary intervals ──────────────────────────────────────────────────────────────

        var boundaries = new SortedSet<double>();
        foreach (var piece in croppedReference.Concat(croppedHypothesis))
        {
            boundaries.Add(piece.Start);
            boundaries.Add(piece.End);
        }

        var intervals = new List<Interval>();
        double? previous = null;
        foreach (var boundary in boundaries)
        {
            if (previous is { } a && boundary - a > EpsilonSeconds)
            {
                var referenceCounts = new int[referenceSpeakers.Count];
                var hypothesisCounts = new int[hypothesisSpeakers.Count];
                foreach (var piece in croppedReference)
                {
                    if (piece.Start <= a && piece.End >= boundary)
                    {
                        referenceCounts[piece.Label]++;
                    }
                }

                foreach (var piece in croppedHypothesis)
                {
                    if (piece.Start <= a && piece.End >= boundary)
                    {
                        hypothesisCounts[piece.Label]++;
                    }
                }

                if (referenceCounts.Sum() + hypothesisCounts.Sum() > 0)
                {
                    intervals.Add(new Interval(boundary - a, referenceCounts, hypothesisCounts));
                }
            }

            previous = boundary;
        }

        // ── the optimal speaker mapping ───────────────────────────────────────────────────────

        var coOccurrence = new double[referenceSpeakers.Count, hypothesisSpeakers.Count];
        foreach (var interval in intervals)
        {
            for (var r = 0; r < referenceSpeakers.Count; r++)
            {
                if (interval.ReferenceCounts[r] == 0)
                {
                    continue;
                }

                for (var h = 0; h < hypothesisSpeakers.Count; h++)
                {
                    coOccurrence[r, h] += interval.Duration * interval.ReferenceCounts[r] * interval.HypothesisCounts[h];
                }
            }
        }

        var hypothesisToReference = OptimalMapping(coOccurrence);

        // ── the components, overall and over reference-overlap regions ────────────────────────

        var overall = DiarisationErrorComponents.Zero;
        var overlap = DiarisationErrorComponents.Zero;
        foreach (var interval in intervals)
        {
            var nr = interval.ReferenceCounts.Sum();
            var nh = interval.HypothesisCounts.Sum();
            var correct = 0;
            for (var h = 0; h < hypothesisSpeakers.Count; h++)
            {
                if (hypothesisToReference[h] is { } r && interval.HypothesisCounts[h] > 0)
                {
                    correct += Math.Min(interval.ReferenceCounts[r], interval.HypothesisCounts[h]);
                }
            }

            var components = new DiarisationErrorComponents
            {
                ReferenceSpeech = interval.Duration * nr,
                Missed = interval.Duration * Math.Max(0, nr - nh),
                FalseAlarm = interval.Duration * Math.Max(0, nh - nr),
                Confusion = interval.Duration * (Math.Min(nr, nh) - correct),
            };

            overall = overall.Add(components);
            if (interval.ReferenceCounts.Count(c => c > 0) >= 2)
            {
                overlap = overlap.Add(components);
            }
        }

        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var h = 0; h < hypothesisSpeakers.Count; h++)
        {
            if (hypothesisToReference[h] is { } r && coOccurrence[r, h] > 0)
            {
                mapping[hypothesisSpeakers[h]] = referenceSpeakers[r];
            }
        }

        return new DiarisationScore
        {
            Options = options,
            Overall = overall,
            OverlapRegions = overlap,
            Mapping = mapping,
            ReferenceSpeakers = referenceSpeakers,
            HypothesisSpeakers = hypothesisSpeakers,
            ScoredSeconds = scoredSeconds,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// The corpus figure: every component summed, so the rate is total error over total reference
    /// speech. That weights a long file more than a short one, which is what "DER over the set"
    /// means everywhere it is reported; a mean of per-file rates is a different number and is not
    /// computed here.
    /// </summary>
    public static DiarisationErrorComponents Aggregate(IEnumerable<DiarisationErrorComponents> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var total = DiarisationErrorComponents.Zero;
        foreach (var item in components)
        {
            if (item is null)
            {
                throw new ArgumentException("Components may not contain null.", nameof(components));
            }

            total = total.Add(item);
        }

        return total;
    }

    // ── pieces and intervals ─────────────────────────────────────────────────────────────────

    private readonly record struct Piece(double Start, double End, int Label);

    private sealed record Interval(double Duration, int[] ReferenceCounts, int[] HypothesisCounts);

    private static List<Piece> ToPieces(IReadOnlyList<SpeakerTurn> turns, IReadOnlyList<string> speakers)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < speakers.Count; i++)
        {
            index[speakers[i]] = i;
        }

        var pieces = new List<Piece>(turns.Count);
        foreach (var turn in turns)
        {
            var start = turn.Start.TotalSeconds;
            var end = turn.End.TotalSeconds;
            if (end - start > EpsilonSeconds)
            {
                pieces.Add(new Piece(start, end, index[turn.Speaker]));
            }
        }

        return pieces;
    }

    private static void WarnAboutSelfOverlap(List<Piece> pieces, IReadOnlyList<string> speakers, string side, List<string> warnings)
    {
        foreach (var group in pieces.GroupBy(p => p.Label))
        {
            double overlapped = 0;
            var end = double.NegativeInfinity;
            foreach (var piece in group.OrderBy(p => p.Start).ThenBy(p => p.End))
            {
                if (piece.Start < end)
                {
                    overlapped += Math.Min(piece.End, end) - piece.Start;
                }

                end = Math.Max(end, piece.End);
            }

            if (overlapped > EpsilonSeconds)
            {
                warnings.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{side} speaker '{speakers[group.Key]}' overlaps itself for {overlapped:0.###} s; that time is counted twice, as pyannote.metrics counts it. Merge the turns if it is a labelling slip."));
            }
        }
    }

    /// <summary>
    /// Regions where at least two pieces are active — of distinct labels when
    /// <paramref name="distinctLabels"/> is set (pyannote's <c>get_overlap</c>, the breakdown's
    /// rule), of any labels when it is not (pyannote's <c>skip_overlap</c> extrusion) — as a list of
    /// disjoint intervals.
    /// </summary>
    private static List<(double Start, double End)> OverlapRegions(List<Piece> pieces, bool distinctLabels)
    {
        var events = new List<(double Time, int Delta, int Label)>();
        foreach (var piece in pieces)
        {
            events.Add((piece.Start, +1, piece.Label));
            events.Add((piece.End, -1, piece.Label));
        }

        // Ends before starts at the same instant, so touching turns do not count as overlapping.
        events.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Delta.CompareTo(b.Delta));

        var active = new Dictionary<int, int>();
        var activePieces = 0;
        var regions = new List<(double Start, double End)>();
        double? openedAt = null;
        foreach (var (time, delta, label) in events)
        {
            active.TryGetValue(label, out var count);
            count += delta;
            activePieces += delta;
            if (count == 0)
            {
                active.Remove(label);
            }
            else
            {
                active[label] = count;
            }

            var overlapping = distinctLabels ? active.Count >= 2 : activePieces >= 2;
            if (overlapping && openedAt is null)
            {
                openedAt = time;
            }
            else if (!overlapping && openedAt is { } start)
            {
                if (time - start > EpsilonSeconds)
                {
                    regions.Add((start, time));
                }

                openedAt = null;
            }
        }

        return Merge(regions);
    }

    private static List<(double Start, double End)> Merge(List<(double Start, double End)> intervals)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var interval in intervals.OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }

    /// <summary>The support minus the cuts, as disjoint intervals in order.</summary>
    private static List<(double Start, double End)> Subtract(
        List<(double Start, double End)> support, List<(double Start, double End)> cuts)
    {
        var mergedCuts = Merge(cuts);
        var result = new List<(double Start, double End)>();
        foreach (var (start, end) in support)
        {
            var cursor = start;
            foreach (var cut in mergedCuts)
            {
                if (cut.End <= cursor)
                {
                    continue;
                }

                if (cut.Start >= end)
                {
                    break;
                }

                if (cut.Start > cursor)
                {
                    result.Add((cursor, Math.Min(cut.Start, end)));
                }

                cursor = Math.Max(cursor, cut.End);
                if (cursor >= end)
                {
                    break;
                }
            }

            if (cursor < end)
            {
                result.Add((cursor, end));
            }
        }

        return [.. result.Where(r => r.End - r.Start > EpsilonSeconds)];
    }

    private static List<Piece> Crop(List<Piece> pieces, List<(double Start, double End)> support)
    {
        var cropped = new List<Piece>();
        foreach (var piece in pieces)
        {
            foreach (var (start, end) in support)
            {
                var from = Math.Max(piece.Start, start);
                var to = Math.Min(piece.End, end);
                if (to - from > EpsilonSeconds)
                {
                    cropped.Add(new Piece(from, to, piece.Label));
                }
            }
        }

        return cropped;
    }

    // ── the mapping search ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one-to-one hypothesis-to-reference mapping that maximises co-occurring speech, by
    /// exhaustive depth-first search with a bound. Every reference speaker in turn takes an
    /// unassigned hypothesis speaker or nobody; a branch is abandoned as soon as the most it could
    /// still add cannot beat the best complete assignment already found. Exact by construction —
    /// it is the search the Hungarian algorithm shortcuts — and, on the near-diagonal matrices
    /// real pairs produce, it visits a few dozen nodes.
    /// </summary>
    /// <returns>Per hypothesis label index, the reference label index it maps to, or null.</returns>
    internal static int?[] OptimalMapping(double[,] coOccurrence)
    {
        ArgumentNullException.ThrowIfNull(coOccurrence);

        var referenceCount = coOccurrence.GetLength(0);
        var hypothesisCount = coOccurrence.GetLength(1);
        var result = new int?[hypothesisCount];
        if (referenceCount == 0 || hypothesisCount == 0)
        {
            return result;
        }

        // The best any single reference speaker can still contribute, for the bound.
        var rowMax = new double[referenceCount];
        for (var r = 0; r < referenceCount; r++)
        {
            for (var h = 0; h < hypothesisCount; h++)
            {
                rowMax[r] = Math.Max(rowMax[r], coOccurrence[r, h]);
            }
        }

        var remaining = new double[referenceCount + 1];
        for (var r = referenceCount - 1; r >= 0; r--)
        {
            remaining[r] = remaining[r + 1] + rowMax[r];
        }

        var assignment = new int[referenceCount];
        Array.Fill(assignment, -1);
        var best = new int[referenceCount];
        Array.Fill(best, -1);
        var bestScore = -1.0;
        var taken = new bool[hypothesisCount];
        long visited = 0;

        void Search(int r, double score)
        {
            if (++visited > MappingSearchBudget)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The speaker mapping search exceeded {MappingSearchBudget:N0} nodes for {referenceCount} reference " +
                    $"and {hypothesisCount} hypothesis speakers. A hypothesis with that many speakers is not a diarisation " +
                    $"of this material; look at it before scoring it."));
            }

            if (r == referenceCount)
            {
                if (score > bestScore)
                {
                    bestScore = score;
                    Array.Copy(assignment, best, referenceCount);
                }

                return;
            }

            if (score + remaining[r] <= bestScore)
            {
                return;
            }

            // Best candidates first, so the bound bites early.
            var candidates = Enumerable.Range(0, hypothesisCount)
                .Where(h => !taken[h])
                .OrderByDescending(h => coOccurrence[r, h])
                .ToArray();

            foreach (var h in candidates)
            {
                taken[h] = true;
                assignment[r] = h;
                Search(r + 1, score + coOccurrence[r, h]);
                assignment[r] = -1;
                taken[h] = false;
            }

            // This reference speaker matches nobody.
            Search(r + 1, score);
        }

        Search(0, 0);

        for (var r = 0; r < referenceCount; r++)
        {
            if (best[r] >= 0)
            {
                result[best[r]] = r;
            }
        }

        return result;
    }
}
