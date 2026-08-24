using System.Text;
using Parakeet.Core.Answers;
using Parakeet.Core.Retrieval;

namespace Parakeet.Engine.LlamaServer;

/// <summary>
/// Builds the prompt and the GBNF grammar for one question. The two are built together because
/// they are two statements of one contract: the prompt asks for bullets citing segment ids, and
/// the grammar makes any other shape unsamplable — including any id that is not live.
/// </summary>
/// <remarks>
/// The evidence lines carry no speaker labels — decided by the maintainer 2026-08-24, the
/// register's question 2: a resolved citation scrolls to cues that already wear their chips, so
/// the reader sees who spoke without the model ever being in a position to say it. The language
/// line appears only when the caller knows a language, and that is the decided shape (register,
/// 2026-08-24): the transcript's language is the request hint or nothing, so a hintless
/// transcript gets the unlocalised prompt and no claim is made about the answer's language.
/// Citation tokens stay ASCII whatever the language, so the grammar never has to know it.
/// </remarks>
public static class AnswerPromptBuilder
{
    /// <summary>The prompt: instruction, evidence lines named by their citation ids, question.</summary>
    public static string BuildPrompt(AskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder();
        builder.Append("You are answering questions about a recording, from transcript evidence.\n");
        builder.Append("Answer as short bullets, one claim per line, starting with \"- \".\n");
        builder.Append("Every bullet ends with the ids of the evidence that supports it, in square brackets, ");
        builder.Append("exactly as they appear below — for example [S12-S15].\n");
        builder.Append("Never write a timestamp, a time of day, or a duration.\n");
        builder.Append("Quote the transcript verbatim inside «» in each bullet.\n");
        builder.Append("A claim you cannot support from the evidence gets [?] instead of an id.\n");
        builder.Append("If the evidence does not answer the question at all, reply exactly: ");
        builder.Append(AnswerParser.AbstainSentinel);
        builder.Append('\n');

        if (request.Language is { } language)
        {
            builder.Append("Answer in the language whose BCP-47 tag is: ").Append(language).Append('\n');
        }

        builder.Append("\nEvidence:\n");
        foreach (var window in request.Evidence)
        {
            builder.Append('[').Append(window.CitationId).Append("] ").Append(window.Text).Append('\n');
        }

        builder.Append("\nQuestion: ").Append(request.Question).Append('\n');
        builder.Append("Answer:\n");
        return builder.ToString();
    }

    /// <summary>
    /// A GBNF grammar admitting only bullets cited by the live ids — each evidence window's own
    /// <see cref="TranscriptWindow.CitationId"/>, enumerated literally, so an id that is not live
    /// is not merely discouraged but unsamplable. Null when there is no evidence to enumerate:
    /// a grammar over an empty id set could only cite <c>[?]</c>, and the caller decides whether
    /// that or an unconstrained answer is the honest fallback.
    /// </summary>
    /// <remarks>
    /// Bounded repetition (<c>{m,n}</c>) throughout rather than chained <c>?</c>, per the GBNF
    /// README's own performance warning. The abstain production is a measured dial — see
    /// <see cref="LlamaServerOptions.AllowAbstain"/> — and the quote production is what turns
    /// FullCite's finding into a mechanical check here.
    /// </remarks>
    public static string? BuildGrammar(
        IReadOnlyList<TranscriptWindow> evidence, bool allowAbstain = true, bool requireQuote = true)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count == 0)
        {
            return null;
        }

        var ids = string.Join(" | ", evidence.Select(w => "\"" + w.CitationId + "\""));

        var builder = new StringBuilder();
        builder.Append(allowAbstain
            ? "root ::= abstain | bullet{1,8}\nabstain ::= \"" + AnswerParser.AbstainSentinel + "\" \"\\n\"\n"
            : "root ::= bullet{1,8}\n");

        builder.Append(requireQuote
            ? "bullet ::= \"- \" text \" \" quote \" \" cites \"\\n\"\n"
            : "bullet ::= \"- \" text \" \" cites \"\\n\"\n");

        // Free text may be any code point except the structural ones; the 25 languages need no
        // more of the grammar than that, which is the point of keeping citations ASCII.
        builder.Append("text ::= [^\\n\\[\\]\\u00AB\\u00BB]{1,400}\n");

        if (requireQuote)
        {
            builder.Append("quote ::= \"\\u00AB\" [^\\n\\u00AB\\u00BB]{3,300} \"\\u00BB\"\n");
        }

        builder.Append("cites ::= \"[\" cite (\", \" cite){0,4} \"]\" | \"[?]\"\n");
        builder.Append("cite ::= ").Append(ids).Append('\n');

        return builder.ToString();
    }
}
