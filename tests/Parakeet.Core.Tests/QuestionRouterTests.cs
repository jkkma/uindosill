using Parakeet.Core.Answers;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

/// <summary>
/// The router the register's decision 3 sketched, against a transcript with real vocabulary in
/// it — the questions here are the ones this project actually asked of a real recording, plus the
/// shapes the decision names.
/// </summary>
public class QuestionRouterTests
{
    /// <summary>
    /// A recording with distinct subjects, so a term can be selective in it. Long enough that
    /// "half the windows" means something: twenty windows of one line each.
    /// </summary>
    private static Bm25Retriever Index()
    {
        string[] lines =
        [
            "the meeting opened with the quarterly budget review",
            "the budget came in over by about twelve percent",
            "most of the overrun is cloud spend on staging",
            "maria presented the axolotl conservation partnership",
            "the partnership gives a co-marketing slot in march",
            "legal has signed off on the partnership already",
            "the app store rejection was about the tracking prompt",
            "we resubmitted on tuesday and it went through",
            "the fire drill on wednesday took the afternoon",
            "nobody told the building manager about the demo",
            "priya owns the cloud cleanup and wants a week",
            "we agreed to meet again on friday about the date",
            "the hiring plan is paused until the next quarter",
            "two candidates withdrew after the second interview",
            "the office move is still scheduled for november",
            "parking will be the main complaint about that",
            "the customer survey came back better than expected",
            "support tickets are down for the third month",
            "the roadmap slide needs redoing before the board",
            "that is everything for today thanks everyone",
        ];

        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < lines.Length; i++)
        {
            segments.Add(new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(i * 60),
                End = TimeSpan.FromSeconds((i * 60) + 60),
                Text = lines[i],
            });
        }

        var document = new TranscriptDocument
        {
            Segments = segments,
            AudioDuration = TimeSpan.FromSeconds(lines.Length * 60),
        };

        return new Bm25Retriever(TranscriptWindowBuilder.Build(document, TranscriptWindowOptions.Cover));
    }

    [Theory]
    [InlineData("give me a summary")]
    [InlineData("Summarise this")]
    [InlineData("summarize the recording please")]
    [InlineData("what are the main topics?")]
    [InlineData("can I get an overview")]
    [InlineData("what's the gist")]
    [InlineData("tl;dr")]
    [InlineData("key takeaways")]
    [InlineData("what did they discuss")]
    [InlineData("what is this about")]
    [InlineData("give me a recap of the whole thing")]
    public void AQuestionAboutTheWholeRecordingGoesWhole(string question)
    {
        var decision = QuestionRouter.Route(question, Index(), wholeTranscriptIsAffordable: true);

        Assert.Equal(AnswerMode.WholeTranscript, decision.Mode);
        Assert.Null(decision.Notice);
    }

    [Theory]
    [InlineData("when did they mention money?")]
    [InlineData("what did maria present")]
    [InlineData("did they talk about the fire drill")]
    [InlineData("who owns the cloud cleanup")]
    [InlineData("what happened with the app store rejection")]
    [InlineData("how much did the budget go over")]
    public void APointedQuestionGoesToRetrieval(string question)
    {
        var decision = QuestionRouter.Route(question, Index(), wholeTranscriptIsAffordable: true);

        Assert.Equal(AnswerMode.Retrieval, decision.Mode);
        Assert.Equal(RoutingBasis.Findable, decision.Basis);
        Assert.Null(decision.Notice);
    }

    [Fact]
    public void NamingSomethingTheRecordingNeverMentionsStaysPointed()
    {
        // The case this session met for real: "did they mention Reggie?" against a transcript
        // with no Reggie in it. An absent term is not a ubiquitous one — the asker named
        // something specific, and retrieval's abstention is the honest, cheap answer. Reading
        // three hours to conclude the same thing is the expensive way to be right.
        var decision = QuestionRouter.Route(
            "did they mention reggie?", Index(), wholeTranscriptIsAffordable: true);

        Assert.Equal(AnswerMode.Retrieval, decision.Mode);
        Assert.Equal(RoutingBasis.Findable, decision.Basis);
    }

    [Fact]
    public void AQuestionOfNothingButUbiquitousWordsHasNothingToRankOn()
    {
        // The rule that needs no vocabulary: every term is present in at least half the windows,
        // so BM25's ordering over them is arbitrary and the top eight are eight arbitrary
        // minutes. That is the mechanical statement of a global question, and it fires without
        // knowing a word of English.
        var decision = QuestionRouter.Route("the the the", Index(), wholeTranscriptIsAffordable: true);

        Assert.Equal(AnswerMode.WholeTranscript, decision.Mode);
        Assert.Equal(RoutingBasis.NothingToRankOn, decision.Basis);
    }

    [Fact]
    public void ALongRecordingIsNotReadWholeWithoutBeingAsked()
    {
        // The asymmetry the router leans on: a wrong pointed answer costs seconds and is
        // obviously thin, a wrong whole-recording pass costs a prefill measured in minutes. So
        // the automatic path refuses to start one, and says so rather than answering thinly in
        // silence — otherwise the answer reads as the recording being thin.
        var decision = QuestionRouter.Route(
            "give me a summary", Index(), wholeTranscriptIsAffordable: false);

        Assert.Equal(AnswerMode.Retrieval, decision.Mode);
        Assert.Equal(RoutingBasis.GlobalButTooLong, decision.Basis);
        Assert.NotNull(decision.Notice);
        Assert.Contains("whole recording", decision.Notice, StringComparison.Ordinal);

        // A pointed question is unaffected by the ceiling — retrieval was where it was going.
        var pointed = QuestionRouter.Route(
            "what did maria present", Index(), wholeTranscriptIsAffordable: false);
        Assert.Equal(RoutingBasis.Findable, pointed.Basis);
        Assert.Null(pointed.Notice);
    }

    [Fact]
    public void TheCueMatchIsWholeWordsAndSurvivesCaseAndPunctuation()
    {
        // Normalised through the same tokenizer retrieval uses, so case, accents and
        // punctuation are handled once rather than three ways.
        Assert.True(QuestionRouter.AsksForTheWholeRecording("TL;DR?"));
        Assert.True(QuestionRouter.AsksForTheWholeRecording("Overall, how did it go?"));

        // A stem matches the start of a word, not its middle: "consummate" is not "summary".
        Assert.False(QuestionRouter.AsksForTheWholeRecording("was the deal consummated"));

        // And a phrase needs its own words, not a substring of a longer one.
        Assert.False(QuestionRouter.AsksForTheWholeRecording("who is the main topicality expert"));
        Assert.False(QuestionRouter.AsksForTheWholeRecording(string.Empty));
    }

    [Fact]
    public void AnEmptyIndexNeverClaimsUbiquity()
    {
        // No windows means no evidence either way, and "every term is ubiquitous" over an empty
        // index is vacuously true — which would route every question on an empty transcript to
        // a whole-transcript pass over nothing.
        var empty = new Bm25Retriever([]);
        Assert.False(empty.EveryTermIsUbiquitous("anything at all"));

        var decision = QuestionRouter.Route("anything at all", empty, wholeTranscriptIsAffordable: true);
        Assert.Equal(AnswerMode.Retrieval, decision.Mode);
    }
}
