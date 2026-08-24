using Parakeet.Core.Transcription;

namespace Parakeet.Core.Retrieval;

/// <summary>
/// The unit retrieval works in: a contiguous run of segments about a minute long. Segments here
/// average about 27 tokens — too small to be a hit on their own — so the index is built over
/// windows, and a retrieved window <em>is</em> the citation: it carries the segment ids the
/// language model is allowed to cite, which is what makes a retrieved answer citable by
/// construction.
/// </summary>
public sealed record TranscriptWindow
{
    /// <summary>1-based position of the first segment in the run — the <c>S&lt;n&gt;</c> id space.</summary>
    public required int FirstSegment { get; init; }

    /// <summary>1-based position of the last segment in the run, inclusive.</summary>
    public required int LastSegment { get; init; }

    /// <summary>The first segment's start.</summary>
    public required TimeSpan Start { get; init; }

    /// <summary>The last segment's end.</summary>
    public required TimeSpan End { get; init; }

    /// <summary>The run's non-empty segment texts, trimmed, one space between them.</summary>
    public required string Text { get; init; }

    /// <summary>The run as the citation grammar spells it: <c>S12</c> or <c>S12-S20</c>.</summary>
    public string CitationId => FirstSegment == LastSegment
        ? FormattableString.Invariant($"S{FirstSegment}")
        : FormattableString.Invariant($"S{FirstSegment}-S{LastSegment}");

    public TimeSpan Duration => End - Start;

    /// <summary>
    /// The text cut to at most <paramref name="maxLength"/> chars with an ellipsis — never
    /// through a surrogate pair, because a preview ending in U+FFFD reads as corruption. The one
    /// truncation the CLI listing and the Sources expander share.
    /// </summary>
    public string Preview(int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        if (Text.Length <= maxLength)
        {
            return Text;
        }

        var cut = maxLength - 1;
        if (char.IsHighSurrogate(Text[cut - 1]))
        {
            cut--;
        }

        return Text[..cut] + "…";
    }
}

/// <summary>How the transcript is cut into windows: about a minute at half overlap by default.</summary>
public sealed record TranscriptWindowOptions
{
    /// <summary>~60 s windows at 50 % overlap — the register's decision 3 shape.</summary>
    public static TranscriptWindowOptions Default { get; } = new();

    /// <summary>The 120 s variant decision 3 names for comparison runs.</summary>
    public static TranscriptWindowOptions Wide { get; } = new()
    {
        WindowLength = TimeSpan.FromSeconds(120),
        Stride = TimeSpan.FromSeconds(60),
    };

    public TimeSpan WindowLength { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Distance between window starts; half the length gives 50 % overlap.</summary>
    public TimeSpan Stride { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Cuts a transcript into overlapping windows of contiguous segments.</summary>
public static class TranscriptWindowBuilder
{
    public static IReadOnlyList<TranscriptWindow> Build(
        TranscriptDocument document, TranscriptWindowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptWindowOptions.Default;
        if (options.WindowLength <= TimeSpan.Zero)
        {
            throw new ArgumentException("Window length must be positive.", nameof(options));
        }

        if (options.Stride <= TimeSpan.Zero || options.Stride > options.WindowLength)
        {
            throw new ArgumentException("Stride must be positive and no longer than the window.", nameof(options));
        }

        // A segment belongs to every window whose time span holds its midpoint, so at half
        // overlap each one appears in two windows and a question landing near a window edge
        // still finds its context whole in the neighbour.
        var midpoints = new List<(int Id, TimeSpan Midpoint)>();
        for (var i = 0; i < document.Segments.Count; i++)
        {
            var segment = document.Segments[i];
            if (!segment.IsEmpty)
            {
                midpoints.Add((i + 1, segment.Start + ((segment.End - segment.Start) / 2)));
            }
        }

        if (midpoints.Count == 0)
        {
            return [];
        }

        var windows = new List<TranscriptWindow>();

        // The furthest midpoint, not the final segment's: a transcript whose segments are out of
        // time order — a hand-edited file is exactly what the reader reopens — would otherwise
        // end the grid early and leave later speech silently unretrievable.
        var last = TimeSpan.Zero;
        foreach (var (_, midpoint) in midpoints)
        {
            if (midpoint > last)
            {
                last = midpoint;
            }
        }

        var seen = new HashSet<(int First, int Last)>();
        for (var k = 0; ; k++)
        {
            var windowStart = k * options.Stride;
            if (windowStart > last)
            {
                break;
            }

            var windowEnd = windowStart + options.WindowLength;
            var first = 0;
            var lastId = 0;
            foreach (var (id, midpoint) in midpoints)
            {
                if (midpoint >= windowStart && midpoint < windowEnd)
                {
                    if (first == 0)
                    {
                        first = id;
                    }

                    lastId = id;
                }
            }

            if (first == 0)
            {
                continue;
            }

            // Sparse audio can hand more than one grid position the same run — and with a stride
            // under half the length, not necessarily consecutive ones. A duplicate window would
            // count its terms twice in every document-frequency figure, so the run set is the
            // check, not the neighbour.
            if (!seen.Add((first, lastId)))
            {
                continue;
            }

            windows.Add(FromRun(document, first, lastId));
        }

        return windows;
    }

    /// <summary>
    /// The window for an explicit 1-based segment run — the same construction retrieval uses,
    /// exposed so a caller resolving a citation can render the run it names.
    /// </summary>
    public static TranscriptWindow FromRun(TranscriptDocument document, int firstSegment, int lastSegment)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstSegment, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastSegment, firstSegment);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lastSegment, document.Segments.Count);

        var run = new List<string>();
        for (var i = firstSegment - 1; i < lastSegment; i++)
        {
            var segment = document.Segments[i];
            if (!segment.IsEmpty)
            {
                run.Add(segment.Text.Trim());
            }
        }

        return new TranscriptWindow
        {
            FirstSegment = firstSegment,
            LastSegment = lastSegment,
            Start = document.Segments[firstSegment - 1].Start,
            End = document.Segments[lastSegment - 1].End,
            Text = string.Join(' ', run),
        };
    }
}
