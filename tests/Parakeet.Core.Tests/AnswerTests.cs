using Parakeet.Core.Answers;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class CitationParseTests
{
    [Fact]
    public void PointsAndRangesParse()
    {
        var point = Citation.Parse("S12");
        Assert.Equal(12, point.StartSegment);
        Assert.Equal(12, point.EndSegment);

        var range = Citation.Parse("S12-S15");
        Assert.Equal(12, range.StartSegment);
        Assert.Equal(15, range.EndSegment);
    }

    [Fact]
    public void TheUncitedMarkerIsItsOwnThing()
    {
        var uncited = Citation.Parse("?");
        Assert.True(uncited.IsUncitedMarker);
        Assert.False(uncited.IsWellFormed);
    }

    [Theory]
    [InlineData("S")]
    [InlineData("12")]
    [InlineData("S12-")]
    [InlineData("S12-S")]
    [InlineData("Sx")]
    [InlineData("S 12")]
    [InlineData("S-1")]
    public void NonsenseKeepsItsRawFormAndIsNotWellFormed(string raw)
    {
        var citation = Citation.Parse(raw);
        Assert.False(citation.IsWellFormed);
        Assert.Equal(raw, citation.Raw);
    }

    [Fact]
    public void ABackwardsRangeParsesAndFailsLater()
    {
        // The grammar cannot forbid S15-S12; parsing keeps it so the validator can fail it
        // with the evidence intact.
        var backwards = Citation.Parse("S15-S12");
        Assert.True(backwards.IsWellFormed);
        Assert.Equal(15, backwards.StartSegment);
        Assert.Equal(12, backwards.EndSegment);
    }
}

public class AnswerParserTests
{
    [Fact]
    public void AGrammarShapedAnswerParsesWhole()
    {
        var answer = AnswerParser.Parse(
            "- Weather: they opened with the forecast [S2-S5]\n" +
            "- The zoo came up in passing [S104]\n" +
            "- Budget worries, mentioned twice [S12, S40-S41]\n" +
            "- Something nobody could anchor [?]\n");

        Assert.False(answer.Abstained);
        Assert.Equal(4, answer.Bullets.Count);

        Assert.Equal("Weather", answer.Bullets[0].Label);
        Assert.Equal("they opened with the forecast", answer.Bullets[0].Text);
        Assert.Equal("S2-S5", Assert.Single(answer.Bullets[0].Citations).Raw);

        Assert.Null(answer.Bullets[1].Label);
        Assert.Equal(2, answer.Bullets[2].Citations.Count);
        Assert.True(answer.Bullets[3].IsUncited);
    }

    [Fact]
    public void TheAbstainSentinelAbstains()
    {
        var answer = AnswerParser.Parse("NOT_IN_TRANSCRIPT\n");

        Assert.True(answer.Abstained);
        Assert.Empty(answer.Bullets);
        Assert.False(answer.IsEmpty);
    }

    [Fact]
    public void TheLeadIsTakenOnlyWhereItWasAskedForAndOnlyBeforeTheClaims()
    {
        const string overview = "This recording covers the budget [S1]\n- Budget: it ran long [S2]\n";

        // Asked for: the framing sentence is the lead, and it keeps its own citations.
        var asked = AnswerParser.Parse(overview, allowLead: true);
        Assert.NotNull(asked.Lead);
        Assert.Equal("This recording covers the budget", asked.Lead!.Text);
        Assert.Equal("S1", Assert.Single(asked.Lead.Citations).Raw);
        Assert.Equal("Budget", Assert.Single(asked.Bullets).Label);

        // Not asked for: the same text is claims, and the unmarked one is a claim like any
        // other — dropping model output silently would hide what the validator exists to catch.
        var unasked = AnswerParser.Parse(overview);
        Assert.Null(unasked.Lead);
        Assert.Equal(2, unasked.Bullets.Count);

        // Only before the claims: once a bullet has been seen, a later unmarked line is a claim
        // the model forgot to mark, not a second framing sentence.
        var late = AnswerParser.Parse("- Budget: it ran long [S2]\nand another thing [S3]\n", allowLead: true);
        Assert.Null(late.Lead);
        Assert.Equal(2, late.Bullets.Count);

        // And only the first: three paragraphs of preamble are claims, and marked as claims.
        var wordy = AnswerParser.Parse("First framing [S1]\nSecond paragraph [S2]\n- A claim [S3]\n", allowLead: true);
        Assert.Equal("First framing", wordy.Lead!.Text);
        Assert.Equal(2, wordy.Bullets.Count);
    }

