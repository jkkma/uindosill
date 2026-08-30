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
    /// <summary>
    /// The prompt: instruction, evidence lines named by their citation ids, question. The dials
    /// mirror <see cref="BuildGrammar"/>'s: an instruction the grammar makes unsamplable — reply
    /// with a sentinel the abstain production does not exist for — steers the model toward an
    /// output it cannot produce, which is measured as degraded answers, not as nothing.
    /// </summary>
    public static string BuildPrompt(AskRequest request, bool allowAbstain = true, bool requireQuote = true)
    {
        var (instruction, userContent) = BuildMessages(request, allowAbstain, requireQuote);
        return instruction + "\n" + userContent + "Answer:\n";
    }

    /// <summary>
    /// The same contract split for the chat endpoint: the instruction block as the system
    /// message, evidence and question as the user message. The model's own template supplies the
    /// turn structure — and with it the end-of-turn the raw-prompt path was measured to lack
    /// (2026-08-24, docs/UNPROVEN.md) — so no "Answer:" cue is appended; the template's
    /// assistant turn is that cue.
    /// </summary>
    public static (string Instruction, string UserContent) BuildMessages(
        AskRequest request, bool allowAbstain = true, bool requireQuote = true)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A survey is the whole-recording job done on a sample: everything below that is
        // about coverage and grouping applies to it unchanged, and the one thing that must
        // not is the sentence claiming the transcript is complete.
        var sampled = request.Mode == AnswerMode.Survey;
        var whole = request.Mode == AnswerMode.WholeTranscript || sampled;
        var builder = new StringBuilder();

        if (whole)
        {
            // The whole-transcript instruction is a different job, not a longer one: the model
            // is holding the entire recording rather than a shortlist retrieval already judged
            // relevant, so what it needs told is coverage and grouping — the two things a
            // summary is graded on and a pointed answer never needs. The failure this steers
            // away from is the one the register predicted for the global path: an answer drawn
            // from the opening minutes that reads exactly like an answer drawn from all of them.
            if (sampled)
            {
                // The gaps are stated, and stated first. A sample presented as a transcript
                // is the failure that matters here: the model would otherwise narrate a
                // three-hour recording as though it had read every minute, and every word of
                // that would carry a real citation, which is what would make it convincing.
                builder.Append("You are describing a recording. Below is an even sample ");
                builder.Append("taken across the whole of it — numbered parts in the order ");
                builder.Append("they were spoken, with gaps between them you cannot see. ");
                builder.Append("Describe what the sample shows, and do not claim to have ");
                builder.Append("read every minute.\n");
            }
            else
            {
                builder.Append("You are describing a recording. Below is its complete ");
                builder.Append("transcript, cut into numbered parts in the order they were ");
                builder.Append("spoken.\n");
            }
        }
        else
        {
            builder.Append("You are answering questions about a recording, from transcript evidence.\n");
        }

        // The opening sentence belongs to both modes, and the wording is one job in both: answer
        // what was asked. For "give me a summary" that is what the recording is and covers; for
        // "did they mention X" it is yes or no. Retrieval had no such line until 2026-08-25 and
        // read the worse for it — a list of cited fragments never says the "yes" the question
        // asked for, and a fragment lifted out of a digression reads as a non-sequitur with a
        // timestamp on it.
        builder.Append("Open with one sentence answering the question directly, on its own line, ");
        builder.Append("with no \"- \" in front of it, ending with ids like every other line.\n");

        if (whole)
        {
            // "Answer the question directly" alone turns a summary request into "This is a
            // summary of the recording", which answers it and says nothing — measured against
            // the wording this replaces, which produced "…is a Thursday Product Sync for the
            // mobile team covering budget, partnerships, app status and recent incidents". One
            // instruction still, with the summary case spelled out.
            builder.Append("If the question asks for a summary or an overview, that sentence ");
            builder.Append("says what the recording is and what it covers.\n");
            builder.Append("Then write bullets, one point per line, starting with \"- \".\n");
            builder.Append("Give each bullet a short topic label followed by \": \".\n");
            builder.Append("Group related points under one bullet, and draw on the whole recording ");
            builder.Append("rather than its opening.\n");

            // Two steers toward the takeaways, added 2026-08-30. Without them the overview reads
            // as a genre description: on one real recording it flattened the comparisons the
            // speakers drew by name into category labels, and dropped the two most repeatable
            // points in it — an on-record assurance and a prediction — while five bullets of
            // description all survived. Salience was the one axis the wording never asked for.
            builder.Append("Keep the proper names the transcript uses — people, titles, other ");
            builder.Append("works — and when the speakers describe something by comparing it to ");
            builder.Append("a named work, keep the name rather than a genre word.\n");
            builder.Append("A promise, assurance, prediction or announcement made in the ");
            builder.Append("recording is a point of its own: say who made it and what ");
            builder.Append("they said.\n");

            // The topic-label instruction invites section headings, and a heading is a line that
            // asserts nothing, cites nothing, and therefore renders as an unsupported claim —
            // observed 2026-08-25, "Development costs:" and "Financial impact and industry
            // context:" among real bullets. The maintainer's decision the same day: forbid them
            // in the prompt rather than guess at them in the parser, since the labels already
            // group what a heading would have grouped.
            builder.Append("Do not write section headings: every line is either that opening ");
            builder.Append("sentence or a bullet.\n");
        }
        else
        {
            // A question with one answer deserves one sentence. Forcing bullets under it made
            // the panel restate its own opening — "Yes, they mentioned Kojima…" above two
            // bullets saying where — where a paragraph would have read as an answer. The lead
            // carries ids like anything else, so stopping there costs no citation.
            builder.Append("If that sentence answers the question completely, write nothing more.\n");
            builder.Append("Otherwise add short bullets, one claim per line, starting with \"- \", ");
            builder.Append("each saying enough to make sense on its own, and each with a short ");
            builder.Append("topic label followed by \": \" when the bullets are about different things.\n");
        }

        builder.Append("Every line ends with the ids of the ");
        builder.Append(whole ? "parts" : "evidence");
        builder.Append(" that support it, in square brackets, ");
        builder.Append("exactly as they appear below — for example [S12-S15].");

        // "the parts", not "every part": the grammar admits five ids on a line, and "every"
        // demanded an enumeration a topic discussed in six parts could not sample (found
        // 2026-08-30) — the same contract-mismatch class this file's own remarks forbid. The
        // steer away from citing only the opening survives the word.
        builder.Append(whole ? " Cite the parts where a point is discussed, not only the first.\n" : "\n");

        builder.Append("Never write a timestamp, a time of day, or a duration.\n");
        if (requireQuote)
        {
            builder.Append("Quote the transcript verbatim inside «» in each bullet.\n");
        }

        builder.Append("A claim you cannot support from the ");
        builder.Append(whole ? "transcript" : "evidence");
        builder.Append(" gets [?] instead of an id.\n");
        if (allowAbstain)
        {
            builder.Append(whole
                ? "If the transcript does not answer the question at all, reply exactly: "
                : "If the evidence does not answer the question at all, reply exactly: ");
            builder.Append(AnswerParser.AbstainSentinel);
            builder.Append('\n');
        }

        // The file's name is provenance the application holds and the transcript does not: it is
        // how a person refers to the recording, and an overview that cannot name what it is
        // describing opens with "this recording". Fenced to naming on purpose — a file name is
        // not evidence, and a claim sourced from it would be the one line in the answer with no
        // segment behind it.
        if (whole && FileLabel(request.Transcript.SourceName) is { } label)
        {
            builder.Append("The recording's file is named \"").Append(label);
            builder.Append("\" — use it to name the recording, never as a fact about its contents.\n");
        }

        if (request.Language is { } language)
        {
            builder.Append("Answer in the language whose BCP-47 tag is: ").Append(language).Append('\n');
        }

        var instruction = builder.ToString();

        builder.Clear();
        builder.Append(whole
            ? (sampled ? "Transcript sample:\n" : "Transcript:\n")
            : "Evidence:\n");
        foreach (var window in request.Evidence)
        {
            builder.Append('[').Append(window.CitationId).Append("] ").Append(window.Text).Append('\n');
        }

        builder.Append("\nQuestion: ").Append(request.Question).Append('\n');
        return (instruction, builder.ToString());
    }

    /// <summary>
    /// The recording's file name without its directory or extension, or null when there is
    /// nothing usable. The directory never travels: it is the user's folder structure, and the
    /// prompt has no use for it.
    /// </summary>
    private static string? FileLabel(string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return null;
        }

        string label;
        try
        {
            label = Path.GetFileNameWithoutExtension(sourceName);
        }
        catch (ArgumentException)
        {
            // A source name that is not a path shape at all — it is a label, not a file.
            label = sourceName;
        }

        label = label.Trim();
        return label.Length == 0 ? null : label;
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
        IReadOnlyList<TranscriptWindow> evidence,
        bool allowAbstain = true,
        bool requireQuote = true,
        bool wantLead = false)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count == 0)
        {
            return null;
        }

        var ids = string.Join(" | ", evidence.Select(w => "\"" + w.CitationId + "\""));

        // The lead has a production wherever the prompt asks for one, and that is this file's
        // stated principle rather than tidiness: prompt and grammar are two statements of one
        // contract, and an instruction the grammar makes unsamplable steers the model toward an
        // output it cannot produce — measured as degraded answers, not as nothing. The bullets
        // go to zero for the same reason: the retrieval prompt says a complete opening sentence
        // may stand alone, and bullet{1,8} forced a padding bullet after it (found 2026-08-30).
        // Twelve, not eight, since 2026-08-30: the whole-transcript prompt now asks for a bullet
        // per takeaway, and the same recording that wrote five descriptive bullets under the old
        // wording wrote eleven under the new one — a ninth bullet the prompt just asked for must
        // not be the thing the grammar forbids.
        var root = wantLead ? "lead bullet{0,12}" : "bullet{1,12}";

        var builder = new StringBuilder();
        builder.Append(allowAbstain
            ? "root ::= abstain | " + root + "\nabstain ::= \"" + AnswerParser.AbstainSentinel + "\" \"\\n\"\n"
            : "root ::= " + root + "\n");

        if (wantLead)
        {
            builder.Append("lead ::= text \" \" cites \"\\n\"\n");
        }

        builder.Append(requireQuote
            ? "bullet ::= \"- \" text \" \" quote \" \" cites \"\\n\"\n"
            : "bullet ::= \"- \" text \" \" cites \"\\n\"\n");

        // Free text may be any code point except the structural ones; the 25 languages need no
        // more of the grammar than that, which is the point of keeping citations ASCII.
        builder.Append("text ::= [^\\n\\[\\]\\u00AB\\u00BB]{1,400}\n");

        // The quote excludes brackets exactly as free text does: the parser lifts citations from
        // the whole bullet before it lifts the quote, so a bracket admitted here would let the
        // model write an id inside «…» and have it promoted to a real citation. Eight characters
        // minimum, because a three-character quote («the») verifies against nearly any span and
        // verifies nothing.
        if (requireQuote)
        {
            builder.Append("quote ::= \"\\u00AB\" [^\\n\\[\\]\\u00AB\\u00BB]{8,300} \"\\u00BB\"\n");
        }

        builder.Append("cites ::= \"[\" cite (\", \" cite){0,4} \"]\" | \"[?]\"\n");
        builder.Append("cite ::= ").Append(ids).Append('\n');

        return builder.ToString();
    }
}
