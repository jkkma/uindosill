using System.Text;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Answers;

/// <summary>Generated notes and the original source runs their checked citations name.</summary>
public sealed record TranscriptSummaryNote(string Text, IReadOnlyList<TranscriptWindow> Evidence);

/// <summary>
/// Plans bounded passes over every cover window, and carries only checked source ids between
/// summary passes. It checks citation structure, membership and quotes, not the truth of a
/// paraphrase. Notes are generated text and must never replace transcript text in Evidence.
/// </summary>
public static class TranscriptSummaryBuilder
{
    public const int DefaultBudgetChars = 24_000;
    public const int DefaultNoteBudgetChars = 8_000;
    public const int MaximumBatches = 256;
    public const int MaximumReductionRounds = 6;

    /// <summary>
    /// Keeps every supplied cover window intact and in order, exactly once. The budget includes
    /// each rendered id and newline, using the prompt's <c>[id] text\n</c> shape. A window that
    /// cannot fit fails explicitly instead of truncating speech or silently sampling it away.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TranscriptWindow>> Partition(
        IReadOnlyList<TranscriptWindow> cover, int budgetChars = DefaultBudgetChars)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetChars, 1);
        var batches = new List<IReadOnlyList<TranscriptWindow>>();
        var current = new List<TranscriptWindow>();
        long chars = 0;
        foreach (var window in cover)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (window.FirstSegment < 1 || window.LastSegment < window.FirstSegment)
            {
                throw new ArgumentException("A summary cover window has invalid segment bounds.", nameof(cover));
            }

            var size = (long)window.Text.Length + window.CitationId.Length + 4;
            if (size > budgetChars)
            {
                throw new InvalidOperationException(
                    $"Summary source window {window.CitationId} exceeds the {budgetChars}-character pass budget. " +
                    "It cannot be summarized without splitting that source window.");
            }

            if (current.Count > 0 && chars + size > budgetChars)
            {
                batches.Add(current.ToArray());
                current.Clear();
                chars = 0;
            }

            current.Add(window);
            chars += size;
        }

        if (current.Count > 0)
        {
            batches.Add(current.ToArray());
        }

        if (batches.Count > MaximumBatches)
        {
            throw new InvalidOperationException($"This transcript needs more than {MaximumBatches} summary passes.");
        }

        return batches;
    }

    /// <summary>
    /// Normalizes all claims of a section or reduction result. Every claim must have usable
    /// text and source citations, every id must have been shown to that pass (gaps included),
    /// and explicit verbatim quotes must match an original cited span. A failed result aborts
    /// the summary; dropping a failed section would misrepresent complete source coverage.
    /// </summary>
    public static TranscriptSummaryNote CreateNote(
        string raw,
        TranscriptDocument document,
        IReadOnlyList<TranscriptWindow> shownEvidence,
        int budgetChars = DefaultNoteBudgetChars)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shownEvidence);
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetChars, 1);
        if (shownEvidence.Count == 0)
        {
            throw new InvalidOperationException("A summary pass has no source evidence to anchor its notes.");
        }

        var allowed = shownEvidence.OrderBy(w => w.FirstSegment).ThenBy(w => w.LastSegment).ToArray();
        foreach (var window in allowed)
        {
            if (window.FirstSegment < 1 || window.LastSegment < window.FirstSegment
                || window.LastSegment > document.Segments.Count
                || window != TranscriptWindowBuilder.FromRun(document, window.FirstSegment, window.LastSegment))
            {
                throw new ArgumentException("Summary evidence must contain unchanged original transcript windows.", nameof(shownEvidence));
            }
        }

        var answer = AnswerParser.Parse(raw, allowLead: true);
        if (answer.Abstained || answer.IsEmpty)
        {
            throw new InvalidOperationException("A summary pass returned no usable cited notes. The complete summary cannot continue.");
        }

        ValidateReservedMarks(raw);
        var claims = answer.Lead is { } lead
            ? new[] { lead }.Concat(answer.Bullets)
            : answer.Bullets;
        var lines = new List<string>();
        var sources = new List<TranscriptWindow>();
        var seen = new HashSet<(int First, int Last)>();
        foreach (var claim in claims)
        {
            if ((string.IsNullOrWhiteSpace(claim.Text) && string.IsNullOrWhiteSpace(claim.Quote))
                || claim.Citations.Count == 0 || claim.Citations.Any(c => c.IsUncitedMarker))
            {
                throw new InvalidOperationException("A summary pass returned an empty or uncited claim. Its notes cannot be used as evidence.");
            }

            var resolved = CitationValidator.Resolve(claim, document);
            if (resolved.QuoteFound == false || resolved.Citations.Any(c =>
                    !c.Check.Resolves || !c.Check.NonEmpty || !c.Check.WithinDuration))
            {
                throw new InvalidOperationException("A summary pass returned a citation or quotation that does not match the original transcript.");
            }

            foreach (var citation in claim.Citations)
            {
                var first = citation.StartSegment!.Value;
                var last = citation.EndSegment!.Value;
                if (!WasShown(first, last, allowed))
                {
                    throw new InvalidOperationException(
                        $"Summary citation [{citation.Raw}] includes source segments not shown to this pass.");
                }

                if (seen.Add((first, last)))
                {
                    sources.Add(TranscriptWindowBuilder.FromRun(document, first, last));
                }
            }

            var line = new StringBuilder("- ");
            if (claim.Label is { } label)
            {
                line.Append(label).Append(": ");
            }

            // The parser retains a quote in Text at its original position, using ordinary
            // quotation marks. It has already passed the explicit quote check above; adding
            // Quote again would duplicate the words and break quotes embedded in a sentence.
            line.Append(claim.Text);

            line.Append(" [").AppendJoin(", ", claim.Citations.Select(c =>
                c.StartSegment == c.EndSegment
                    ? FormattableString.Invariant($"S{c.StartSegment}")
                    : FormattableString.Invariant($"S{c.StartSegment}-S{c.EndSegment}"))).Append(']');
            lines.Add(line.ToString());
        }

        var text = string.Join('\n', lines);
        if (text.Length > budgetChars)
        {
            throw new InvalidOperationException(
                $"A summary pass returned more than {budgetChars} characters of notes. Its claims cannot be silently truncated.");
        }

        return new TranscriptSummaryNote(text, sources.ToArray());
    }

    /// <summary>
    /// Checks a final synthesis against only the original source runs referenced by its input
    /// notes. Uncited prose and admitted-uncited markers remain for the UI to mark unverified;
    /// any supplied source citation must resolve, stay inside the shown runs, and support an
    /// explicit verbatim quote in at least one of that claim's cited spans.
    /// </summary>
    public static void ValidateFinalAnswer(
        AnswerDocument answer,
        TranscriptDocument document,
        IReadOnlyList<TranscriptWindow> shownEvidence)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shownEvidence);
        var allowed = shownEvidence.OrderBy(w => w.FirstSegment).ThenBy(w => w.LastSegment).ToArray();
        var claims = answer.Lead is { } lead
            ? new[] { lead }.Concat(answer.Bullets)
            : answer.Bullets;
        foreach (var claim in claims)
        {
            var resolved = CitationValidator.Resolve(claim, document);
            if (resolved.QuoteFound == false)
            {
                throw new InvalidOperationException("The final summary quotes words that do not match its cited original transcript spans.");
            }

            foreach (var citation in resolved.Citations.Where(c => !c.Citation.IsUncitedMarker))
            {
                if (!citation.Check.Resolves || !citation.Check.NonEmpty || !citation.Check.WithinDuration)
                {
                    throw new InvalidOperationException("The final summary contains a citation that does not resolve to speech inside the original recording.");
                }

                if (!WasShown(citation.Citation.StartSegment!.Value, citation.Citation.EndSegment!.Value, allowed))
                {
                    throw new InvalidOperationException(
                        $"Final summary citation [{citation.Citation.Raw}] includes source segments not shown to this synthesis pass.");
                }
            }
        }
    }

    /// <summary>
    /// Combines consecutive validated notes up to the synthesis budget. Each input note appears
    /// once; original source runs are deduplicated by bounds without filling gaps between them.
    /// Call again after reducing these groups if their combined output still needs another pass.
    /// </summary>
    public static IReadOnlyList<TranscriptSummaryNote> GroupNotes(
        IReadOnlyList<TranscriptSummaryNote> notes, int budgetChars = DefaultBudgetChars)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetChars, 1);
        var groups = new List<TranscriptSummaryNote>();
        var current = new List<TranscriptSummaryNote>();
        long chars = 0;
        foreach (var note in notes)
        {
            ArgumentNullException.ThrowIfNull(note);
            if (string.IsNullOrWhiteSpace(note.Text) || note.Evidence.Count == 0)
            {
                throw new InvalidOperationException("A summary note is empty or has no checked source references.");
            }

            if (note.Text.Length > budgetChars)
            {
                throw new InvalidOperationException($"A summary note exceeds the {budgetChars}-character synthesis budget.");
            }

            var separator = current.Count == 0 ? 0 : 2;
            if (current.Count > 0 && chars + separator + note.Text.Length > budgetChars)
            {
                groups.Add(Combine(current));
                current.Clear();
                chars = 0;
                separator = 0;
            }

            current.Add(note);
            chars += separator + note.Text.Length;
        }

        if (current.Count > 0)
        {
            groups.Add(Combine(current));
        }

        if (groups.Count > MaximumBatches)
        {
            throw new InvalidOperationException($"The summary needs more than {MaximumBatches} synthesis passes.");
        }

        return groups;
    }

    /// <summary>Stops stalled or unbounded intermediate reductions before scheduling another round.</summary>
    public static void EnsureReductionProgress(
        IReadOnlyList<TranscriptSummaryNote> previous,
        IReadOnlyList<TranscriptSummaryNote> next,
        int round,
        int maxRounds = MaximumReductionRounds)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentOutOfRangeException.ThrowIfLessThan(round, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRounds, 1);
        if (round > maxRounds)
        {
            throw new InvalidOperationException($"The summary exceeded {maxRounds} intermediate reduction rounds.");
        }

        if (next.Count == 0 || next.Sum(n => (long)n.Text.Length) >= previous.Sum(n => (long)n.Text.Length))
        {
            throw new InvalidOperationException("The summary reduction did not shorten its notes. Another pass would not make bounded progress.");
        }
    }

    private static TranscriptSummaryNote Combine(IReadOnlyList<TranscriptSummaryNote> notes) => new(
        string.Join("\n\n", notes.Select(n => n.Text)),
        notes.SelectMany(n => n.Evidence).DistinctBy(w => (w.FirstSegment, w.LastSegment)).ToArray());

    private static bool WasShown(int first, int last, IReadOnlyList<TranscriptWindow> allowed)
    {
        var next = first;
        foreach (var window in allowed)
        {
            if (window.LastSegment < next)
            {
                continue;
            }

            if (window.FirstSegment > next)
            {
                return false;
            }

            if (window.LastSegment >= last)
            {
                return true;
            }

            next = window.LastSegment + 1;
        }

        return false;
    }

    private static void ValidateReservedMarks(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            var quoteStarts = line.Count(c => c == '«');
            if (quoteStarts != line.Count(c => c == '»') || quoteStarts > 1)
            {
                throw new InvalidOperationException("A summary pass returned malformed or multiple verbatim quotations on one claim.");
            }

            // The forgiving display parser leaves malformed citation-like brackets as prose.
            // They cannot pass into a later prompt looking like additional checked source ids.
            var start = 0;
            while ((start = line.IndexOf('[', start)) >= 0)
            {
                var end = line.IndexOf(']', start + 1);
                var inner = end < 0 ? line[(start + 1)..] : line[(start + 1)..end];
                if (inner.TrimStart().StartsWith('S')
                    && (end < 0 || !inner.Split(',').All(Citation.LooksLikeCitation)))
                {
                    throw new InvalidOperationException("A summary pass returned a malformed source citation.");
                }

                if (end < 0)
                {
                    break;
                }

                start = end + 1;
            }
        }
    }
}
