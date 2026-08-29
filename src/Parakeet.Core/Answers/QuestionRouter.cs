using Parakeet.Core.Retrieval;

namespace Parakeet.Core.Answers;

/// <summary>Why the router sent a question the way it did — the reason, not a score.</summary>
public enum RoutingBasis
{
    /// <summary>The question asks for the recording as a whole in so many words.</summary>
    GlobalCue = 0,

    /// <summary>Every term in it is one the index cannot order windows by.</summary>
    NothingToRankOn = 1,

    /// <summary>The question names something the index can find.</summary>
    Findable = 2,

    /// <summary>Global by kind, answered from an even sample of the recording because reading it
    /// all was too expensive to start without being asked.</summary>
    GlobalButTooLong = 3,
}

/// <summary>One routing call's outcome: the mode, why, and the sentence a reader is owed when
/// the mode is not what the question asked for.</summary>
public sealed record RoutingDecision
{
    public required AnswerMode Mode { get; init; }

    public required RoutingBasis Basis { get; init; }

    /// <summary>User copy, set only when the router did something the asker would not predict —
    /// today, only when a whole-recording question was answered from retrieved parts because the
    /// recording was too long to read automatically. Null the rest of the time: the answer's own
    /// provenance line already says which mode produced it, and a notice on every question is
    /// noise that teaches nobody anything.</summary>
    public string? Notice { get; init; }
}

/// <summary>
/// Picks retrieval or the whole transcript from the question itself, so a person does not have to
/// know which tier answers which shape of question. The register's decision 3 sketched exactly
/// this — "a heuristic first … and the model itself as the classifier if the heuristic fails" —
/// and what is built here is the heuristic half.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model is not the classifier, and cannot be here.</b> Routing decides the context the
/// child process is started with, so a model-based classifier would have to load a model in order
/// to decide how to load it. Routing therefore stays lexical and runs before anything is loaded —
/// the same reason retrieval runs before the transcriber is unloaded.
/// </para>
/// <para>
/// <b>It leans to retrieval when unsure, because the two mistakes do not cost the same.</b>
/// Answering a pointed question from the whole recording costs a prefill measured in minutes on
/// integrated graphics; answering a global question from retrieval costs seconds and a visibly
/// thin answer the asker can immediately re-ask. Cheap-and-obviously-wrong beats
/// expensive-and-plausible, which is the same reasoning the register applies to the global path
/// degrading into "the model saw a tenth of the transcript and guessed the rest".
/// </para>
/// <para>
/// <b>Nothing here is measured.</b> The cue list is English, and the labelled question set's
/// `global` questions are what will say how often either rule is right — in this language or any
/// other. `docs/UNPROVEN.md` carries that. The second rule is the one that does not depend on a
/// vocabulary at all, and it is deliberately the fallback rather than the primary: it fires only
/// when every term is ubiquitous, which most global questions are not, because they use words
/// like "summary" that the recording itself never says.
/// </para>
/// </remarks>
public static class QuestionRouter
{
    /// <summary>
    /// Word beginnings that ask for the whole recording. Stems rather than words so one entry
    /// covers "summary", "summarise", "summarized" and the rest without a list per inflection.
    /// </summary>
    private static readonly string[] GlobalStems =
    [
        "summar", "recap", "overview", "synopsis", "gist", "rundown", "tldr", "takeaway",
    ];

    /// <summary>
    /// Whole phrases, matched on word boundaries against the normalised question. Written in the
    /// tokenizer's own normal form — punctuation is gone, so "tl;dr" is "tl dr" and "what's" is
    /// "what s".
    /// </summary>
    private static readonly string[] GlobalPhrases =
    [
        "main topic", "main topics", "main point", "main points",
        "key point", "key points", "key takeaway", "key takeaways",
        "big picture", "high level", "tl dr", "overall", "in general",
        "what is this about", "what s this about", "what was this about",
        "what is this recording about", "what is it about",
        "what do they talk about", "what did they talk about",
        "what do they discuss", "what did they discuss", "what is discussed",
        "what topics", "which topics", "what happened in this",
    ];

    /// <summary>
    /// Routes one question.
    /// </summary>
    /// <param name="question">What the person typed.</param>
    /// <param name="retriever">The index over this transcript — the second rule's evidence.</param>
    /// <param name="wholeTranscriptIsAffordable">
    /// Whether reading the whole recording is cheap enough to start without being asked. The
    /// caller decides what that means; the application's answer is that the whole transcript fits
    /// the context the engine allocates for retrieval anyway, so the automatic path never commits
    /// a person to a larger cache — or a longer prefill — than the tier they were already on.
    /// </param>
    public static RoutingDecision Route(
        string question, Bm25Retriever retriever, bool wholeTranscriptIsAffordable)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(retriever);

        var basis =
            AsksForTheWholeRecording(question) ? RoutingBasis.GlobalCue
            : retriever.EveryTermIsUbiquitous(question) ? RoutingBasis.NothingToRankOn
            : RoutingBasis.Findable;

        if (basis == RoutingBasis.Findable)
        {
            return new RoutingDecision { Mode = AnswerMode.Retrieval, Basis = basis };
        }

        if (!wholeTranscriptIsAffordable)
        {
            return new RoutingDecision
            {
                // A survey since 2026-08-27, and retrieval before it. The asker wanted the whole
                // recording; the honest thing to hand them when all of it will not fit is a
                // thinner pass over all of it, not a thorough pass over the eight windows a
                // scorer liked — and a global question is the case where the scorer has least to
                // go on. Reading every minute stays available and stays a decision, because it
                // was measured at 1,112.6 s of prefill on the second machine.
                Mode = AnswerMode.Survey,
                Basis = RoutingBasis.GlobalButTooLong,

                // The one case worth a sentence: the asker wanted the whole recording, did not
                // get all of it, and would otherwise read a sampled answer as a complete one.
                Notice = "This recording is long, so the answer below comes from an even sample "
                    + "across all of it rather than every minute - it may miss things said in "
                    + "between. For a pass over every word, switch answering to \"the whole "
                    + "transcript\" in Settings.",
            };
        }

        return new RoutingDecision { Mode = AnswerMode.WholeTranscript, Basis = basis };
    }

    /// <summary>
    /// Whether the question names the recording as a whole. Normalised through the same tokenizer
    /// retrieval uses, so accents, case and punctuation are handled once and identically.
    /// </summary>
    public static bool AsksForTheWholeRecording(string question)
    {
        ArgumentNullException.ThrowIfNull(question);

        var normalised = SearchTokenizer.Normalize(question);
        if (normalised.Length == 0)
        {
            return false;
        }

        var padded = " " + normalised + " ";
        foreach (var phrase in GlobalPhrases)
        {
            if (padded.Contains(" " + phrase + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var token in SearchTokenizer.Tokenize(question))
        {
            foreach (var stem in GlobalStems)
            {
                if (token.StartsWith(stem, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
