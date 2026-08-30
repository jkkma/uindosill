using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class SearchTokenizerTests
{
    [Fact]
    public void LowerCasesInvariantlyAndSplitsOnEverythingElse()
    {
        Assert.Equal(["year", "over", "year"], SearchTokenizer.Tokenize("Year-over-year!"));
        Assert.Equal(["don", "t"], SearchTokenizer.Tokenize("Don't"));
        Assert.Equal(["3", "2"], SearchTokenizer.Tokenize("3.2"));
    }

    [Fact]
    public void AccentedLettersSurvive()
    {
        // The 25 languages are the requirement; a tokenizer that strips diacritics would fold
        // Spanish "si" and "sí" into one term and call it a match.
        Assert.Equal(["café", "señor", "größe"], SearchTokenizer.Tokenize("Café, Señor, Größe"));
    }

    [Fact]
    public void EmptyAndPunctuationOnlyTextTokenizesToNothing()
    {
        Assert.Empty(SearchTokenizer.Tokenize(string.Empty));
        Assert.Empty(SearchTokenizer.Tokenize("— … !?"));
    }

    [Fact]
    public void NormalizeJoinsWithSingleSpaces() =>
        Assert.Equal("wir haben s gesehen", SearchTokenizer.Normalize("«Wir haben's  gesehen!»"));

    [Fact]
    public void DecomposedAccentsTokenizeTheSameAsComposedOnes()
    {
        // In NFD, "señor" is n plus a combining tilde, and a combining mark is not a letter to
        // Rune.IsLetterOrDigit — without composing first the word split into "sen" and "or", and
        // every accented comparison quietly failed whenever the two sides' Unicode forms
        // diverged. The two spellings below are different char sequences of the same word.
        var composed = "señor";
        var decomposed = "señor";
        Assert.NotEqual(composed, decomposed);

        Assert.Equal(SearchTokenizer.Tokenize(composed), SearchTokenizer.Tokenize(decomposed));
        Assert.Equal(["señor"], SearchTokenizer.Tokenize(decomposed));
    }
}

public class TranscriptWindowBuilderTests
{
    private static TranscriptDocument Transcript(params string[] texts)
    {
        // Ten-second segments back to back, so segment n covers [10n, 10n+10).
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

        return new TranscriptDocument { Segments = segments };
    }

    [Fact]
    public void EveryNonEmptySegmentLandsInAtLeastOneWindow()
    {
        var document = Transcript([.. Enumerable.Range(0, 30).Select(i => $"segment number {i}")]);
        var windows = TranscriptWindowBuilder.Build(document);

        for (var id = 1; id <= 30; id++)
        {
            Assert.Contains(windows, w => w.FirstSegment <= id && id <= w.LastSegment);
        }
    }

    [Fact]
    public void HalfOverlapPutsInteriorSegmentsInTwoWindows()
    {
        // The point of the overlap: a question landing near a window edge still finds its
        // context whole in the neighbour.
        var document = Transcript([.. Enumerable.Range(0, 30).Select(i => $"segment number {i}")]);
        var windows = TranscriptWindowBuilder.Build(document);

        // Away from both edges, exactly two windows hold each segment.
        for (var id = 7; id <= 24; id++)
        {
            Assert.Equal(2, windows.Count(w => w.FirstSegment <= id && id <= w.LastSegment));
        }
    }