    [Fact]
    public void ACitationLiftedFromMidSentenceDoesNotLeaveASpaceBeforeThePunctuation()
    {
        // The overview path invites citations mid-sentence ("cite every part where a point is
        // discussed"), and lifting one out used to leave the space that was in front of it:
        // "…the staging environment [S1-S4]." rendered as "…the staging environment .".
        var bullet = AnswerParser.Parse("- Budget: it went over [S1-S4].\n").Bullets[0];
        Assert.Equal("it went over.", bullet.Text);

        var commas = AnswerParser.Parse("- Two things [S1], and a third [S2].\n").Bullets[0];
        Assert.Equal("Two things, and a third.", commas.Text);

        // French typography puts a space before ; : ! ? and the twenty-five languages are the
        // requirement, so those keep theirs.
        var french = AnswerParser.Parse("- Vraiment [S1] ?\n").Bullets[0];
        Assert.Equal("Vraiment ?", french.Text);
    }

    [Fact]
    public void ALeadAloneIsAnAnswerAndDoesNotAbstain()
    {
        // A lead is content: an answer that is only a framing sentence is thin, but rendering
        // it as "the model produced no answer" would throw away what it did say — and a
        // sentinel beside it is the same contradiction a sentinel beside bullets is.
        var leadOnly = AnswerParser.Parse("This recording covers the budget [S1]\n", allowLead: true);
        Assert.False(leadOnly.IsEmpty);
        Assert.NotNull(leadOnly.Lead);

        var contradictory = AnswerParser.Parse(
            "This recording covers the budget [S1]\nNOT_IN_TRANSCRIPT\n", allowLead: true);
        Assert.False(contradictory.Abstained);
        Assert.NotNull(contradictory.Lead);
    }

