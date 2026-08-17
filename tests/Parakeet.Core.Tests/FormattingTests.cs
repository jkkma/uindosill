using System.Globalization;
using System.Text;
using System.Text.Json;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Formatting;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class TimecodeTests
{
    [Theory]
    [InlineData(0, "00:00:00,000")]
    [InlineData(1.234, "00:00:01,234")]
    [InlineData(61.5, "00:01:01,500")]
    [InlineData(3661.001, "01:01:01,001")]
    public void SrtTimecodesUseCommaAndThreeDigits(double seconds, string expected) =>
        Assert.Equal(expected, Timecode.ToSrt(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void VttTimecodesUseADot() =>
        Assert.Equal("00:00:01.234", Timecode.ToVtt(TimeSpan.FromSeconds(1.234)));

    [Fact]
    public void HoursDoNotWrapAtTwentyFour()
    {
        // TimeSpan.Hours would report 1 for a 25-hour offset and produce a corrupt file.
        Assert.Equal("25:00:00,000", Timecode.ToSrt(TimeSpan.FromHours(25)));
    }

    [Fact]
    public void NegativeTimesClampRatherThanEmitAnInvalidCue() =>
        Assert.Equal("00:00:00,000", Timecode.ToSrt(TimeSpan.FromSeconds(-3)));
}

public class SubtitleCueBuilderTests
{
    private static TranscriptSegment Segment(double start, double end, string text, bool withWords = true)
    {
        var words = new List<TranscriptWord>();
        if (withWords)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var slice = (end - start) / Math.Max(1, parts.Length);
            for (var i = 0; i < parts.Length; i++)
            {
                words.Add(new TranscriptWord
                {
                    Text = parts[i],
                    Start = TimeSpan.FromSeconds(start + (slice * i)),
                    End = TimeSpan.FromSeconds(start + (slice * (i + 1))),
                    Confidence = 0.9f,
                });
            }
        }

        return new TranscriptSegment
        {
            Start = TimeSpan.FromSeconds(start),
            End = TimeSpan.FromSeconds(end),
            Text = text,
            Words = words,
        };
    }

    [Fact]
    public void ShortSegmentBecomesOneCue()
    {
        var cues = SubtitleCueBuilder.Build([Segment(0, 2, "hello there")]);

        var cue = Assert.Single(cues);
        Assert.Equal("hello there", cue.Text);
    }

    [Fact]
    public void LongSegmentIsSplitAtWordBoundaries()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 60));
        var cues = SubtitleCueBuilder.Build([Segment(0, 25, text)]);

        Assert.True(cues.Count > 1);
        Assert.All(cues, c => Assert.All(c.Lines, line => Assert.DoesNotContain("wor ", line, StringComparison.Ordinal)));
        Assert.Equal(60, cues.Sum(c => c.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
    }

    [Fact]
    public void CuesNeverOverlap()
    {
        var text = string.Join(' ', Enumerable.Repeat("something", 80));
        var cues = SubtitleCueBuilder.Build([Segment(0, 30, text), Segment(30, 60, text)]);

        for (var i = 1; i < cues.Count; i++)
        {
            // Overlapping cues make players drop one silently, which loses text with no error.
            Assert.True(cues[i].Start >= cues[i - 1].End, $"cue {i} starts before cue {i - 1} ends");
        }
    }

    [Fact]
    public void CuesRespectTheDurationCap()
    {
        var options = SubtitleOptions.Default with { MaxCueDuration = TimeSpan.FromSeconds(4) };
        var text = string.Join(' ', Enumerable.Repeat("a", 200));
        var cues = SubtitleCueBuilder.Build([Segment(0, 30, text)], options);

        Assert.All(cues, c => Assert.True(c.End - c.Start <= TimeSpan.FromSeconds(4.5)));
    }

    [Fact]
    public void SegmentWithoutWordTimestampsIsStillSplitAndTimed()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 60));
        var cues = SubtitleCueBuilder.Build([Segment(10, 40, text, withWords: false)]);

        Assert.True(cues.Count > 1);
        Assert.Equal(TimeSpan.FromSeconds(10), cues[0].Start);
        Assert.InRange(cues[^1].End.TotalSeconds, 39, 41);
    }

    [Fact]
    public void EmptySegmentsAreSkipped()
    {
        var cues = SubtitleCueBuilder.Build([Segment(0, 2, "   ", withWords: false), Segment(3, 5, "real text")]);

        var cue = Assert.Single(cues);
        Assert.Equal("real text", cue.Text);
    }

    [Fact]
    public void GreedyFillDoesNotStrandAWordAloneAtTheEndOfASegment()
    {
        // Taken from a real transcript: 167 characters at a capacity of 84 filled greedily to
        // 79 + 82 + "thing.", leaving one word flashing on screen by itself.
        const string Text =
            "And and I just want to reiterate that the trial system in that game for learning combos " +
            "is really, really well done. And I wish that other games would copy the same thing.";

        var cues = SubtitleCueBuilder.Build([Segment(0, 8, Text)]);

        Assert.True(cues.Count > 1, "the fixture should span several cues");
        Assert.True(cues[^1].Text.Length >= 16, $"last cue is a widow: '{cues[^1].Text}'");

        // Rebalancing must not lose or duplicate a single word.
        Assert.Equal(
            Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            cues.Sum(c => c.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
    }

    [Fact]
    public void GenuinelyShortSegmentsAreLeftAlone()
    {
        // "Mm-hmm." is a real utterance, not a leftover, and must keep its own cue.
        var cues = SubtitleCueBuilder.Build([Segment(0, 1, "Mm-hmm."), Segment(2, 3, "Um")]);

        Assert.Equal(2, cues.Count);
        Assert.Equal("Mm-hmm.", cues[0].Text);
        Assert.Equal("Um", cues[1].Text);
    }

    [Fact]
    public void RebalancingWillNotStraddleALongPauseToAvoidAWidow()
    {
        // From a real podcast transcript: "...get into local." then nine seconds of laughter, then
        // "Yeah. Correct." Rebalancing on characters alone merged across the gap and produced a
        // cue on screen for 11.9 seconds. An over-long cue is worse than a short one.
        var words = new List<TranscriptWord>();
        void Add(string text, double start, double end) => words.Add(new TranscriptWord
        {
            Text = text,
            Start = TimeSpan.FromSeconds(start),
            End = TimeSpan.FromSeconds(end),
            Confidence = 0.9f,
        });

        var t = 205.0;
        foreach (var word in "you switch to like passport bro content you can just keep the name get into local".Split(' '))
        {
            Add(word, t, t + 0.25);
            t += 0.3;
        }

        Add("Yeah.", 219.0, 219.4);
        Add("Correct.", 219.5, 220.2);

        var segment = new TranscriptSegment
        {
            Start = TimeSpan.FromSeconds(205),
            End = TimeSpan.FromSeconds(220.3),
            Text = string.Join(' ', words.Select(w => w.Text)),
            Words = words,
        };

        var cues = SubtitleCueBuilder.Build([segment]);

        Assert.All(cues, c => Assert.True(
            c.End - c.Start <= SubtitleOptions.Default.MaxCueDuration + TimeSpan.FromMilliseconds(1),
            $"cue spans {(c.End - c.Start).TotalSeconds:0.##}s: '{c.Text}'"));
    }

    [Fact]
    public void RebalancingKeepsEveryCueWithinCapacity()
    {
        var text = string.Join(' ', Enumerable.Repeat("elephantine", 15)) + " x";
        var cues = SubtitleCueBuilder.Build([Segment(0, 20, text)]);

        Assert.All(cues, c => Assert.True(c.Text.Length <= SubtitleOptions.Default.Capacity, c.Text));
    }

    [Fact]
    public void LinesStayWithinTheCharacterLimit()
    {
        var text = string.Join(' ', Enumerable.Repeat("elephant", 30));
        var cues = SubtitleCueBuilder.Build([Segment(0, 30, text)]);

        Assert.All(cues, c => Assert.True(c.Lines.Count <= 2));
        Assert.All(cues, c => Assert.All(c.Lines, line => Assert.True(line.Length <= 50, line)));
    }

    [Fact]
    public void EachLineKnowsExactlyWhichWordsWereWrappedIntoIt()
    {
        // The alignment the whole word-timing feature rests on. It used to be recoverable only by
        // re-splitting a finished line and trusting the tokens to still line up with the words
        // they came from; a drift there moves every timestamp onto its neighbour and leaves output
        // that reads as completely correct.
        var text = string.Join(' ', Enumerable.Repeat("elephant", 30));
        var cues = SubtitleCueBuilder.Build([Segment(0, 30, text)]);

        Assert.All(cues, cue =>
        {
            Assert.Equal(cue.Lines.Count, cue.LineWords.Count);
            for (var i = 0; i < cue.Lines.Count; i++)
            {
                Assert.Equal(cue.Lines[i], string.Join(" ", cue.LineWords[i].Select(w => w.Text.Trim())));
            }
        });

        // And nothing is lost or reordered between the segment and the cues it became.
        Assert.Equal(30, cues.Sum(c => c.Words.Count()));
        Assert.All(cues, cue => Assert.True(
            cue.Words.Zip(cue.Words.Skip(1)).All(pair => pair.Second.Start >= pair.First.Start),
            "words within a cue are out of order"));
    }

    [Fact]
    public void SegmentWithoutWordTimestampsCarriesNoWordsAtAll()
    {
        // Path B synthesises cue times from each chunk's share of the characters. There are no
        // TranscriptWord instances on that path to attach, and an empty list is the honest answer
        // — a formatter must be able to tell "no timings" from "timings at zero".
        var text = string.Join(' ', Enumerable.Repeat("word", 60));
        var cues = SubtitleCueBuilder.Build([Segment(10, 40, text, withWords: false)]);

        Assert.NotEmpty(cues);
        Assert.All(cues, cue => Assert.Empty(cue.LineWords));
        Assert.All(cues, cue => Assert.NotEmpty(cue.Lines));
    }
}

public class FormatterTests
{
    private static TranscriptDocument Document() => new()
    {
        SourceName = "meeting.wav",
        AudioDuration = TimeSpan.FromSeconds(12),
        ModelId = "parakeet-tdt-0.6b-v3-q8_0",
        Quantisation = "q8_0",
        Backend = ComputeBackend.Vulkan,
        ProcessingTime = TimeSpan.FromSeconds(1.2),
        Segments =
        [
            new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(0.5),
                End = TimeSpan.FromSeconds(3),
                Text = "first thing we should do",
                Words =
                [
                    new TranscriptWord { Text = "first", Start = TimeSpan.FromSeconds(0.5), End = TimeSpan.FromSeconds(0.9), Confidence = 0.95f },
                    new TranscriptWord { Text = "thing", Start = TimeSpan.FromSeconds(0.9), End = TimeSpan.FromSeconds(1.4), Confidence = 0.4f },
                ],
            },
            new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(5),
                End = TimeSpan.FromSeconds(8),
                Text = "and | then the second",
            },
        ],
    };

    [Fact]
    public void SrtHasNumberedCuesWithArrows()
    {
        var srt = TranscriptFormats.Srt.Format(Document());
        var lines = srt.Split('\n');

        Assert.Equal("1", lines[0]);
        Assert.Contains("-->", lines[1], StringComparison.Ordinal);
        Assert.Contains("00:00:00,500", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("\n\n", srt, StringComparison.Ordinal);
    }

    [Fact]
    public void VttStartsWithTheSignature()
    {
        var vtt = TranscriptFormats.Vtt.Format(Document());

        Assert.StartsWith("WEBVTT\n\n", vtt, StringComparison.Ordinal);
        Assert.Contains("00:00:00.500", vtt, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonCarriesProvenanceAndWordTimings()
    {
        var json = TranscriptFormats.Json.Format(Document());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("meeting.wav", root.GetProperty("source").GetString());
        Assert.Equal("q8_0", root.GetProperty("quantisation").GetString());
        Assert.Equal("vulkan", root.GetProperty("backend").GetString());

        var segments = root.GetProperty("segments");
        Assert.Equal(2, segments.GetArrayLength());

        var first = segments[0];
        Assert.Equal(0.5, first.GetProperty("start").GetDouble());
        Assert.Equal("first", first.GetProperty("words")[0].GetProperty("w").GetString());
    }

    [Fact]
    public void JsonIsValidWhenNothingWasTranscribed()
    {
        var json = TranscriptFormats.Json.Format(TranscriptDocument.Empty);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(string.Empty, document.RootElement.GetProperty("text").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public void MarkdownEscapesPipesSoTheMetadataTableSurvives()
    {
        var markdown = TranscriptFormats.Markdown.Format(Document());

        Assert.Contains("# meeting.wav", markdown, StringComparison.Ordinal);
        Assert.Contains("| Model | parakeet-tdt-0.6b-v3-q8_0 |", markdown, StringComparison.Ordinal);
        Assert.Contains("and \\| then the second", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextCanOmitTimestamps()
    {
        var text = TranscriptFormats.PlainText.Format(
            Document(), TranscriptFormatOptions.Default with { IncludeTimestamps = false });

        Assert.StartsWith("first thing we should do", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFormatterHandlesAnEmptyDocument()
    {
        foreach (var formatter in TranscriptFormats.All)
        {
            var output = formatter.Format(TranscriptDocument.Empty);
            Assert.NotNull(output);
        }
    }

    [Fact]
    public void NewLineIsHonouredByEveryFormatter()
    {
        // With speaker turns, so the RTTM formatter — which writes nothing for a document that has
        // none — has lines whose endings can be checked like everyone else's.
        var document = Document() with
        {
            SpeakerTurns = [new SpeakerTurn { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(8), Speaker = "A" }],
        };

        foreach (var formatter in TranscriptFormats.All)
        {
            var output = formatter.Format(document, TranscriptFormatOptions.Default with { NewLine = "\r\n" });
            Assert.DoesNotContain("\n\n\n", output.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("\r\n", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("srt", "srt")]
    [InlineData("SRT", "srt")]
    [InlineData(".srt", "srt")]
    [InlineData("markdown", "md")]
    [InlineData("text", "txt")]
    [InlineData("webvtt", "vtt")]
    public void FormatLookupAcceptsTheObviousAliases(string input, string expected)
    {
        Assert.True(TranscriptFormats.TryGet(input, out var formatter));
        Assert.Equal(expected, formatter.Id);
    }

    [Fact]
    public void UnknownFormatNamesTheKnownOnes()
    {
        var exception = Assert.Throws<ArgumentException>(() => TranscriptFormats.Get("docx"));
        Assert.Contains("srt", exception.Message, StringComparison.Ordinal);
    }
}

public class TranscriptDocumentTests
{
    [Fact]
    public void LowConfidenceWordsAreFindable()
    {
        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment
                {
                    Start = TimeSpan.Zero,
                    End = TimeSpan.FromSeconds(1),
                    Text = "a b",
                    Words =
                    [
                        new TranscriptWord { Text = "a", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(0.5), Confidence = 0.2f },
                        new TranscriptWord { Text = "b", Start = TimeSpan.FromSeconds(0.5), End = TimeSpan.FromSeconds(1), Confidence = 0.9f },
                    ],
                },
            ],
        };

        var suspect = Assert.Single(document.LowConfidenceWords(0.45f));
        Assert.Equal("a", suspect.Text);
        Assert.Equal(0.55f, document.Segments[0].MeanConfidence!.Value, 3);
    }

    [Fact]
    public void RealTimeFactorIsNullWithoutBothDurations()
    {
        Assert.Null(new TranscriptDocument { Segments = [] }.RealTimeFactor);

        var document = new TranscriptDocument
        {
            Segments = [],
            AudioDuration = TimeSpan.FromSeconds(10),
            ProcessingTime = TimeSpan.FromSeconds(2),
        };

        Assert.Equal(0.2, document.RealTimeFactor!.Value, 6);
    }

    [Fact]
    public void ShiftingASegmentMovesItsWordsToo()
    {
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(1),
            Text = "x",
            Words = [new TranscriptWord { Text = "x", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1) }],
        };

        var shifted = segment.Shift(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), shifted.Start);
        Assert.Equal(TimeSpan.FromSeconds(30), shifted.Words[0].Start);
    }
}

public class WordTimedVttFormatterTests
{
    private static TranscriptWord Word(string text, int startMs, int endMs) => new()
    {
        Text = text,
        Start = TimeSpan.FromMilliseconds(startMs),
        End = TimeSpan.FromMilliseconds(endMs),
        Confidence = 0.9f,
    };

    /// <summary>
    /// Whole milliseconds throughout, never <c>FromSeconds(1.4)</c>: a fixture whose expected
    /// output depends on how a binary fraction lands in the last millisecond digit is testing the
    /// double, not the formatter.
    /// </summary>
    private static TranscriptDocument Document() => new()
    {
        SourceName = "meeting.wav",
        AudioDuration = TimeSpan.FromSeconds(12),
        Segments =
        [
            new TranscriptSegment
            {
                Start = TimeSpan.FromMilliseconds(500),
                End = TimeSpan.FromMilliseconds(2600),
                Text = "first thing we should do",
                Words =
                [
                    Word("first", 500, 900),
                    Word("thing", 900, 1400),
                    Word("we", 1400, 1700),
                    Word("should", 1700, 2200),
                    Word("do", 2200, 2600),
                ],
            },

            // No words: the engine reported none for this segment. Real and reachable — the
            // clip JSON carries "text" and "words" as independent fields.
            new TranscriptSegment
            {
                Start = TimeSpan.FromMilliseconds(5000),
                End = TimeSpan.FromMilliseconds(8000),
                Text = "and then the second",
            },
        ],
    };

    [Fact]
    public void ThePlainVttOutputIsPinnedSoThisFeatureCannotMoveIt()
    {
        // The point of a separate format id rather than a flag: the default vtt output is
        // byte-stable across this change. Pinned here so a later refactor of the shared cue
        // builder has to notice.
        const string Expected =
            "WEBVTT\n" +
            "\n" +
            "1\n" +
            "00:00:00.500 --> 00:00:02.600\n" +
            "first thing we should do\n" +
            "\n" +
            "2\n" +
            "00:00:05.000 --> 00:00:08.000\n" +
            "and then the second\n" +
            "\n";

        Assert.Equal(Expected, TranscriptFormats.Vtt.Format(Document()));
    }

    [Fact]
    public void TheWordTimedOutputIsThePlainOneWithTagsInsertedAndNothingElse()
    {
        const string Expected =
            "WEBVTT\n" +
            "\n" +
            "1\n" +
            "00:00:00.500 --> 00:00:02.600\n" +
            "<c>first</c><00:00:00.900> <c>thing</c><00:00:01.400> <c>we</c>" +
            "<00:00:01.700> <c>should</c><00:00:02.200> <c>do</c>\n" +
            "\n" +
            "2\n" +
            "00:00:05.000 --> 00:00:08.000\n" +
            "and then the second\n" +
            "\n";

        Assert.Equal(Expected, TranscriptFormats.WordTimedVtt.Format(Document()));
    }

    [Fact]
    public void StrippingTheTagsReproducesThePlainVttByteForByte()
    {
        // The invariant that catches a word/line misalignment. A cue whose timestamps have drifted
        // onto their neighbours still parses, still plays, and still reads correctly — this is the
        // only assertion that sees it.
        var document = new TranscriptDocument
        {
            Segments =
            [
                Spoken(0, "alpha bravo delta gamma sigma theta kappa omega"),
                Spoken(6000, "the quick brown fox jumped over the lazy dog and kept running for a while"),
                new TranscriptSegment
                {
                    Start = TimeSpan.FromMilliseconds(20000),
                    End = TimeSpan.FromMilliseconds(24000),
                    Text = "a segment the engine returned no word timestamps for at all",
                },
            ],
        };

        Assert.Equal(
            TranscriptFormats.Vtt.Format(document),
            StripTags(TranscriptFormats.WordTimedVtt.Format(document)));
    }

    [Fact]
    public void ALineBreakCarriesTheTimestampOntoTheNewLineRatherThanStrandingItAtTheEnd()
    {
        var document = new TranscriptDocument { Segments = [Spoken(0, "alpha bravo delta gamma sigma theta kappa omega")] };

        var payload = Assert.Single(ParseCues(TranscriptFormats.WordTimedVtt.Format(document))).Payload;
        var lines = payload.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("<c>alpha</c>", lines[0], StringComparison.Ordinal);

        // The timestamp belongs to the first word of the second line, so it opens that line rather
        // than dangling off the end of the first one.
        Assert.EndsWith("<c>gamma</c>", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("<00:00:02.000><c>sigma</c>", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NoTimestampIsEmittedBeforeTheFirstWordOfACue()
    {
        // A timestamp equal to the cue's start says nothing — the cue is already showing — and is
        // what FFmpeg's reader rejects outright. Continuation lines may start with one; the first
        // line of a cue may not.
        var vtt = TranscriptFormats.WordTimedVtt.Format(Busy());

        foreach (var cue in ParseCues(vtt))
        {
            var opening = cue.Payload.Split('\n')[0];
            Assert.False(
                opening.Length > 1 && opening[0] == '<' && char.IsAsciiDigit(opening[1]),
                $"the cue at {cue.Start} opens with a timestamp: {opening}");
        }
    }

    [Fact]
    public void EveryWordAfterTheFirstOfItsCueCarriesATimestamp()
    {
        // The complement of the constraint tests below: a formatter that emitted no tags at all
        // would satisfy every "strictly inside, strictly increasing" assertion in this file.
        var cues = SubtitleCueBuilder.Build(Busy().Segments);
        var expected = cues.Sum(c => Math.Max(0, c.Words.Count() - 1));

        var emitted = ParseCues(TranscriptFormats.WordTimedVtt.Format(Busy()))
            .Sum(c => InlineTimestamps(c.Payload).Count);

        Assert.Equal(expected, emitted);
        Assert.True(emitted > 100, $"the fixture should exercise more than a handful of words, got {emitted}");
    }

    [Fact]
    public void WordTimestampsFallStrictlyInsideTheirCueAndStrictlyIncreaseAfterTidyHasRun()
    {
        AssertWebVttOrdering(TranscriptFormats.WordTimedVtt.Format(Busy()));
    }

    [Fact]
    public void TidyMovingACueOutFromUnderItsOwnWordsDropsTagsRatherThanEmittingInvalidOnes()
    {
        // Tidy rewrites a cue's start and end for readability — a 700 ms floor and a 1 ms gap —
        // after the words are attached, and it does so with a with-expression, so the word times
        // ride along unchanged. Overlapping segments are what make the two actually disagree: the
        // one-word cue is extended to the floor, and the cue behind it is pushed past its own
        // opening words.
        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment
                {
                    Start = TimeSpan.FromMilliseconds(10000),
                    End = TimeSpan.FromMilliseconds(10050),
                    Text = "Mm",
                    Words = [Word("Mm", 10000, 10050)],
                },
                new TranscriptSegment
                {
                    Start = TimeSpan.FromMilliseconds(10000),
                    End = TimeSpan.FromMilliseconds(12000),
                    Text = "and then we should go",
                    Words =
                    [
                        Word("and", 10000, 10400),
                        Word("then", 10400, 10800),
                        Word("we", 10800, 11200),
                        Word("should", 11200, 11600),
                        Word("go", 11600, 12000),
                    ],
                },
            ],
        };

        var cues = SubtitleCueBuilder.Build(document.Segments);
        var vtt = TranscriptFormats.WordTimedVtt.Format(document);

        // The premise: Tidy really has pushed the second cue past word timings it still carries.
        var second = cues[1];
        Assert.Contains(second.Words, w => w.Start <= second.Start);

        // The consequence: those tags are absent, the rest are legal, and no text is lost.
        AssertWebVttOrdering(vtt);
        Assert.Equal(TranscriptFormats.Vtt.Format(document), StripTags(vtt));
        Assert.True(
            InlineTimestamps(ParseCues(vtt)[1].Payload).Count < second.Words.Count() - 1,
            "the fixture no longer forces any tag to be dropped");
    }

    [Fact]
    public void ASegmentWithoutWordTimestampsProducesValidCuesWithNoTags()
    {
        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment
                {
                    Start = TimeSpan.FromMilliseconds(10000),
                    End = TimeSpan.FromMilliseconds(40000),
                    Text = string.Join(' ', Enumerable.Repeat("word", 60)),
                },
            ],
        };

        var vtt = TranscriptFormats.WordTimedVtt.Format(document);
        var cues = ParseCues(vtt);

        Assert.True(cues.Count > 1);
        Assert.All(cues, cue => Assert.Empty(InlineTimestamps(cue.Payload)));
        Assert.DoesNotContain("<c>", vtt, StringComparison.Ordinal);
        Assert.Equal(TranscriptFormats.Vtt.Format(document), vtt);
    }

    [Fact]
    public void EveryWordIsWrappedSoThePastAndFutureSelectorsHaveSomethingToMatch()
    {
        // Bare text between two timestamps matches neither ::cue(:past) nor ::cue(:future) — the
        // WebVTT spec's own styling example is annotated "No match (no elements)". Without a span
        // per word the highlight cannot be styled, which is the entire purpose of this format.
        var vtt = TranscriptFormats.WordTimedVtt.Format(Document());
        var payload = ParseCues(vtt)[0].Payload;

        Assert.Equal(5, CountOccurrences(payload, "<c>"));
        Assert.Equal(5, CountOccurrences(payload, "</c>"));
    }

    [Fact]
    public void TheExtensionDoesNotCollideWithThePlainVttOutput()
    {
        // Both formats requested at once must not resolve to one path: under the default rename
        // policy the loser lands as "name (2).vtt" and nothing says which file is which.
        Assert.NotEqual(TranscriptFormats.Vtt.FileExtension, TranscriptFormats.WordTimedVtt.FileExtension);
        Assert.Equal(".words.vtt", TranscriptFormats.WordTimedVtt.FileExtension);
        Assert.True(TranscriptFormats.TryGet("vtt-words", out var formatter));
        Assert.Same(TranscriptFormats.WordTimedVtt, formatter);
    }

    /// <summary>
    /// Segments long enough and numerous enough that wrapping, splitting, tail rebalancing and
    /// <c>Tidy</c> all run, so the ordering assertions see the output of the whole pipeline.
    /// </summary>
    private static TranscriptDocument Busy()
    {
        var segments = new List<TranscriptSegment>();
        var start = 0;

        for (var i = 0; i < 12; i++)
        {
            var spoken = Spoken(start, string.Join(' ', Enumerable.Range(0, 20 + i).Select(n => $"word{n:00}")));
            segments.Add(spoken);

            // Gaps wide enough that segments never overlap and the 700 ms floor never has to
            // shorten anything. Overlap is a real case and it has its own test; here it would only
            // hide whether a tag went missing for the reason under test.
            start = (int)spoken.End.TotalMilliseconds + 800;

            var interjection = Spoken(start, "Mm-hmm.", msPerWord: 120);
            segments.Add(interjection);
            start = (int)interjection.End.TotalMilliseconds + 800;
        }

        return new TranscriptDocument { Segments = segments };
    }

    /// <summary>A segment whose words are evenly spaced from <paramref name="startMs"/>.</summary>
    private static TranscriptSegment Spoken(int startMs, string text, int msPerWord = 500)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = new List<TranscriptWord>(parts.Length);

        for (var i = 0; i < parts.Length; i++)
        {
            words.Add(Word(parts[i], startMs + (msPerWord * i), startMs + (msPerWord * (i + 1))));
        }

        return new TranscriptSegment
        {
            Start = TimeSpan.FromMilliseconds(startMs),
            End = TimeSpan.FromMilliseconds(startMs + (msPerWord * parts.Length)),
            Text = string.Join(' ', parts),
            Words = words,
        };
    }

    /// <summary>
    /// The rule as FFmpeg's reader enforces it: strictly greater than the cue start, strictly less
    /// than the cue end, strictly greater than the timestamp before it. A file that breaks this
    /// renders in a browser and not through FFmpeg — divergent output from identical bytes.
    /// </summary>
    private static void AssertWebVttOrdering(string vtt)
    {
        var cues = ParseCues(vtt);
        Assert.NotEmpty(cues);

        foreach (var cue in cues)
        {
            var previous = cue.Start;
            foreach (var stamp in InlineTimestamps(cue.Payload))
            {
                Assert.True(stamp > previous, $"{stamp} does not follow {previous} in the cue at {cue.Start}");
                Assert.True(stamp < cue.End, $"{stamp} is not before the end of the cue at {cue.Start}");
                previous = stamp;
            }
        }
    }

    private sealed record ParsedCue(TimeSpan Start, TimeSpan End, string Payload);

    private static List<ParsedCue> ParseCues(string vtt)
    {
        var cues = new List<ParsedCue>();

        foreach (var block in vtt.Replace("\r\n", "\n", StringComparison.Ordinal)
                                 .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var arrow = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (arrow < 0)
            {
                continue;
            }

            var range = lines[arrow].Split(" --> ", StringSplitOptions.None);
            cues.Add(new ParsedCue(
                ParseTimecode(range[0]),
                ParseTimecode(range[1]),
                string.Join("\n", lines[(arrow + 1)..])));
        }

        return cues;
    }

    /// <summary>Reads a tag body the way FFmpeg does: it is a timestamp only if it opens with a digit.</summary>
    private static List<TimeSpan> InlineTimestamps(string payload)
    {
        var stamps = new List<TimeSpan>();

        for (var i = 0; i < payload.Length;)
        {
            var open = payload.IndexOf('<', i);
            if (open < 0)
            {
                break;
            }

            var close = payload.IndexOf('>', open);
            if (close < 0)
            {
                break;
            }

            var body = payload[(open + 1)..close];
            if (body.Length > 0 && char.IsAsciiDigit(body[0]))
            {
                stamps.Add(ParseTimecode(body));
            }

            i = close + 1;
        }

        return stamps;
    }

    private static TimeSpan ParseTimecode(string value)
    {
        var parts = value.Trim().Split(':', '.');
        return new TimeSpan(
            0,
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    private static string StripTags(string vtt)
    {
        var builder = new StringBuilder(vtt.Length);
        var inTag = false;

        foreach (var c in vtt)
        {
            if (c == '<')
            {
                inTag = true;
            }
            else if (c == '>' && inTag)
            {
                inTag = false;
            }
            else if (!inTag)
            {
                // The guard above matters: a cue's own timing line is "-->", and dropping every
                // bare '>' turns it into "--" and fails the comparison for a reason that has
                // nothing to do with the formatter.
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
