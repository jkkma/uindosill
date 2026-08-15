using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class ScriptDetectionTests
{
    [Theory]
    [InlineData('a', TextScript.Latin)]
    [InlineData('Z', TextScript.Latin)]
    [InlineData('é', TextScript.Latin)]
    [InlineData('ż', TextScript.Latin)]
    [InlineData('ц', TextScript.Cyrillic)]
    [InlineData('Ђ', TextScript.Cyrillic)]
    [InlineData('α', TextScript.Greek)]
    [InlineData('Ω', TextScript.Greek)]
    [InlineData('7', TextScript.None)]
    [InlineData(' ', TextScript.None)]
    [InlineData('.', TextScript.None)]
    public void CharactersLandInTheExpectedScript(char value, TextScript expected) =>
        Assert.Equal(expected, TranscriptAnalysis.ScriptOf(value));

    [Fact]
    public void PunctuationAndDigitsHaveNoScript() =>
        Assert.Equal(TextScript.None, TranscriptAnalysis.DominantScript("2026 — 12:04 (!)"));

    [Fact]
    public void TheMajorityScriptWinsRatherThanTheFirstLetter() =>
        // One stray Latin acronym does not make a Cyrillic sentence Latin.
        Assert.Equal(TextScript.Cyrillic, TranscriptAnalysis.DominantScript("цар фінаншл тім CMP"));
}

public class TranscriptAnalysisTests
{
    private static TranscriptSegment Segment(
        double start, string text, params float[] confidences)
    {
        var words = confidences
            .Select((c, i) => new TranscriptWord
            {
                Text = $"w{i}",
                Start = TimeSpan.FromSeconds(start + i),
                End = TimeSpan.FromSeconds(start + i + 1),
                Confidence = c,
            })
            .ToList();

        return new TranscriptSegment
        {
            Start = TimeSpan.FromSeconds(start),
            End = TimeSpan.FromSeconds(start + Math.Max(1, confidences.Length)),
            Text = text,
            Words = words,
        };
    }

    private static TranscriptDocument Document(params TranscriptSegment[] segments) =>
        new() { Segments = segments };

    [Fact]
    public void AUniformTranscriptHasNothingToReport()
    {
        var document = Document(
            Segment(0, "the quick brown fox", 0.9f, 0.9f),
            Segment(4, "jumps over the lazy dog", 0.95f));

        Assert.Empty(TranscriptAnalysis.Analyse(document, 0.45f));
    }

    [Fact]
    public void ASegmentInAnotherScriptIsFlaggedAgainstTheDocumentMajority()
    {
        var document = Document(
            Segment(0, "our financial team", 0.9f),
            Segment(4, "and our contracts people", 0.9f),
            Segment(8, "цар фінаншл тім", 0.9f));

        var anomaly = Assert.Single(TranscriptAnalysis.Analyse(document, null));

        Assert.Equal(TranscriptAnomalyKind.ScriptDisagreement, anomaly.Kind);
        Assert.Equal(2, anomaly.SegmentIndex);
        Assert.Equal(TimeSpan.FromSeconds(8), anomaly.Start);
        Assert.Contains("Cyrillic", anomaly.Detail, StringComparison.Ordinal);
        Assert.Contains("Latin", anomaly.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMajorityDecidesWhichSegmentIsTheOddOne()
    {
        // The same two scripts, the proportions reversed: the lone Latin segment is now the
        // anomaly. A per-segment vote would report the wrong one here.
        var document = Document(
            Segment(0, "цар фінаншл тім"),
            Segment(4, "і цар контракспипо"),
            Segment(8, "our contracts people"));

        var anomaly = Assert.Single(TranscriptAnalysis.Analyse(document, null));

        Assert.Equal(2, anomaly.SegmentIndex);
        Assert.Contains("Latin where the transcript is Cyrillic", anomaly.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASegmentWithoutLettersAgreesWithEverything()
    {
        var document = Document(
            Segment(0, "the quick brown fox"),
            Segment(4, "2026."),
            Segment(8, "jumps over the lazy dog"));

        Assert.Empty(TranscriptAnalysis.Analyse(document, null));
    }

    [Fact]
    public void LowConfidenceWordsAreCountedPerSegment()
    {
        var document = Document(
            Segment(0, "clear speech", 0.9f, 0.95f),
            Segment(4, "muddled speech", 0.2f, 0.3f, 0.9f));

        var anomaly = Assert.Single(TranscriptAnalysis.Analyse(document, 0.45f));

        Assert.Equal(TranscriptAnomalyKind.LowConfidence, anomaly.Kind);
        Assert.Equal(1, anomaly.SegmentIndex);
        Assert.Equal("2 of 3 words below 0.45", anomaly.Detail);
    }

    [Fact]
    public void ANullThresholdReportsScriptOnly()
    {
        var document = Document(Segment(0, "clear speech", 0.01f, 0.02f));

        Assert.Empty(TranscriptAnalysis.Analyse(document, null));
    }

    [Fact]
    public void ScriptDisagreementSortsAheadOfLowConfidence()
    {
        var document = Document(
            Segment(0, "the quick brown fox", 0.1f),
            Segment(4, "jumps over the lazy dog", 0.9f),
            Segment(8, "цар фінаншл тім", 0.9f));

        var anomalies = TranscriptAnalysis.Analyse(document, 0.45f);

        Assert.Equal(2, anomalies.Count);
        Assert.Equal(TranscriptAnomalyKind.ScriptDisagreement, anomalies[0].Kind);
        Assert.Equal(TranscriptAnomalyKind.LowConfidence, anomalies[1].Kind);
    }

    [Fact]
    public void OneSegmentCanCarryBothFindings()
    {
        // The shape the NASA clip produced: the odd-script segment is also the least confident.
        var document = Document(
            Segment(0, "the quick brown fox", 0.9f, 0.9f),
            Segment(4, "цар фінаншл тім", 0.24f, 0.35f, 0.21f));

        var anomalies = TranscriptAnalysis.Analyse(document, 0.45f);

        Assert.Equal(2, anomalies.Count);
        Assert.All(anomalies, a => Assert.Equal(1, a.SegmentIndex));
    }

    [Fact]
    public void EmptySegmentsAreSkipped()
    {
        var document = Document(
            Segment(0, "the quick brown fox", 0.9f),
            Segment(4, "   "));

        Assert.Empty(TranscriptAnalysis.Analyse(document, 0.45f));
    }

    [Fact]
    public void AnEmptyDocumentIsNotAnAnomaly() =>
        Assert.Empty(TranscriptAnalysis.Analyse(TranscriptDocument.Empty, 0.45f));

    [Fact]
    public void WordsWithoutConfidenceAreNotCountedAgainstTheSegment()
    {
        var segment = new TranscriptSegment
        {
            Start = TimeSpan.Zero,
            End = TimeSpan.FromSeconds(2),
            Text = "the quick brown fox",
            Words = [new TranscriptWord { Text = "the", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1) }],
        };

        Assert.Empty(TranscriptAnalysis.Analyse(Document(segment), 0.45f));
    }
}
