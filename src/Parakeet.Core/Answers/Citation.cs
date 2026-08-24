using System.Globalization;

namespace Parakeet.Core.Answers;

/// <summary>
/// One citation as the model wrote it: an opaque segment id or run of ids — <c>S12</c>,
/// <c>S12-S15</c> — or the admitted-uncited marker <c>?</c>. The model never writes a
/// timestamp; the app resolves ids to a <c>TranscriptSegment</c>'s times, and an id that does
/// not resolve renders as unresolved rather than ever becoming a time a reader might trust.
/// </summary>
/// <remarks>
/// Ids are 1-based positions in the transcript's segment array — the file format carries no id
/// field, so position is the id, and a citation is only meaningful against the one transcript
/// it was answered from. <see cref="Raw"/> is kept exactly as written even when it parses,
/// because a defect report that cannot show what the model actually said is a defect report
/// about something else.
/// </remarks>
public sealed record Citation
{
    /// <summary>The text between the brackets, exactly as the model wrote it.</summary>
    public required string Raw { get; init; }

    /// <summary>1-based first segment of the cited run, or null when <see cref="Raw"/> did not parse.</summary>
    public int? StartSegment { get; init; }

    /// <summary>1-based last segment, inclusive; equals <see cref="StartSegment"/> for a point citation.</summary>
    public int? EndSegment { get; init; }

    /// <summary>The model admitted it could not anchor the claim: <c>[?]</c>.</summary>
    public bool IsUncitedMarker => Raw == "?";

    /// <summary>Both ids parsed. Whether they resolve against a transcript is the validator's question.</summary>
    public bool IsWellFormed => StartSegment is not null && EndSegment is not null;

    public static Citation Uncited { get; } = new() { Raw = "?" };

    /// <summary>
    /// Parses <c>S12</c> and <c>S12-S15</c>. Anything else — including a backwards range, which
    /// parses and then fails the validator's ordering check — comes back well-formed or not, but
    /// always with <see cref="Raw"/> preserved.
    /// </summary>
    public static Citation Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var text = raw.Trim();
        if (text == "?")
        {
            return Uncited;
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return TryId(text, out var id)
                ? new Citation { Raw = text, StartSegment = id, EndSegment = id }
                : new Citation { Raw = text };
        }

        return TryId(text[..dash], out var first) && TryId(text[(dash + 1)..], out var second)
            ? new Citation { Raw = text, StartSegment = first, EndSegment = second }
            : new Citation { Raw = text };
    }

    /// <summary>True when <paramref name="raw"/> is something a citation bracket can hold at all.</summary>
    public static bool LooksLikeCitation(string raw)
    {
        var text = raw.Trim();
        if (text == "?")
        {
            return true;
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        return dash < 0
            ? TryId(text, out _)
            : TryId(text[..dash], out _) && TryId(text[(dash + 1)..], out _);
    }

    private static bool TryId(string text, out int id)
    {
        id = 0;
        var trimmed = text.Trim();
        return trimmed.Length >= 2
            && trimmed[0] == 'S'
            && int.TryParse(trimmed[1..], NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }
}