    [Fact]
    public void WindowsCarryContiguousRunsWithRealTimes()
    {
        var document = Transcript([.. Enumerable.Range(0, 30).Select(i => $"segment number {i}")]);
        var windows = TranscriptWindowBuilder.Build(document);

        Assert.NotEmpty(windows);
        Assert.All(windows, w =>
        {
            Assert.InRange(w.FirstSegment, 1, 30);
            Assert.InRange(w.LastSegment, w.FirstSegment, 30);
            Assert.Equal(document.Segments[w.FirstSegment - 1].Start, w.Start);
            Assert.Equal(document.Segments[w.LastSegment - 1].End, w.End);
            Assert.Contains($"segment number {w.FirstSegment - 1}", w.Text, StringComparison.Ordinal);
            Assert.Contains($"segment number {w.LastSegment - 1}", w.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EmptySegmentsContributeNoTextButDoNotBreakTheRun()
    {
        var document = Transcript("first thing", "", "third thing");
        var windows = TranscriptWindowBuilder.Build(document);

        var window = Assert.Single(windows);
        Assert.Equal(1, window.FirstSegment);
        Assert.Equal(3, window.LastSegment);
        Assert.Equal("first thing third thing", window.Text);
    }

    [Fact]
    public void AnEmptyTranscriptYieldsNoWindows()
    {
        Assert.Empty(TranscriptWindowBuilder.Build(TranscriptDocument.Empty));
        Assert.Empty(TranscriptWindowBuilder.Build(Transcript("", "", "")));
    }

    [Fact]
    public void SegmentsOutOfTimeOrderAreStillRetrievable()
    {
        // A hand-edited file is exactly what the reader reopens, and the grid used to stop at
        // the FINAL segment's midpoint — a transcript whose last entry sat early in time left
        // everything after it silently outside every window, a recall hole with no refusal
        // anywhere.
        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.FromSeconds(200), End = TimeSpan.FromSeconds(210), Text = "the late remark about the axolotl" },
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "the early opening about the budget" },
            ],
        };

        var windows = TranscriptWindowBuilder.Build(document);

        Assert.Contains(windows, w => w.Text.Contains("axolotl", StringComparison.Ordinal));
        Assert.Contains(windows, w => w.Text.Contains("budget", StringComparison.Ordinal));
    }

    [Fact]
    public void APreviewNeverSplitsASurrogatePair()
    {
        // A preview ending in U+FFFD reads as corruption. The window's text is cut between
        // characters, never through an astral pair.
        var window = TranscriptWindowBuilder.FromRun(
            Transcript(string.Concat(Enumerable.Repeat("🦎", 60))), 1, 1);

        var preview = window.Preview(80);
        Assert.True(preview.Length <= 80);
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
        Assert.DoesNotContain('�', preview);
        Assert.False(char.IsHighSurrogate(preview[^2]), "the cut landed inside a surrogate pair");
    }

    [Fact]
    public void SparseAudioDoesNotDuplicateARun()
    {
        // One segment at 40–50 s sits inside two grid windows; an index holding the same run
        // twice would count its terms twice in every document-frequency figure.
        var segments = new List<TranscriptSegment>
        {
            new() { Start = TimeSpan.FromSeconds(40), End = TimeSpan.FromSeconds(50), Text = "alone in the dark" },
        };
        var windows = TranscriptWindowBuilder.Build(new TranscriptDocument { Segments = segments });

        var window = Assert.Single(windows);
        Assert.Equal("S1", window.CitationId);
    }

    [Fact]
    public void TheCoverVariantTilesEverySegmentExactlyOnce()
    {
        // The whole-transcript path's evidence shape: retrieval's half-overlap would send the
        // transcript twice in one prompt, so the cover tiles it — every non-empty segment in
        // exactly one window, none left out.
        var document = Transcript([.. Enumerable.Range(0, 30).Select(i => $"segment number {i}")]);
        var windows = TranscriptWindowBuilder.Build(document, TranscriptWindowOptions.Cover);

        for (var id = 1; id <= 30; id++)
        {
            Assert.Equal(1, windows.Count(w => w.FirstSegment <= id && id <= w.LastSegment));
        }

        Assert.Equal(document.Segments.Count, windows.Sum(w => w.LastSegment - w.FirstSegment + 1));
    }

