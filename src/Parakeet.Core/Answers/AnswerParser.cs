using System.Text;

namespace Parakeet.Core.Answers;

/// <summary>
/// Parses a model's raw output into an <see cref="AnswerDocument"/>. The shape it reads is the
/// one the citation grammar produces — labelled bullets, bracketed segment ids, an optional
/// <c>«…»</c> verbatim quote, <c>NOT_IN_TRANSCRIPT</c> as the whole-answer abstention — but it
/// does not assume the grammar was enforced: the post-hoc path (prompt, parse, resolve) was
/// FullCite's best-scoring configuration, and this parser is what makes it possible here.
/// </summary>
/// <remarks>
/// Tolerant of text, strict about citations. A line that is not a bullet still becomes one,
/// uncited, because dropping model output silently would hide exactly the behaviour the
/// validator exists to catch. A bracket group is taken as a citation group only when everything
/// inside it parses as ids or the <c>?</c> marker; <c>[laughs]</c> stays in the text, inert —
/// only structured citations ever render as anything clickable, so a bracket left in prose can
/// never impersonate one.
/// </remarks>
public static class AnswerParser
{
    /// <summary>The whole-answer abstention sentinel, exactly as the grammar spells it.</summary>
    public const string AbstainSentinel = "NOT_IN_TRANSCRIPT";

    /// <param name="modelOutput">The engine's stream, concatenated.</param>
    /// <param name="allowLead">
    /// Take prose ahead of the first bullet as <see cref="AnswerDocument.Lead"/> rather than as
    /// a claim among the others. On wherever the prompt asked for an opening sentence, which
    /// since 2026-08-25 is both answer modes; off leaves stray prose rendering as the uncited
    /// claim it is. The lead goes through the citation machinery either way — what this switches
    /// is only whether the shape was asked for.
    /// </param>
    public static AnswerDocument Parse(string modelOutput, bool allowLead = false)
    {
        ArgumentNullException.ThrowIfNull(modelOutput);

        // A template that forces its think block open leaves a literal `<think>` at the front
        // of the stream under `--reasoning-format none` (measured 2026-08-16); unstripped it
        // would parse as a junk bullet and defeat the abstain match. Only the leading tag: a
        // tag deeper in the text is model output the validator should get to see.
        var output = modelOutput.TrimStart();
        if (output.StartsWith("<think>", StringComparison.Ordinal))
        {
            output = output["<think>".Length..].TrimStart();
        }

        var bullets = new List<AnswerBullet>();
        AnswerBullet? lead = null;
        var abstained = false;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsSentinelLine(line))
            {
                abstained = true;
                continue;
            }

            var bullet = ParseBullet(line);
            if (bullet is null)
            {
                continue;
            }

            // The lead is the prose before the claims begin, and only there: once a bullet has
            // been seen, a later unmarked line is a claim the model forgot to mark, not a second
            // framing sentence. Only the first such line is taken — a model that writes three
            // paragraphs of preamble has written claims, and they are marked as claims.
            if (allowLead
                && lead is null
                && bullets.Count == 0
                && !line.StartsWith("- ", StringComparison.Ordinal))
            {
                lead = bullet;
                continue;
            }

