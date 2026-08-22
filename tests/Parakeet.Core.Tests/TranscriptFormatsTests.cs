using Parakeet.Core.Formatting;

namespace Parakeet.Core.Tests;

/// <summary>
/// The registry's one spelling per format, and the list that every reader of a format list is
/// meant to see.
/// </summary>
public class TranscriptFormatsTests
{
    [Fact]
    public void CanonicalResolvesEveryAliasToTheRegistrysSpellingAndNamesEachFormatOnce()
    {
        // Every spelling TryGet accepts for the word-timed format, the turns format and plain text,
        // in the order a user might type them. The guards that used to read this list compared the
        // typed spelling against the canonical id and let the aliases through; the writer resolved
        // them and wrote duplicates. One list, resolved once, in first-seen order.
        var canonical = TranscriptFormats.Canonical(
            ["txt", "words", "WEBVTT-WORDS", ".vtt-words", ".rttm", "RTTM", "text", "plain", "vtt", "webvtt", "srt"]);

        Assert.Equal(["txt", "vtt-words", "rttm", "vtt", "srt"], canonical);
    }

    [Fact]
    public void CanonicalRefusesASpellingThatNamesNothing()
    {
        var failure = Assert.Throws<ArgumentException>(() => TranscriptFormats.Canonical(["txt", "docx"]));
        Assert.Contains("docx", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Known formats", failure.Message, StringComparison.Ordinal);
    }
}