    [Fact]
    public void AHandEditedCoverStillTilesEachSegmentExactlyOnce()
    {
        // Out-of-time-order segments used to hand a grid position the whole [smallest..largest]
        // id run, swallowing segments whose midpoints lived in another position — the same text
        // sent twice in one prompt, in the one shape whose contract is no overlap (found
        // 2026-08-30). Each scatter is its own citable run instead.
        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(10), Text = "early opening" },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(100), End = TimeSpan.FromSeconds(110), Text = "late aside" },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(20), End = TimeSpan.FromSeconds(30), Text = "early follow-up" },
                new TranscriptSegment { Start = TimeSpan.FromSeconds(110), End = TimeSpan.FromSeconds(120), Text = "late closing" },
            ],
        };

        var windows = TranscriptWindowBuilder.Build(document, TranscriptWindowOptions.Cover);

        for (var id = 1; id <= 4; id++)
        {
            Assert.Equal(1, windows.Count(w => w.FirstSegment <= id && id <= w.LastSegment));
        }
    }

    [Fact]
    public void TheWideVariantMakesLongerWindows()
    {
        var document = Transcript([.. Enumerable.Range(0, 30).Select(i => $"segment number {i}")]);
        var narrow = TranscriptWindowBuilder.Build(document);
        var wide = TranscriptWindowBuilder.Build(document, TranscriptWindowOptions.Wide);

        Assert.True(wide.Count < narrow.Count);
        Assert.True(wide.Max(w => w.Duration) > narrow.Max(w => w.Duration));
    }

    [Fact]
    public void CitationIdSpellsPointsAndRanges()
    {
        var document = Transcript("one segment", "two segments");

        Assert.Equal("S1", TranscriptWindowBuilder.FromRun(document, 1, 1).CitationId);
        Assert.Equal("S1-S2", TranscriptWindowBuilder.FromRun(document, 1, 2).CitationId);
    }

    [Fact]
    public void FromRunWritesEachVoiceOncePerTurnAndRepeatsItAfterAGap()
    {
        // The maintainer's decision of 2026-08-30, reversing 2026-08-24's: the evidence says who
        // is speaking, so the model can. One label per turn, not per segment; a labelled segment
        // after an unlabelled one repeats its label, because the gap left the turn ambiguous —
        // and the label is whatever the document carries, a reader's name included.
        var document = Transcript("one", "two", "three", "four", "five");
        document = document with
        {
            Segments =
            [
                document.Segments[0] with { Speaker = "Speaker 1" },
                document.Segments[1] with { Speaker = "Speaker 1" },
                document.Segments[2] with { Speaker = "Ada" },
                document.Segments[3] with { Speaker = null },
                document.Segments[4] with { Speaker = "Ada" },
            ],
        };

        Assert.Equal(
            "Speaker 1: one two Ada: three four Ada: five",
            TranscriptWindowBuilder.FromRun(document, 1, 5).Text);

        // A transcript that was never labelled renders byte-identically to what it always did.
        Assert.Equal("one two", TranscriptWindowBuilder.FromRun(Transcript("one", "two"), 1, 2).Text);
    }

    [Fact]
    public void FromRunRefusesIdsOutsideTheTranscript()
    {
        var document = Transcript("only one");

        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptWindowBuilder.FromRun(document, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptWindowBuilder.FromRun(document, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptWindowBuilder.FromRun(document, 2, 1));
    }

    [Fact]
    public void NonsenseOptionsRefuse()
    {
        var document = Transcript("something");

        Assert.Throws<ArgumentException>(() => TranscriptWindowBuilder.Build(
            document, new TranscriptWindowOptions { WindowLength = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => TranscriptWindowBuilder.Build(
            document, new TranscriptWindowOptions { Stride = TimeSpan.FromSeconds(90) }));
    }
}

public class Bm25RetrieverTests
{
    private static IReadOnlyList<TranscriptWindow> Windows(params string[] texts)
    {
        var windows = new List<TranscriptWindow>();
        for (var i = 0; i < texts.Length; i++)
        {
            windows.Add(new TranscriptWindow
            {
                FirstSegment = (i * 5) + 1,
                LastSegment = (i * 5) + 5,
                Start = TimeSpan.FromSeconds(i * 60),
                End = TimeSpan.FromSeconds((i * 60) + 60),
                Text = texts[i],
            });
        }

        return windows;
    }

    [Fact]
    public void TheWindowThatHoldsTheTermComesFirst()
    {
        // The question's filler words appear in several windows, as they would in a real
        // transcript — idf has to keep them from outvoting the one distinctive term.
        var retriever = new Bm25Retriever(Windows(
            "we talked about the weather and about the traffic",
            "then they turned to the hippopotamus at the zoo",
            "and they talked about the weather again"));

        var hits = retriever.Retrieve("what did they say about the hippopotamus", 3);

        Assert.NotEmpty(hits);
        Assert.Equal("S6-S10", hits[0].Window.CitationId);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var retriever = new Bm25Retriever(Windows("The Hippopotamus Was Mentioned Here"));

        Assert.Single(retriever.Retrieve("hippopotamus", 5));
        Assert.Single(retriever.Retrieve("HIPPOPOTAMUS", 5));
    }

    [Fact]
    public void ARareTermOutweighsACommonOne()
    {
        // Both query terms appear once in their windows; "the" appears everywhere, so idf has
        // to do the ranking — a scorer without it would tie them.
        var retriever = new Bm25Retriever(Windows(
            "the meeting started with the agenda and the minutes",
            "the zebra escaped during the discussion",
            "the closing remarks thanked the attendees"));

        var hits = retriever.Retrieve("the zebra", 3);

        Assert.Equal("S6-S10", hits[0].Window.CitationId);
    }

    [Fact]
    public void NoMatchMeansEmptyNotError()
    {
        var retriever = new Bm25Retriever(Windows("nothing relevant lives here"));

        Assert.Empty(retriever.Retrieve("quantum chromodynamics", 5));
        Assert.Empty(retriever.Retrieve("", 5));
        Assert.Empty(new Bm25Retriever([]).Retrieve("anything", 5));
    }

    [Fact]
    public void TheLimitIsRespectedAndBestComesFirst()
    {
        var retriever = new Bm25Retriever(Windows(
            "fox", "fox fox", "fox fox fox", "no animals here"));

        var hits = retriever.Retrieve("fox", 2);

        Assert.Equal(2, hits.Count);
        Assert.True(hits[0].Score >= hits[1].Score);
        Assert.Equal("S11-S15", hits[0].Window.CitationId);
    }

    [Fact]
    public void TiesKeepTranscriptOrder()
    {
        // Identical windows score identically; a tie that reordered between runs would make
        // recall measurements unrepeatable.
        var retriever = new Bm25Retriever(Windows("same words here", "same words here"));

        var hits = retriever.Retrieve("words", 2);

        Assert.Equal(2, hits.Count);
        Assert.Equal(hits[0].Score, hits[1].Score);
        Assert.True(hits[0].Window.FirstSegment < hits[1].Window.FirstSegment);
    }

    [Fact]
    public void APlantedNeedleIsFound()
    {
        // The register's needle check, at retrieval tier: a distinctive sentence planted in a
        // long stretch of repetitive talk must surface in the top hits.
        var texts = Enumerable.Repeat("we should probably look at the numbers again before friday", 40).ToArray();
        texts[23] = "the axolotl regenerated its password on a tuesday";

        var retriever = new Bm25Retriever(Windows(texts));
        var hits = retriever.Retrieve("axolotl password", 10);

        Assert.NotEmpty(hits);
        Assert.Equal((23 * 5) + 1, hits[0].Window.FirstSegment);
    }

    [Fact]
    public void ASillyLimitRefuses() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25Retriever([]).Retrieve("x", 0));
}