            bullets.Add(bullet);
        }

        // A sentinel beside claims is a contradiction no renderer should repeat: the claims are
        // the checkable half, so they stand and the abstention is dropped rather than the two
        // rendering together as "the recording doesn't answer that" above a list of answers.
        return new AnswerDocument
        {
            Bullets = bullets,
            Lead = lead,
            Abstained = abstained && bullets.Count == 0 && lead is null,
        };
    }

    /// <summary>
    /// The sentinel as a whole line, tolerating the dressing a post-hoc model puts on it — a
    /// bullet marker, bold or italic marks, terminal punctuation — because rendering the raw
    /// internal token as a claim is worse than reading it generously. A sentinel <em>inside</em>
    /// prose stays inert: only a line that is nothing but the token counts.
    /// </summary>
    private static bool IsSentinelLine(string line)
    {
        var candidate = line;
        if (candidate.StartsWith("- ", StringComparison.Ordinal))
        {
            candidate = candidate[2..].Trim();
        }

        candidate = candidate.Trim('*', '_').TrimEnd('.', '!').Trim();
        return candidate == AbstainSentinel;
    }

    private static AnswerBullet? ParseBullet(string line)
    {
        var body = line.StartsWith("- ", StringComparison.Ordinal) ? line[2..]
            : line == "-" ? string.Empty
            : line;

        var (text, citations) = ExtractCitations(body);
        var (remaining, quote) = ExtractQuote(text);

        // Guillemets are the answer's reserved quote marks — the grammar excludes them from free
        // text — so any left in the prose after the one quote was lifted are re-marked as plain
        // quotes rather than rendering dressed as the verified one.
        remaining = remaining.Replace('«', '“').Replace('»', '”');

        var (finalText, label) = ExtractLabel(remaining);

        if (finalText.Length == 0 && citations.Count == 0 && quote is null)
        {
            return null;
        }

        return new AnswerBullet
        {
            Label = label,
            Text = finalText,
            Quote = quote,
            Citations = citations,
        };
    }

    private static (string Text, IReadOnlyList<Citation> Citations) ExtractCitations(string body)
    {
        var citations = new List<Citation>();
        var text = new StringBuilder(body.Length);
        var i = 0;

        while (i < body.Length)
        {
            var open = body.IndexOf('[', i);
            if (open < 0)
            {
                text.Append(body, i, body.Length - i);
                break;
            }

            var close = body.IndexOf(']', open + 1);
            if (close < 0)
            {
                text.Append(body, i, body.Length - i);
                break;
            }

            text.Append(body, i, open - i);
            var inner = body[(open + 1)..close];
            var parts = inner.Split(',');

            if (parts.All(p => Citation.LooksLikeCitation(p)))
            {
                foreach (var part in parts)
                {
                    citations.Add(Citation.Parse(part));
                }
            }
            else
            {
                text.Append(body, open, close - open + 1);
            }

            i = close + 1;
        }

        return (Collapse(text.ToString()), citations);
    }

    /// <summary>
    /// Reads the quote for the validator to check, and leaves it exactly where the model put it.
    /// </summary>
    /// <remarks>
    /// Lifting it out was right while the grammar shaped every bullet — <c>text " " quote " "
    /// cites</c> puts the quote last, so removing it left a whole sentence behind. Ungrammared,
    /// which is the shipped decode since 2026-08-25, a model writes the quote into the sentence
    /// where it reads naturally, and cutting it out left holes: "The budget was allegedly." was a
    /// real bullet, its subject removed by this method (observed 2026-08-25). The quote is
    /// re-marked with ordinary quotation marks by the caller, so it still reads as quoted rather
    /// than as the model's own words, and <see cref="CitationValidator"/> still checks it against
    /// the cited span — what changes is only that the sentence survives.
    /// </remarks>
    private static (string Text, string? Quote) ExtractQuote(string text)
    {
        var open = text.IndexOf('«', StringComparison.Ordinal);
        if (open < 0)
        {
            return (text, null);
        }

        var close = text.IndexOf('»', open + 1);
        if (close < 0)
        {
            return (text, null);
        }

        var quote = text[(open + 1)..close].Trim();
        return (text, quote.Length == 0 ? null : quote);
    }

    private static (string Text, string? Label) ExtractLabel(string text)
    {
        // The grammar's label: up to forty characters before ": ", with brackets and colons
        // excluded by construction — the first ": " found guarantees the colon exclusion.
        var separator = text.IndexOf(": ", StringComparison.Ordinal);
        if (separator is <= 0 or > 40)
        {
            return (text, null);
        }

        var candidate = text[..separator];
        if (candidate.Contains('[', StringComparison.Ordinal) || candidate.Contains(']', StringComparison.Ordinal))
        {
            return (text, null);
        }

        return (text[(separator + 2)..].Trim(), candidate.Trim());
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                // A citation lifted from mid-sentence leaves the space that was in front of it:
                // "…the staging environment [S1-S4]." becomes "…the staging environment ." on
                // screen. Only the period and the comma are closed up — a space before ; : ! ?
                // is correct French typography, and the twenty-five languages are the
                // requirement.
                if (ch is not ('.' or ','))
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
            }

            // A citation removed from between a comma and a full stop — "…on the PS2, [S1]." —
            // leaves ",.", which is our own damage and is not punctuation in any of them. A
            // separated citation list — "…refunds, [S1], [S2], [S3]." — leaves a run of commas
            // by the same mechanism.
            if (builder.Length > 0 && builder[^1] is ',' or ';')
            {
                if (ch == '.')
                {
                    builder.Length--;
                }
                else if (ch is ',' or ';')
                {
                    continue;
                }
            }

            builder.Append(ch);
        }

        // And a citation that ended the sentence leaves the comma before it dangling.
        return builder.ToString().TrimEnd(' ', ',', ';');
    }
}
