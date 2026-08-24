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

    public static AnswerDocument Parse(string modelOutput)
    {
        ArgumentNullException.ThrowIfNull(modelOutput);

        var bullets = new List<AnswerBullet>();
        var abstained = false;

        foreach (var rawLine in modelOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line == AbstainSentinel)
            {
                abstained = true;
                continue;
            }

            var bullet = ParseBullet(line);
            if (bullet is not null)
            {
                bullets.Add(bullet);
            }
        }

        return new AnswerDocument { Bullets = bullets, Abstained = abstained };
    }

    private static AnswerBullet? ParseBullet(string line)
    {
        var body = line.StartsWith("- ", StringComparison.Ordinal) ? line[2..]
            : line == "-" ? string.Empty
            : line;

        var (text, citations) = ExtractCitations(body);
        var (remaining, quote) = ExtractQuote(text);
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
        var remaining = Collapse(text[..open] + text[(close + 1)..]);
        return (remaining, quote.Length == 0 ? null : quote);
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
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
