using System.Globalization;
using Parakeet.Core.Diarisation;

namespace Parakeet.Engine.Sortformer;

/// <summary>
/// The knobs NeMo's <c>ts_vad_post_processing</c> turns, and the order it turns them in: a
/// hysteresis over the per-frame probabilities, padding, then a short-speech filter, then a
/// short-gap filter.
/// </summary>
/// <remarks>
/// <para>
/// Values tuned on the 18 AMI development meetings, pooled — 2 179 distinct configurations scored
/// — and applied unchanged to the 16 test meetings, which were scored once. Changing a default here
/// invalidates that: the number in <c>docs/PHASES.md</c> is this parameter set's number and no
/// other's.
/// </para>
/// <para>
/// Two things about the order were read at source rather than assumed, because both are easy to get
/// backwards and neither fails loudly. The <b>filters run speech-first</b> — NeMo's <c>filtering</c>
/// defaults to <c>filter_speech_first=1.0</c>, so short speech is deleted before short gaps are
/// filled; the other way round, a gap-fill rescues a segment that should have gone. And the
/// <b>names are swapped in NeMo's own post-processing YAML comments</b> relative to the docstring in
/// <c>filtering</c>; the code and the docstring agree with each other, so the code is what is
/// followed: <see cref="MinimumSpeechDuration"/> drops, <see cref="MinimumSilenceDuration"/> fills.
/// </para>
/// </remarks>
public sealed record SortformerPostProcessingOptions
{
    /// <summary>
    /// The parameter set that produced the measured result: AMI test 16.33% at collar 0 with
    /// overlap, 13.60% at the headline collar 0.25.
    /// </summary>
    public static SortformerPostProcessingOptions Default { get; } = new();

    /// <summary>Probability above which a speaker starts being counted as speaking.</summary>
    public double Onset { get; init; } = 0.5;

    /// <summary>
    /// Probability below which they stop. Equal to <see cref="Onset"/> in the tuned set, which
    /// makes the hysteresis degenerate to a plain comparison; the sweep found no gain from
    /// separating them and the two are kept apart so a later tuning pass can.
    /// </summary>
    public double Offset { get; init; } = 0.5;

    /// <summary>Seconds each segment is extended backwards before overlapping segments are merged.</summary>
    public TimeSpan PadOnset { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Seconds each segment is extended forwards.</summary>
    public TimeSpan PadOffset { get; init; } = TimeSpan.Zero;

    /// <summary>Segments shorter than this are deleted. Zero in the tuned set: nothing is dropped.</summary>
    public TimeSpan MinimumSpeechDuration { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Gaps shorter than this between two segments of the same speaker are filled. One second in
    /// the tuned set, which is what the pyannote <c>only_words</c> references want: they mark word
    /// extents, so a speaker pausing mid-sentence is still one turn to them.
    /// </summary>
    public TimeSpan MinimumSilenceDuration { get; init; } = TimeSpan.FromSeconds(1);

    public void Validate()
    {
        if (!double.IsFinite(Onset) || Onset is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Onset), Onset, "Onset must be a probability.");
        }

        if (!double.IsFinite(Offset) || Offset is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Offset), Offset, "Offset must be a probability.");
        }

        // The hysteresis needs its thresholds this way round, and the binariser's correctness
        // argument says so explicitly: with offset <= onset, up-sampling the 80 ms predictions to
        // NeMo's 10 ms grid cannot move a crossing, which is why this works on the coarse grid.
        // Reversed, "above onset" and "below offset" are both true for every frame between them and
        // the state machine opens and closes on alternate frames for as long as that lasts — a
        // checkerboard of 80 ms segments rather than a diagnosable failure. The two are adjacent
        // properties with the same shape and are easy to transpose.
        if (Offset > Onset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Offset),
                Offset,
                $"The offset ({Offset}) must not be above the onset ({Onset}): a speaker has to be heard " +
                "before they can stop being heard, and reversing the two turns the hysteresis into a " +
                "one-frame oscillator.");
        }

        if (PadOnset < TimeSpan.Zero || PadOffset < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PadOnset), "Padding cannot be negative.");
        }

        if (MinimumSpeechDuration < TimeSpan.Zero || MinimumSilenceDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumSpeechDuration), "Minimum durations cannot be negative.");
        }
    }
}