    [Fact]
    public void ALeadingThinkTagIsStrippedNotParsed()
    {
        // A template that forces its think block open leaves a literal `<think>` at the front
        // of the stream under `--reasoning-format none` (measured 2026-08-16); unstripped it
        // parses as a junk bullet and defeats the abstain match. Only the leading tag is
        // stripped — one deeper in the text is model output the validator should see.
        var abstained = AnswerParser.Parse("<think>\nNOT_IN_TRANSCRIPT\n");
        Assert.True(abstained.Abstained);
        Assert.Empty(abstained.Bullets);

        var answered = AnswerParser.Parse("<think>\n- a claim [S1]\n");
        Assert.Single(answered.Bullets);

        var inline = AnswerParser.Parse("- the tag <think> mid-text [S1]\n");
        Assert.Contains("<think>", inline.Bullets[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyOutputIsEmptyNotAbstained()
    {
        // The model producing nothing and the model saying "not in the recording" are different
        // outcomes; conflating them would render silence as an honest abstention.
        var answer = AnswerParser.Parse("   \n\n");

        Assert.True(answer.IsEmpty);
        Assert.False(answer.Abstained);
    }

    [Fact]
    public void ASentinelBesideBulletsDoesNotAbstain()
    {
        // "The recording doesn't answer that." above a list of answers is a contradiction no
        // renderer should repeat: the bullets are the checkable half, so they stand.
        var answer = AnswerParser.Parse("NOT_IN_TRANSCRIPT\n- but also this claim [S1]\n");

        Assert.False(answer.Abstained);
        Assert.Single(answer.Bullets);
    }

    [Theory]
    [InlineData("NOT_IN_TRANSCRIPT.")]
    [InlineData("**NOT_IN_TRANSCRIPT**")]
    [InlineData("- NOT_IN_TRANSCRIPT")]
    [InlineData("_NOT_IN_TRANSCRIPT_")]
    [InlineData("**NOT_IN_TRANSCRIPT**.")]
    [InlineData("- **NOT_IN_TRANSCRIPT**.")]
    [InlineData("NOT_IN_TRANSCRIPT [?]")]
    public void ANearMissSentinelLineAbstainsInsteadOfRenderingTheRawToken(string line)
    {
        // The post-hoc path's dressing — punctuation, bold, a bullet marker, an admitted-uncited
        // [?] — must not turn the internal token into a rendered claim. The stacked shapes are
        // the regression: bold-then-period defeated a single-pass trim, and [?] defeated a check
        // that ran before citation extraction, so both rendered the raw token as the answer's
        // framing sentence (found 2026-08-30).
        var answer = AnswerParser.Parse(line + "\n", allowLead: true);

        Assert.True(answer.Abstained);
        Assert.Empty(answer.Bullets);
        Assert.Null(answer.Lead);
    }

    [Fact]
    public void TheSentinelInsideProseStaysInert()
    {
        var answer = AnswerParser.Parse("- NOT_IN_TRANSCRIPT is what it replies when unsure [S1]\n");

        Assert.False(answer.Abstained);
        Assert.Single(answer.Bullets);
    }

    [Fact]
    public void TheSentinelBesideARealCitationStandsAsTheBulletItClaimsToBe()
    {
        // An abstention citing a segment is a contradiction, and the citation is the checkable
        // half: the line stays a bullet so the validator gets to see it, exactly as a sentinel
        // beside other bullets already loses to them.
        var answer = AnswerParser.Parse("NOT_IN_TRANSCRIPT [S3]\n");

        Assert.False(answer.Abstained);
        Assert.Single(answer.Bullets);
    }

    [Fact]
    public void ASecondGuillemetPairIsReMarkedAsPlainQuotes()
    {
        // Guillemets are the answer's reserved quote marks; only the extracted quote may ever
        // render wearing them, or a second pair reads as a verified quote it is not.
        var answer = AnswerParser.Parse("- One «real quote» and another «impostor» beside it [S1]\n");

        var bullet = Assert.Single(answer.Bullets);
        Assert.Equal("real quote", bullet.Quote);
        Assert.DoesNotContain('«', bullet.Text);
        Assert.DoesNotContain('»', bullet.Text);
        Assert.Contains("“impostor”", bullet.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteIsExtractedAndTheTextKeepsReading()
    {
        // Read for the validator, and left where the model put it. Removing it was right while
        // the grammar put every quote last; ungrammared a model writes it into the sentence, and
        // cutting it out left holes — "The budget was allegedly." was a real bullet whose
        // subject this parser had deleted (observed 2026-08-25).
        var trailing = AnswerParser.Parse("- They were sure about it «we checked the numbers twice» [S7]\n");

        var bullet = Assert.Single(trailing.Bullets);
        Assert.Equal("we checked the numbers twice", bullet.Quote);
        Assert.Equal("They were sure about it “we checked the numbers twice”", bullet.Text);

        // The shape that was being destroyed: a quote carrying the sentence's own subject.
        var inline = AnswerParser.Parse("- The budget was allegedly «two hundred in outsourcing» [S7]\n");
        Assert.Equal("two hundred in outsourcing", inline.Bullets[0].Quote);
        Assert.Equal("The budget was allegedly “two hundred in outsourcing”", inline.Bullets[0].Text);

        // And mid-sentence, which is where an ungrammared model most often puts it.
        var middle = AnswerParser.Parse("- There was an «initial two hundred» that was not shaping up [S7]\n");
        Assert.Equal("initial two hundred", middle.Bullets[0].Quote);
        Assert.Equal("There was an “initial two hundred” that was not shaping up", middle.Bullets[0].Text);
    }

    [Fact]
    public void ACitationRemovedFromBesidePunctuationDoesNotLeaveItsDebris()
    {
        // Our own damage, not the model's: a citation between a comma and a full stop leaves
        // ",.", and one that ends the sentence leaves the comma before it dangling.
        Assert.Equal(
            "The speaker mentions the PS2.",
            AnswerParser.Parse("- The speaker mentions the PS2, [S1].\n").Bullets[0].Text);

        Assert.Equal(
            "It was noted",
            AnswerParser.Parse("- It was noted, [S1]\n").Bullets[0].Text);

        // A semicolon before a stop goes the same way, and a legitimate comma stays put.
        Assert.Equal(
            "One thing, then another.",
            AnswerParser.Parse("- One thing, then another [S1].\n").Bullets[0].Text);

        // A separated citation list leaves a run of commas by the same mechanism — this was a
        // real lead reading "…and the concept of refunds,." (2026-08-25).
        Assert.Equal(
            "Costs and the concept of refunds.",
            AnswerParser.Parse("- Costs and the concept of refunds, [S1], [S2], [S3].\n").Bullets[0].Text);
    }

    [Fact]
    public void TheModelsOwnSpacingBeforePunctuationIsNotOurDebris()
    {
        // The debris repair used to run everywhere, not just where a citation was lifted, and
        // ate the space in front of any word that starts with a period — "as .csv" rendered
        // "as.csv", and "Yes, .NET" lost its comma too (found 2026-08-30). Only the hole a
        // citation actually left is closed.
        Assert.Equal(
            "They export the results as .csv files",
            AnswerParser.Parse("- They export the results as .csv files [S3]\n").Bullets[0].Text);

        Assert.Equal(
            "Yes, .NET was chosen",
            AnswerParser.Parse("- Yes, .NET was chosen [S2]\n").Bullets[0].Text);

        // And a bullet the model itself ended on a comma keeps it — only a lifted citation
        // leaves a dangling one.
        Assert.Equal(
            "It trailed off with a comma,",
            AnswerParser.Parse("- It trailed off with a comma,\n").Bullets[0].Text);
    }

    [Fact]
    public void ACitationSittingFlushInTheSentenceLeavesNoSpaceBehind()
    {
        // A citation with no whitespace around it left no hole to close, and the first cut of
        // the hole marker treated it as a space anyway — "It worked !" for "It worked[S1]!"
        // (found 2026-08-30). The lift removes the citation and nothing else.
        Assert.Equal(
            "It worked!",
            AnswerParser.Parse("- It worked[S1]!\n").Bullets[0].Text);

        Assert.Equal(
            "Adoption hit 60%!",
            AnswerParser.Parse("- Adoption hit 60%[S2]!\n").Bullets[0].Text);

        Assert.Equal(
            "The budget doubled ().",
            AnswerParser.Parse("- The budget doubled ([S1]).\n").Bullets[0].Text);
    }

    [Fact]
    public void ProseBracketsAreNotCitations()
    {
        // The transcript's own conventions reach answers: [inaudible], [laughs]. Only
        // structured citations may ever render as anything clickable.
        var answer = AnswerParser.Parse("- Someone [laughs] mentioned the deadline [S9]\n");

        var bullet = Assert.Single(answer.Bullets);
        Assert.Equal("Someone [laughs] mentioned the deadline", bullet.Text);
        Assert.Equal("S9", Assert.Single(bullet.Citations).Raw);
    }

    [Fact]
    public void ALineWithoutABulletMarkerStillBecomesABullet()
    {
        // The post-hoc path: no grammar was enforced, and dropping model output silently would
        // hide exactly what the validator exists to catch.
        var answer = AnswerParser.Parse("The whole answer as one paragraph [S3-S4]");

        var bullet = Assert.Single(answer.Bullets);
        Assert.Equal("The whole answer as one paragraph", bullet.Text);
        Assert.Single(bullet.Citations);
    }

    [Fact]
    public void CrlfAndCrIsToleratedInTheModelOutput()
    {
        var answer = AnswerParser.Parse("- first claim [S1]\r\n- second claim [S2]\r\n");

        Assert.Equal(2, answer.Bullets.Count);
        Assert.Equal("first claim", answer.Bullets[0].Text);
    }

    [Fact]
    public void ALongColonSentenceIsNotMistakenForALabel()
    {
        var text = "What they said about the budget over the following forty minutes was this: nothing";
        var answer = AnswerParser.Parse($"- {text} [S8]\n");

        var bullet = Assert.Single(answer.Bullets);
        Assert.Null(bullet.Label);
        Assert.Equal(text, bullet.Text);
    }

    [Fact]
    public void AColonInsideTheQuoteIsTheTranscriptTalkingNotALabel()
    {
        // The quote is speech and says what it likes. Its own ": " used to be read as the label
        // separator, splitting the quote across the label chip and the claim — label
        // "He said “wait" beside text "no”" (found 2026-08-30).
        var spoken = AnswerParser.Parse("- He said «wait: no» [S1]\n");
        var bullet = Assert.Single(spoken.Bullets);
        Assert.Null(bullet.Label);
        Assert.Equal("wait: no", bullet.Quote);
        Assert.Equal("He said “wait: no”", bullet.Text);

        // Opening the bullet on the quote is the same shape harder.
        var opening = AnswerParser.Parse("- «Grade: A» is how they put it [S2]\n");
        Assert.Null(opening.Bullets[0].Label);
        Assert.Equal("Grade: A", opening.Bullets[0].Quote);

        // A label in front of a colon-carrying quote is still a label.
        var labelled = AnswerParser.Parse("- Verdict: he said «wait: no» [S1]\n");
        Assert.Equal("Verdict", labelled.Bullets[0].Label);
        Assert.Equal("wait: no", labelled.Bullets[0].Quote);
    }

    [Fact]
    public void AStarOrNumberMarkedListIsAListNotALeadAndAClaim()
    {
        // The ungrammared decode writes whatever markdown habit the model has. "1." and "* "
        // used to fail the marker test, so the first claim was promoted to the framing sentence
        // and every later claim kept its literal number (found 2026-08-30).
        var numbered = AnswerParser.Parse("1. The budget ran over [S2]\n2. Hiring was frozen [S5]\n", allowLead: true);
        Assert.Null(numbered.Lead);
        Assert.Equal(2, numbered.Bullets.Count);
        Assert.Equal("The budget ran over", numbered.Bullets[0].Text);
        Assert.Equal("Hiring was frozen", numbered.Bullets[1].Text);

        var starred = AnswerParser.Parse("* One thing [S1]\n* Another [S2]\n", allowLead: true);
        Assert.Null(starred.Lead);
        Assert.Equal(2, starred.Bullets.Count);
        Assert.Equal("One thing", starred.Bullets[0].Text);

        // A framing sentence that opens with a year is a lead, not the two-thousand-and-
        // twenty-fourth claim.
        var year = AnswerParser.Parse("2024. That is the year under discussion [S1]\n- One claim [S2]\n", allowLead: true);
        Assert.NotNull(year.Lead);
        Assert.Equal("2024. That is the year under discussion", year.Lead!.Text);
    }
}

/// <summary>Decision 5's transcript pin: the segmentation's identity, shared with the lab.</summary>
public class TranscriptPinTests
{
    [Fact]
    public void ThePinMatchesTheLabScriptsHashOnASharedVector()
    {
        // The expected value was computed by scripts/measure-answers.ps1's own StringBuilder
        // loop over these two segments — the script and SegmentsSha256() are two
        // implementations of one algorithm, and this vector is what holds them together. A
        // question set pins a transcript by this hash, so an implementation drift would
        // silently unpin every labelled set.
        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the meeting opened" },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(10), End = TimeSpan.FromSeconds(20.5), Text = "maria presented" },
            ],
        };

        Assert.Equal(
            "978febcd5f8a613d1607c0c91f19f1f66e24d808c01bd6e4b8971d2e1bcbfbf1",
            document.SegmentsSha256());
    }

    [Fact]
    public void ThePinHashesTheExportsRoundedTimesNotTheTickExactOnes()
    {
        // The script hashes the exported JSON, whose times the writer rounds to three decimals
        // — and the last segment of nearly every real recording ends off the millisecond grid,
        // because the flush trims to the file's actual final sample. Hashed tick-exact, this
        // segment rendered "27.716062" here and "27.716" in the lab, one differing line changing
        // the whole SHA-256 and the wrong-transcript check firing on a matching pair (found
        // 2026-08-30). The expected value is the script's own loop over the rounded rendering.
        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(27.7160625), Text = "then they adjourned" },
            ],
        };

        Assert.Equal(
            "e09d7399efd676c31f4071821fb83245c66da8569219f4a8fa09bc8a53b147ff",
            document.SegmentsSha256());
    }

