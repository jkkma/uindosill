using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Answers;

/// <summary>What one citation survived, each check decidable without judging any prose.</summary>
public sealed record CitationCheck
{
    /// <summary>Both ids parsed, in order, and inside the transcript: 1 ≤ first ≤ last ≤ segment count.</summary>
    public required bool Resolves { get; init; }

    /// <summary>The cited run contains some non-whitespace text — a citation of silence anchors nothing.</summary>
    public required bool NonEmpty { get; init; }

    /// <summary>The run's end does not pass the recording's end, when the duration is known.</summary>
    public required bool WithinDuration { get; init; }

    /// <summary>
    /// The bullet's verbatim quote appears in the cited span after both go through
    /// <see cref="SearchTokenizer.Normalize"/>. Null when the bullet carries no quote — and when
    /// the citation never resolved, because a quote with no span to check against was never
    /// checked: false is reserved for checked-and-failed.
    /// </summary>
    public bool? QuoteMatches { get; init; }

    /// <summary>Everything checkable checked out. A failed quote fails this; an absent one does not.</summary>
    public bool Passes => Resolves && NonEmpty && WithinDuration && QuoteMatches != false;
}

/// <summary>
/// A citation with what it resolved to. When <see cref="CitationCheck.Resolves"/> is false the
/// times are null and stay null: an unresolved citation is rendered as unresolved, never as a
/// time a reader might take for a real one.
/// </summary>
public sealed record ResolvedCitation
{
    public required Citation Citation { get; init; }

    public required CitationCheck Check { get; init; }

    /// <summary>The first cited segment's start, when the citation resolves.</summary>
    public TimeSpan? Start { get; init; }

    /// <summary>The last cited segment's end, when the citation resolves.</summary>
    public TimeSpan? End { get; init; }
}

/// <summary>One bullet's citations, resolved in the order the model wrote them.</summary>
public sealed record ResolvedBullet
{
    public required AnswerBullet Bullet { get; init; }

    public required IReadOnlyList<ResolvedCitation> Citations { get; init; }
}

/// <summary>A whole answer against a transcript.</summary>
public sealed record AnswerValidation
{
    public required IReadOnlyList<ResolvedBullet> Bullets { get; init; }

    /// <summary>
    /// Whether the resolving citations step forward through the recording without overlapping,
    /// or null when the answer never claimed to follow it — chronology is a property of what was
    /// asked for, not something a validator can infer from prose.
    /// </summary>
    public bool? Monotone { get; init; }

    /// <summary>Every citation on every bullet passed its checks, the uncited markers aside.</summary>
    public bool AllCitationsPass =>
        Bullets.All(b => b.Citations.All(c => c.Citation.IsUncitedMarker || c.Check.Passes));
}

/// <summary>
/// Resolves opaque segment ids against the one transcript they are meaningful for, and runs
/// every check that is decidable without judging the prose. Under the rule that the model never
/// writes a timestamp, this is not a test run after the fact — it is the mechanism: the times a
/// reader sees are the times resolved here or nothing.
/// </summary>
public static class CitationValidator
{
    public static ResolvedCitation Resolve(Citation citation, TranscriptDocument transcript, string? quote = null)
    {
        ArgumentNullException.ThrowIfNull(citation);
        ArgumentNullException.ThrowIfNull(transcript);

        var resolves = citation.StartSegment is { } first
            && citation.EndSegment is { } last
            && first >= 1
            && first <= last
            && last <= transcript.Segments.Count;

        if (!resolves)
        {
            return new ResolvedCitation
            {
                Citation = citation,
                Check = new CitationCheck
                {
                    Resolves = false,
                    NonEmpty = false,
                    WithinDuration = false,
                    // An unresolved citation names no span, so the quote was never checked;
                    // false here would render as "quote not found" — an accusation of a check
                    // that never ran.
                    QuoteMatches = null,
                },
            };
        }

        var start = citation.StartSegment!.Value;
        var end = citation.EndSegment!.Value;

        var nonEmpty = false;
        for (var i = start - 1; i < end; i++)
        {
            if (!transcript.Segments[i].IsEmpty)
            {
                nonEmpty = true;
                break;
            }
        }

        var spanEnd = transcript.Segments[end - 1].End;
        var withinDuration = transcript.AudioDuration is not { } duration || spanEnd <= duration;

        bool? quoteMatches = null;
        if (quote is not null)
        {
            var span = TranscriptWindowBuilder.FromRun(transcript, start, end);
            quoteMatches = ContainsNormalized(span.Text, quote);
        }

        return new ResolvedCitation
        {
            Citation = citation,
            Check = new CitationCheck
            {
                Resolves = true,
                NonEmpty = nonEmpty,
                WithinDuration = withinDuration,
                QuoteMatches = quoteMatches,
            },
            Start = transcript.Segments[start - 1].Start,
            End = spanEnd,
        };
    }

    public static AnswerValidation Validate(
        AnswerDocument answer, TranscriptDocument transcript, bool expectChronological = false)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(transcript);

        var bullets = new List<ResolvedBullet>(answer.Bullets.Count);
        foreach (var bullet in answer.Bullets)
        {
            bullets.Add(new ResolvedBullet
            {
                Bullet = bullet,
                Citations = [.. bullet.Citations.Select(c => Resolve(c, transcript, bullet.Quote))],
            });
        }

        return new AnswerValidation
        {
            Bullets = bullets,
            Monotone = expectChronological ? IsMonotone(bullets) : null,
        };
    }

    /// <summary>
    /// The quote-substring check, in the one normalisation retrieval and validation share:
    /// token-boundary containment, so <c>art</c> can never claim to be inside <c>start</c>.
    /// </summary>
    public static bool ContainsNormalized(string span, string quote)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(quote);

        var normalisedQuote = SearchTokenizer.Normalize(quote);
        if (normalisedQuote.Length == 0)
        {
            return false;
        }

        var normalisedSpan = SearchTokenizer.Normalize(span);
        return (" " + normalisedSpan + " ").Contains(" " + normalisedQuote + " ", StringComparison.Ordinal);
    }

    private static bool IsMonotone(List<ResolvedBullet> bullets)
    {
        var previousEnd = 0;
        foreach (var bullet in bullets)
        {
            foreach (var citation in bullet.Citations)
            {
                if (!citation.Check.Resolves)
                {
                    continue;
                }

                if (citation.Citation.StartSegment!.Value <= previousEnd)
                {
                    return false;
                }

                previousEnd = citation.Citation.EndSegment!.Value;
            }
        }

        return true;
    }
}