/// <summary>Turns a <c>[frames x 4]</c> block of speaker probabilities into speaker turns.</summary>
public static class SortformerPostProcessing
{
    /// <summary>
    /// Applies the four stages to each speaker independently and returns the union, in time order.
    /// </summary>
    /// <param name="probabilities">
    /// Row-major <c>[frames x speakers]</c> activity in [0, 1], one row per
    /// <see cref="SortformerGeometry.FrameSeconds"/>.
    /// </param>
    /// <param name="speakers">Columns in <paramref name="probabilities"/>.</param>
    /// <param name="options">Null for the tuned set.</param>
    /// <remarks>
    /// Speakers are labelled <c>spk0</c>..<c>spk3</c> by the model's own column, not renamed by
    /// order of appearance. That is deliberate: the column is what the speaker cache works to keep
    /// stable, and a scorer wants to see the labels the model actually produced. Display renaming
    /// is <c>SpeakerTurns.RenameByFirstAppearance</c>'s job, one layer up.
    /// </remarks>
    public static IReadOnlyList<SpeakerTurn> ToTurns(
        ReadOnlySpan<float> probabilities,
        int speakers = SortformerGeometry.SpeakerCount,
        SortformerPostProcessingOptions? options = null)
    {
        options ??= SortformerPostProcessingOptions.Default;
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(speakers, 1);

        var frames = probabilities.Length / speakers;
        var limit = frames * SortformerGeometry.FrameSeconds;
        var turns = new List<SpeakerTurn>();

        for (var speaker = 0; speaker < speakers; speaker++)
        {
            var segments = Binarise(probabilities, frames, speakers, speaker, options.Onset, options.Offset);
            segments = PadAndMerge(segments, options.PadOnset.TotalSeconds, options.PadOffset.TotalSeconds, limit);
            segments = DropShort(segments, options.MinimumSpeechDuration.TotalSeconds);
            segments = FillGaps(segments, options.MinimumSilenceDuration.TotalSeconds);

            var label = string.Create(CultureInfo.InvariantCulture, $"spk{speaker}");
            foreach (var (start, end) in segments)
            {
                turns.Add(new SpeakerTurn
                {
                    Start = SpeakerTurns.FromSeconds(start),
                    End = SpeakerTurns.FromSeconds(end),
                    Speaker = label,
                });
            }
        }

        turns.Sort(static (a, b) =>
        {
            var byStart = a.Start.CompareTo(b.Start);
            if (byStart != 0)
            {
                return byStart;
            }

            var byEnd = a.End.CompareTo(b.End);
            return byEnd != 0 ? byEnd : string.CompareOrdinal(a.Speaker, b.Speaker);
        });

        return turns;
    }

    /// <summary>
    /// The hysteresis: a speaker starts when their probability rises above <paramref name="onset"/>
    /// and stops when it falls below <paramref name="offset"/>, so a frame between the two thresholds
    /// leaves the state as it was.
    /// </summary>
    /// <remarks>
    /// A speaker still talking at the last frame is closed at the end of the recording rather than
    /// dropped. NeMo up-samples its 80 ms predictions to a 10 ms grid before binarising; with
    /// <c>offset &lt;= onset</c> that cannot move a crossing — repeating a value cannot make it cross
    /// a threshold it did not cross — so this works on the 80 ms grid and lands on the same
    /// boundaries.
    /// </remarks>
    private static List<(double Start, double End)> Binarise(
        ReadOnlySpan<float> probabilities, int frames, int speakers, int speaker, double onset, double offset)
    {
        var segments = new List<(double, double)>();
        var speaking = false;
        var start = 0;

        for (var frame = 0; frame < frames; frame++)
        {
            var probability = probabilities[frame * speakers + speaker];
            if (!speaking)
            {
                if (probability > onset)
                {
                    speaking = true;
                    start = frame;
                }
            }
            else if (probability < offset)
            {
                speaking = false;
                segments.Add((start * SortformerGeometry.FrameSeconds, frame * SortformerGeometry.FrameSeconds));
            }
        }

        if (speaking)
        {
            segments.Add((start * SortformerGeometry.FrameSeconds, frames * SortformerGeometry.FrameSeconds));
        }

        return segments;
    }

    /// <summary>Extends both edges, clamped to the recording, then merges whatever that made touch.</summary>
    private static List<(double Start, double End)> PadAndMerge(
        List<(double Start, double End)> segments, double padOnset, double padOffset, double limit)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            var (start, end) = segments[i];
            segments[i] = (Math.Max(0.0, start - padOnset), Math.Min(limit, end + padOffset));
        }

        return Merge(segments, 0.0);
    }

    private static List<(double Start, double End)> DropShort(List<(double Start, double End)> segments, double minimum) =>
        minimum > 0 ? segments.FindAll(s => s.End - s.Start >= minimum) : segments;

    private static List<(double Start, double End)> FillGaps(List<(double Start, double End)> segments, double minimum) =>
        minimum > 0 ? Merge(segments, minimum) : segments;

    /// <summary>Joins segments separated by at most <paramref name="gap"/> seconds.</summary>
    private static List<(double Start, double End)> Merge(List<(double Start, double End)> segments, double gap)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        segments.Sort(static (a, b) =>
        {
            var byStart = a.Start.CompareTo(b.Start);
            return byStart != 0 ? byStart : a.End.CompareTo(b.End);
        });

        var merged = new List<(double Start, double End)> { segments[0] };
        for (var i = 1; i < segments.Count; i++)
        {
            var (start, end) = segments[i];
            var last = merged[^1];
            if (start - last.End <= gap)
            {
                merged[^1] = (last.Start, Math.Max(last.End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        return merged;
    }
}
