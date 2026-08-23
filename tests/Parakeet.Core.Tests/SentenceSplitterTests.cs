using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class SentenceSplitterTests
{
    [Fact]
    public void ASegmentIsCutAfterEachSentenceAndThePiecesKeepTheSegmentsOuterTimes()
    {
        // The shape found on a broadcast documentary on 2026-08-23: one VAD segment, several
        // sentences, and the model's own word timings showing the pauses the energy gate never saw.
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.FromSeconds(8.52),
            End = TimeSpan.FromSeconds(37.95),
            Text = "Damit es einfach geiler aussieht. Moin Moin. Eine Langkolle bitte.",
            Words =
            [
                Word("Damit", 8.6, 8.8), Word("es", 8.8, 8.9), Word("einfach", 9.0, 9.4), Word("geiler", 9.4, 9.8), Word("aussieht.", 9.9, 10.36),
                Word("Moin", 11.3, 11.5), Word("Moin.", 11.6, 11.9),
                Word("Eine", 12.9, 13.1), Word("Langkolle", 13.1, 13.6), Word("bitte.", 13.7, 14.1),
            ],
            SourceSegmentIndex = 1,
            Speaker = "Speaker 2",
        };

        var pieces = SentenceSplitter.Split(segment);

        Assert.Equal(["Damit es einfach geiler aussieht.", "Moin Moin.", "Eine Langkolle bitte."], pieces.Select(p => p.Text));

        Assert.Equal(segment.Start, pieces[0].Start);               // the first piece keeps the segment's start
        Assert.Equal(TimeSpan.FromSeconds(10.36), pieces[0].End);   // and ends with its last word
        Assert.Equal(TimeSpan.FromSeconds(11.3), pieces[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(11.9), pieces[1].End);
        Assert.Equal(TimeSpan.FromSeconds(12.9), pieces[2].Start);
        Assert.Equal(segment.End, pieces[2].End);                   // the last piece keeps the segment's end

        // The words go with their sentence, and nothing about provenance moves.
        Assert.Equal([5, 2, 3], pieces.Select(p => p.Words.Count));
        Assert.All(pieces, p => Assert.Equal(1, p.SourceSegmentIndex));
        Assert.All(pieces, p => Assert.Equal("Speaker 2", p.Speaker));
    }

    [Fact]
    public void ASegmentWithoutWordTimingsIsLeftWholeHoweverManySentencesItHolds()
    {
        // Every translated segment: text, times, no words. There is nothing to time a cut by, and
        // a time that is not the engine's is not written.
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(12),
            Text = "First one. Then two. And three.",
        };

        var pieces = SentenceSplitter.Split(segment);

        Assert.Single(pieces);
        Assert.Same(segment, pieces[0]);
    }

    [Fact]
    public void ASegmentWhoseWordsDoNotSpellItsTextIsLeftWhole()
    {
        // The same gate SpeakerAssignment applies before it cuts on a speaker change: a mismatch
        // between the words and the text is a bookkeeping fault, and a text cut at a guessed
        // position would look exactly like a transcript.
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(4),
            Text = "One here. Two there.",
            Words = [Word("One", 0, 1), Word("here.", 1, 2), Word("Two", 2, 3), Word("over", 3, 3.5), Word("there.", 3.5, 4)],
        };

        var pieces = SentenceSplitter.Split(segment);

        Assert.Single(pieces);
        Assert.Same(segment, pieces[0]);
    }

    [Fact]
    public void ASingleSentenceSegmentComesBackAsItself()
    {
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "Die Erbstegressel.",
            Words = [Word("Die", 0, 1), Word("Erbstegressel.", 1, 2)],
        };

        Assert.Same(segment, Assert.Single(SentenceSplitter.Split(segment)));
    }

    [Fact]
    public void ASegmentThatEndsMidSentenceKeepsTheTailAsItsLastPiece()
    {
        // What the segment cap leaves: a cut at the quietest frame, through a sentence.
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(30),
            Text = "Eins. Zwei drei",
            Words = [Word("Eins.", 1, 2), Word("Zwei", 3, 4), Word("drei", 4, 5)],
        };

        var pieces = SentenceSplitter.Split(segment);

        Assert.Equal(["Eins.", "Zwei drei"], pieces.Select(p => p.Text));
        Assert.Equal(TimeSpan.FromSeconds(30), pieces[1].End);
    }

    [Fact]
    public void SplittingASequenceKeepsOrderNeverMergesAndGivesTheTextBack()
    {
        var first = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(4),
            Text = "Ja. Nein.",
            Words = [Word("Ja.", 0, 1), Word("Nein.", 2, 3)],
            SourceSegmentIndex = 0,
        };
        var second = new TranscriptSegment
        {
            Start = TimeSpan.FromSeconds(4),
            End = TimeSpan.FromSeconds(8),
            Text = "Vielleicht.",
            Words = [Word("Vielleicht.", 5, 6)],
            SourceSegmentIndex = 1,
        };

        var pieces = SentenceSplitter.Split([first, second]);

        Assert.Equal(["Ja.", "Nein.", "Vielleicht."], pieces.Select(p => p.Text));
        Assert.Equal([0, 0, 1], pieces.Select(p => p.SourceSegmentIndex));

        // Joining a segment's pieces with single spaces is the segment's text — the cut moved no
        // character and invented none.
        Assert.Equal(first.Text, string.Join(' ', pieces.Where(p => p.SourceSegmentIndex == 0).Select(p => p.Text)));
        Assert.Equal(first.Words.Count + second.Words.Count, pieces.Sum(p => p.Words.Count));
    }

    [Theory]
    [InlineData("aussieht.", "Moin")]
    [InlineData("Was?", "Und")]
    [InlineData("Nein!", "Und")]
    [InlineData("Naja…", "Und")]
    [InlineData("Naja...", "Und")]
    [InlineData("fertig.\"", "Und")]      // a closing quote after the mark
    [InlineData("fertig.«", "Und")]
    [InlineData("fertig.)", "Und")]
    [InlineData("»fertig.«", "Und")]
    [InlineData("sagt.", "„Nein")]         // the next sentence opening with a quote
    [InlineData("dice.", "¿Qué")]          // or an inverted mark
    [InlineData("said.", "\"Yes")]
    [InlineData("said.", "(Yes")]
    [InlineData(" fertig. ", " Und ")]     // engines pad tokens with spaces
    [InlineData("Genau.", "50")]           // a number opens a sentence too — measured, see the class remarks
    [InlineData("Westen.", "4.41")]
    public void AMarkFollowedByACapitalOrANumberEndsASentence(string word, string next) =>
        Assert.True(SentenceSplitter.EndsSentence(word, next));

    [Theory]
    [InlineData("Ende", "Und")]            // no mark at all
    [InlineData("Ende.", "und")]           // the next word does not open a sentence
    [InlineData("Ende.", "")]
    [InlineData("Ende.", "„und")]
    [InlineData("z.", "B.")]               // a single letter is an abbreviation
    [InlineData("B.", "Kantinen")]
    [InlineData("u.", "a.")]
    [InlineData("z.B.", "Kantinen")]       // a stop inside the word is one too
    [InlineData("d.h.", "Die")]
    [InlineData("e.g.", "The")]
    [InlineData("3.", "Oktober")]          // digits alone are an ordinal
    [InlineData("1990.", "Dann")]          // at the cost of a year that really ended a sentence
    [InlineData("bzw.", "die")]
    [InlineData("...", "Und")]             // marks alone end nothing
    [InlineData("…", "Und")]
    [InlineData("\"", "Und")]
    [InlineData("", "Und")]
    public void WhatLooksLikeAFullStopButIsNotOneDoesNot(string word, string next) =>
        Assert.False(SentenceSplitter.EndsSentence(word, next));

    [Theory]
    [InlineData("Dr.", "Müller")]
    [InlineData("Prof.", "Schmidt")]
    [InlineData("ca.", "40")]
    [InlineData("Nr.", "5")]
    public void AnAbbreviationBeforeACapitalOrANumberIsTheKnownFalseCut(string word, string next)
    {
        // Recorded as a test rather than hidden: each of these reads as a sentence end under this
        // rule, and a per-language abbreviation list was declined on purpose — see the class
        // remarks. If that decision is ever reversed, these are the assertions to flip.
        Assert.True(SentenceSplitter.EndsSentence(word, next));
    }

    private static TranscriptWord Word(string text, double start, double end) =>
        new() { Text = text, Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end) };
}
