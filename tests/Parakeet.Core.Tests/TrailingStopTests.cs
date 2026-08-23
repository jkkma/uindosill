using Parakeet.Core.Formatting;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class TrailingStopTests
{
    [Theory]
    [InlineData("Das ist gut.", "Das ist gut")]
    [InlineData("Das ist gut.  ", "Das ist gut")]
    [InlineData("Wirklich?", "Wirklich?")]
    [InlineData("Toll!", "Toll!")]
    [InlineData("Naja...", "Naja...")]
    [InlineData("Naja…", "Naja…")]
    [InlineData("sagte er.\"", "sagte er\"")]
    [InlineData("„Ein Satz.“", "„Ein Satz“")]
    [InlineData("»So.«", "»So«")]
    [InlineData("(so.)", "(so)")]
    [InlineData("Mr.", "Mr")]
    [InlineData("ohne Punkt", "ohne Punkt")]
    [InlineData("z.B. hier", "z.B. hier")]
    [InlineData("", "")]
    [InlineData(".", "")]
    public void OnlyASentenceFinalFullStopGoes(string input, string expected) =>
        Assert.Equal(expected, TrailingStop.Strip(input));

    [Fact]
    public void ALineThatLosesNothingIsTheSameInstance()
    {
        // Callers keep what they have when nothing changed — the cue builder and the window both
        // test for this rather than comparing strings.
        const string text = "Wirklich?";
        Assert.Same(text, TrailingStop.Strip(text));

        var word = new TranscriptWord { Text = "Wirklich?", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1) };
        Assert.Same(word, TrailingStop.Strip(word));
        Assert.Equal("gut", TrailingStop.Strip(word with { Text = "gut." }).Text);
    }

    [Fact]
    public void EveryExportedCueLosesItsFinalStopAndTheWordTimedVttAgrees()
    {
        // A cue is cut by length and time, not by sentence, so two sentences can share one: the stop
        // between them stays and the one at the end goes — in the plain lines, and in the words the
        // word-timed VTT writes, so the three subtitle files say the same text. A cue timed by
        // character share (no words) is treated the same, and a question keeps its mark.
        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment
                {
                    Start = TimeSpan.Zero,
                    End = TimeSpan.FromSeconds(3),
                    Text = "Das ist gut. Und das auch.",
                    Words =
                    [
                        Word("Das", 0.1, 0.3), Word("ist", 0.3, 0.5), Word("gut.", 0.5, 0.9),
                        Word("Und", 1.4, 1.6), Word("das", 1.6, 1.8), Word("auch.", 1.8, 2.4),
                    ],
                },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(4), End = TimeSpan.FromSeconds(6), Text = "Wirklich?" },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(7), End = TimeSpan.FromSeconds(9), Text = "Ohne Zeiten gesagt." },
            ],
        };

        var srt = TranscriptFormats.Srt.Format(document);
        Assert.Contains("Das ist gut. Und das auch\n", srt, StringComparison.Ordinal);
        Assert.DoesNotContain("auch.\n", srt, StringComparison.Ordinal);
        Assert.Contains("Wirklich?\n", srt, StringComparison.Ordinal);
        Assert.Contains("Ohne Zeiten gesagt\n", srt, StringComparison.Ordinal);

        var vtt = TranscriptFormats.Vtt.Format(document);
        Assert.Contains("Das ist gut. Und das auch\n", vtt, StringComparison.Ordinal);
        Assert.Contains("Ohne Zeiten gesagt\n", vtt, StringComparison.Ordinal);

        var words = TranscriptFormats.WordTimedVtt.Format(document);
        Assert.Contains("<c>gut.</c>", words, StringComparison.Ordinal);
        Assert.Contains("<c>auch</c>", words, StringComparison.Ordinal);
        Assert.DoesNotContain("<c>auch.</c>", words, StringComparison.Ordinal);

        // And the transcript formats keep the text as the model wrote it.
        Assert.Contains("Und das auch.", TranscriptFormats.PlainText.Format(document), StringComparison.Ordinal);
        Assert.Contains("Und das auch.", TranscriptFormats.Json.Format(document), StringComparison.Ordinal);
        Assert.Contains("Und das auch.", TranscriptFormats.Markdown.Format(document), StringComparison.Ordinal);

        static TranscriptWord Word(string text, double start, double end) =>
            new() { Text = text, Start = TimeSpan.FromSeconds(start), End = TimeSpan.FromSeconds(end) };
    }
}