    [Fact]
    public void ADifferentSegmentationIsADifferentPin()
    {
        // The failure the pin exists to catch: same audio, another model, every id pointing at
        // different words while looking perfectly fine.
        var one = new TranscriptDocument
        {
            Segments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the meeting opened" }],
        };
        var other = new TranscriptDocument
        {
            Segments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(9), Text = "the meeting opened" }],
        };

        Assert.NotEqual(one.SegmentsSha256(), other.SegmentsSha256());
    }
}

public class CitationValidatorTests
{
    private static TranscriptDocument Transcript(params string[] texts)
    {
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < texts.Length; i++)
        {
            segments.Add(new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(i * 10),
                End = TimeSpan.FromSeconds((i * 10) + 10),
                Text = texts[i],
            });
        }

        return new TranscriptDocument
        {
            Segments = segments,
            AudioDuration = TimeSpan.FromSeconds(texts.Length * 10),
        };
    }

    [Fact]
    public void AResolvingCitationCarriesTheSegmentsOwnTimes()
    {
        var transcript = Transcript("first", "second", "third");
        var resolved = CitationValidator.Resolve(Citation.Parse("S2-S3"), transcript);

        Assert.True(resolved.Check.Passes);
        Assert.Equal(TimeSpan.FromSeconds(10), resolved.Start);
        Assert.Equal(TimeSpan.FromSeconds(30), resolved.End);
    }

    [Theory]
    [InlineData("S0")]
    [InlineData("S4")]
    [InlineData("S1-S4")]
    [InlineData("S3-S2")]
    [InlineData("banana")]
    public void WhatCannotResolveResolvesToNothing(string raw)
    {
        var transcript = Transcript("first", "second", "third");
        var resolved = CitationValidator.Resolve(Citation.Parse(raw), transcript);

        Assert.False(resolved.Check.Resolves);
        Assert.False(resolved.Check.Passes);

        // Never a time: an unresolved citation rendered with a timestamp is exactly the fluent
        // wrong number the whole design exists to prevent.
        Assert.Null(resolved.Start);
        Assert.Null(resolved.End);
    }

    [Fact]
    public void ACitationOfSilenceFailsNonEmpty()
    {
        var transcript = Transcript("something", "", "something else");
        var resolved = CitationValidator.Resolve(Citation.Parse("S2"), transcript);

        Assert.True(resolved.Check.Resolves);
        Assert.False(resolved.Check.NonEmpty);
        Assert.False(resolved.Check.Passes);
    }

    [Fact]
    public void ASpanPastTheRecordingsEndFailsWithinDuration()
    {
        var transcript = Transcript("fine", "fine") with { AudioDuration = TimeSpan.FromSeconds(15) };
        var resolved = CitationValidator.Resolve(Citation.Parse("S2"), transcript);

        Assert.True(resolved.Check.Resolves);
        Assert.False(resolved.Check.WithinDuration);
    }

    [Fact]
    public void AnUnknownDurationChecksNothing()
    {
        var transcript = Transcript("fine") with { AudioDuration = null };

        Assert.True(CitationValidator.Resolve(Citation.Parse("S1"), transcript).Check.WithinDuration);
    }

    [Fact]
    public void TheQuoteCheckIsNormalisedAndBoundaryAware()
    {
        var transcript = Transcript("Wir haben's gesehen, und zwar zweimal.");

        Assert.True(CitationValidator.Resolve(Citation.Parse("S1"), transcript, "wir haben s gesehen").Check.QuoteMatches);
        Assert.False(CitationValidator.Resolve(Citation.Parse("S1"), transcript, "etwas anderes").Check.QuoteMatches);

        // Token boundaries: "art" is not inside "start".
        Assert.False(CitationValidator.ContainsNormalized("we start now", "art"));
        Assert.True(CitationValidator.ContainsNormalized("state of the art now", "art"));
    }

    [Fact]
    public void AQuoteAgainstAnUnresolvedCitationWasNeverCheckedAndNoQuoteChecksNothing()
    {
        // An unresolved citation names no span, so the quote was never checked. False here is
        // reserved for checked-and-failed: a [?]-only bullet whose quote reported false used to
        // render "quote not found in the transcript" — an accusation of a check that never ran.
        var transcript = Transcript("something");

        Assert.Null(CitationValidator.Resolve(Citation.Parse("S9"), transcript, "something").Check.QuoteMatches);
        Assert.Null(CitationValidator.Resolve(Citation.Parse("?"), transcript, "something").Check.QuoteMatches);
        Assert.Null(CitationValidator.Resolve(Citation.Parse("S1"), transcript).Check.QuoteMatches);
        Assert.False(CitationValidator.Resolve(Citation.Parse("S1"), transcript, "not these words").Check.QuoteMatches);
    }

    [Fact]
    public void ValidationWalksEveryBulletAndTheUncitedMarkerDoesNotFailTheAnswer()
    {
        var transcript = Transcript("first", "second", "third");
        var answer = AnswerParser.Parse(
            "- solid claim [S1]\n" +
            "- honest admission [?]\n");

        var validation = CitationValidator.Validate(answer, transcript);

        Assert.True(validation.AllCitationsPass);
        Assert.Null(validation.Monotone);
    }

    [Fact]
    public void OneBadCitationFailsTheAnswer()
    {
        var transcript = Transcript("first", "second");
        var answer = AnswerParser.Parse("- fine [S1]\n- invented [S17]\n");

        Assert.False(CitationValidator.Validate(answer, transcript).AllCitationsPass);
    }

    [Fact]
    public void TheLeadIsCheckedLikeAnyOtherClaim()
    {
        // The lead makes claims about the recording exactly as a bullet does. An unchecked lead
        // would be the one paragraph in the panel a reader could not verify — so an invented id
        // in it fails the whole answer, and a resolved one carries real times like the rest.
        var transcript = Transcript("first", "second");

        var good = AnswerParser.Parse("This recording covers two things [S1-S2]\n- fine [S1]\n", allowLead: true);
        var goodValidation = CitationValidator.Validate(good, transcript);
        Assert.True(goodValidation.AllCitationsPass);
        Assert.NotNull(goodValidation.Lead);
        Assert.True(goodValidation.Lead!.Citations[0].Check.Resolves);

        var invented = AnswerParser.Parse("This recording covers two things [S17]\n- fine [S1]\n", allowLead: true);
        Assert.False(CitationValidator.Validate(invented, transcript).AllCitationsPass);

        // No lead, no resolved lead — and nothing about the bullets changes.
        var plain = CitationValidator.Validate(AnswerParser.Parse("- fine [S1]\n"), transcript);
        Assert.Null(plain.Lead);
        Assert.True(plain.AllCitationsPass);
    }

    [Fact]
    public void AMultiCiteBulletNeedsItsQuoteInOneSpanNotEveryOne()
    {
        // The grammar invites up to five citations on a bullet; one quote cannot sit inside
        // every one of their spans at once, so an honest multi-cite bullet is judged per bullet.
        // Each citation's own QuoteMatches stays the per-span truth a tooltip can state.
        var transcript = Transcript("the axolotl was discussed at length", "entirely other business");
        var found = AnswerParser.Parse("- Both moments matter «axolotl was discussed» [S1, S2]\n");
        var validation = CitationValidator.Validate(found, transcript);

        Assert.True(validation.AllCitationsPass);
        Assert.True(validation.Bullets[0].QuoteFound);
        Assert.Equal(
            [true, false],
            validation.Bullets[0].Citations.Select(c => c.Check.QuoteMatches).ToArray());

        var nowhere = AnswerParser.Parse("- Both moments matter «never spoken words» [S1, S2]\n");
        Assert.False(CitationValidator.Validate(nowhere, transcript).AllCitationsPass);
    }

    [Fact]
    public void ChronologyIsCheckedOnlyWhenClaimed()
    {
        var transcript = Transcript("a", "b", "c", "d", "e", "f");
        var forward = AnswerParser.Parse("- one [S1-S2]\n- two [S4]\n- three [S5-S6]\n");
        var backward = AnswerParser.Parse("- one [S4]\n- two [S1-S2]\n");
        var overlapping = AnswerParser.Parse("- one [S1-S3]\n- two [S3-S4]\n");

        Assert.True(CitationValidator.Validate(forward, transcript, expectChronological: true).Monotone);
        Assert.False(CitationValidator.Validate(backward, transcript, expectChronological: true).Monotone);
        Assert.False(CitationValidator.Validate(overlapping, transcript, expectChronological: true).Monotone);
        Assert.Null(CitationValidator.Validate(backward, transcript).Monotone);
    }

    [Fact]
    public void ARetrievedWindowIsCitableByConstruction()
    {
        // The property the whole tier-0 design rests on: the citation is the window retrieval
        // handed back, so resolving it must land on the window's own times.
        var document = Transcript(
            "we talked about the weather",
            "the axolotl regenerated its password",
            "closing remarks and thanks");

        var windows = TranscriptWindowBuilder.Build(
            document, new TranscriptWindowOptions { WindowLength = TimeSpan.FromSeconds(10), Stride = TimeSpan.FromSeconds(10) });
        var hit = new Bm25Retriever(windows).Retrieve("axolotl password", 1)[0];

        var citation = Citation.Parse(hit.Window.CitationId);
        var resolved = CitationValidator.Resolve(citation, document);

        Assert.True(resolved.Check.Passes);
        Assert.Equal(hit.Window.Start, resolved.Start);
        Assert.Equal(hit.Window.End, resolved.End);
    }
}
