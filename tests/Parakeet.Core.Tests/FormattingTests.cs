using System.Text.Json;
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
    public void LinesStayWithinTheCharacterLimit()
    {
        var text = string.Join(' ', Enumerable.Repeat("elephant", 30));
        var cues = SubtitleCueBuilder.Build([Segment(0, 30, text)]);

        Assert.All(cues, c => Assert.True(c.Lines.Count <= 2));
        Assert.All(cues, c => Assert.All(c.Lines, line => Assert.True(line.Length <= 50, line)));
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
        foreach (var formatter in TranscriptFormats.All)
        {
            var output = formatter.Format(Document(), TranscriptFormatOptions.Default with { NewLine = "\r\n" });
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
