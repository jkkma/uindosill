using Parakeet.Core.Formatting;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;

namespace Parakeet.Core.Tests;

/// <summary>
/// The translation seam, with nothing behind it. Every test here runs against the canned
/// translator or against a deliberately misbehaving one, because that is the whole point of the
/// seam existing before the model does: the invariants the 2026-08-19 spike settled — the target
/// token, the lost word timings, the preserved speakers — are asserted now, so the decode loop
/// arrives into a shape that already refuses to break them.
/// </summary>
public class TranslationTests
{
    private static TranscriptSegment Spoken(double start, double end, string text, string? speaker = null) =>
        new()
        {
            Start = TimeSpan.FromSeconds(start),
            End = TimeSpan.FromSeconds(end),
            Text = text,
            SourceSegmentIndex = (int)start,
            Speaker = speaker,
            Words =
            [
                new TranscriptWord
                {
                    Text = text.Split(' ')[0],
                    Start = TimeSpan.FromSeconds(start),
                    End = TimeSpan.FromSeconds(start + 0.4),
                    Speaker = speaker,
                },
            ],
        };

    private static TranscriptDocument Document(params TranscriptSegment[] segments) =>
        new() { Segments = segments, SourceName = "call.wav", ModelId = "tdt-0.6b-v3-f16" };

    [Fact]
    public async Task TheTargetTokenIsOnEverySourceTheTranslatorIsGiven()
    {
        // The invariant the spike found the hard way: the same Spanish segments without >>eng<<
        // came back as fluent German — the checkpoint's first declared target — rather than as an
        // error. A forgotten prefix is therefore invisible downstream, so it is not a convention
        // a caller remembers; it is put on by the seam, and this is the test that says so.
        await using var translator = new FakeTranscriptTranslator();
        var document = Document(Spoken(0, 3, "hola qué tal"), Spoken(3, 6, "muy bien gracias"));

        await TranscriptTranslation.TranslateAsync(document, translator, TranslationOptions.Default);

        Assert.Equal(2, translator.Requests.Count);
        Assert.All(translator.Requests, r => Assert.StartsWith(">>eng<<", r.Source, StringComparison.Ordinal));
        Assert.Equal(">>eng<< hola qué tal", translator.Requests[0].Source);
    }

    [Fact]
    public void ASourceCannotBeBuiltWithoutATargetToken()
    {
        // Blank rather than absent is the shape a bug takes here: a translator whose target token
        // is unset would otherwise build a source that looks right and decodes without complaint.
        var segments = new[] { Spoken(0, 3, "hola") };

        Assert.Throws<ArgumentException>(() =>
            TranslationRequest.Build(segments, TranslationOptions.Default, "   "));
        Assert.Throws<ArgumentNullException>(() =>
            TranslationRequest.Build(segments, TranslationOptions.Default, null!));
    }

    [Fact]
    public async Task WordTimingsDoNotSurviveAndTheSegmentSaysSoRatherThanCarryingTheOldOnes()
    {
        await using var translator = new FakeTranscriptTranslator();
        Assert.False(translator.Capabilities.PreservesWordTimings);

        var document = Document(Spoken(0, 3, "hola qué tal"));
        Assert.NotEmpty(document.Segments[0].Words);

        var translated = await TranscriptTranslation.TranslateAsync(document, translator);

        Assert.Empty(Assert.Single(translated.Segments).Words);
    }

    [Fact]
    public async Task SpeakersAndTimesComeThroughUntouched()
    {
        // Speakers are decided by the pass before this one, from word alignment this pass destroys.
        // If translation could change them there would be no order in which both could be right.
        await using var translator = new FakeTranscriptTranslator();
        var document = Document(
            Spoken(0, 3, "hola qué tal", "Speaker 1"),
            Spoken(3, 6, "muy bien gracias", "Speaker 2"));

        var translated = await TranscriptTranslation.TranslateAsync(document, translator);

        Assert.Equal(["Speaker 1", "Speaker 2"], translated.Segments.Select(s => s.Speaker));
        Assert.Equal(TimeSpan.FromSeconds(3), translated.Segments[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(6), translated.Segments[1].End);
        Assert.Equal([0, 3], translated.Segments.Select(s => s.SourceSegmentIndex));
        Assert.Equal("[en] muy bien gracias", translated.Segments[1].Text);
    }

    [Fact]
    public async Task TheDocumentRecordsWhatTranslatedItAndIntoWhat()
    {
        await using var translator = new FakeTranscriptTranslator();
        var document = Document(Spoken(0, 3, "hola"));

        Assert.False(document.IsTranslated);

        var translated = await TranscriptTranslation.TranslateAsync(document, translator);

        Assert.True(translated.IsTranslated);
        Assert.Equal("en", translated.TranslatedTo);
        Assert.Equal("fake-translator", translated.TranslationModelId);

        // The ASR provenance is not overwritten by the translator's: both models are on the page.
        Assert.Equal("tdt-0.6b-v3-f16", translated.ModelId);
    }

    [Fact]
    public async Task ContextIsTheOnlyLeverAndItReachesTheTranslator()
    {
        await using var translator = new FakeTranscriptTranslator();
        var document = Document(
            Spoken(0, 3, "uno"),
            Spoken(3, 6, "dos"),
            Spoken(6, 9, "tres"));

        await TranscriptTranslation.TranslateAsync(document, translator, new TranslationOptions { ContextSegments = 2 });

        Assert.Empty(translator.Requests[0].Context);
        Assert.Equal(["uno"], translator.Requests[1].Context);
        Assert.Equal(["uno", "dos"], translator.Requests[2].Context);

        // Zero is the default, because nothing has measured what context buys.
        Assert.Equal(0, TranslationOptions.Default.ContextSegments);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranslationOptions { ContextSegments = -1 }.Validate());
    }

    [Fact]
    public async Task AnOverLongSegmentIsRefusedRatherThanTruncated()
    {
        // A truncated source translates fluently and says nothing about the half it dropped, which
        // is the failure this whole contract exists to avoid. The fake counts whitespace tokens
        // instead of SentencePiece ones; what is under test is the refusal, not the count.
        await using var translator = new FakeTranscriptTranslator(new FakeTranslatorOptions { MaxSourceTokens = 4 });
        var document = Document(Spoken(0, 3, "uno dos"), Spoken(3, 6, "uno dos tres cuatro cinco"));

        var refused = await Assert.ThrowsAsync<SegmentTooLongException>(
            () => TranscriptTranslation.TranslateAsync(document, translator));

        Assert.Equal(1, refused.SegmentIndex);
        Assert.Equal(4, refused.Limit);
        Assert.Equal(6, refused.Tokens);   // five words plus the target token
    }

    [Fact]
    public async Task ATranslatorThatKeepsTheOldWordTimingsIsRefused()
    {
        // The one failure that produces a file which looks entirely correct: new text, old times,
        // and a player highlighting a word nobody said at that moment.
        await using var translator = new MisbehavingTranslator(MisbehavingTranslator.Fault.KeepsWords);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TranscriptTranslation.TranslateAsync(Document(Spoken(0, 3, "hola")), translator));

        Assert.Contains("lie with timestamps on it", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MisbehavingTranslator.Fault.DropsASegment, "loses transcript")]
    [InlineData(MisbehavingTranslator.Fault.MovesASegment, "belongs to the audio")]
    [InlineData(MisbehavingTranslator.Fault.ChangesTheSpeaker, "changed who said segment")]
    public async Task ATranslatorThatChangesAnythingButTheTextIsRefused(MisbehavingTranslator.Fault fault, string expected)
    {
        await using var translator = new MisbehavingTranslator(fault);
        var document = Document(Spoken(0, 3, "hola", "Speaker 1"), Spoken(3, 6, "adiós", "Speaker 1"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TranscriptTranslation.TranslateAsync(document, translator));

        Assert.Contains(expected, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATranslatorThatNeedsToldTheSourceLanguageCannotBeDriven()
    {
        // Nothing in this pipeline detects one: the transcript's language field records what was
        // requested rather than what was heard, and the hint is inert on this checkpoint. So a
        // one-to-one translator is refused up front instead of being handed a guess.
        await using var translator = new MisbehavingTranslator(MisbehavingTranslator.Fault.NeedsTheSourceLanguage);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TranscriptTranslation.TranslateAsync(Document(Spoken(0, 3, "hola")), translator));

        Assert.Contains("many-to-one", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptySegmentComesBackEmptyRatherThanDroppedOrPrefixed()
    {
        await using var translator = new FakeTranscriptTranslator();
        var document = new TranscriptDocument
        {
            Segments =
            [
                Spoken(0, 3, "hola"),
                new TranscriptSegment { Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(6), Text = "  " },
            ],
        };

        var translated = await TranscriptTranslation.TranslateAsync(document, translator);

        Assert.Equal(2, translated.Segments.Count);
        Assert.True(translated.Segments[1].IsEmpty);
    }

    [Fact]
    public async Task CancellationStopsThePassAndTheCapabilitySaysItCan()
    {
        await using var translator = new FakeTranscriptTranslator(
            new FakeTranslatorOptions { PerSegmentDelay = TimeSpan.FromMilliseconds(20) });

        Assert.True(translator.Capabilities.SupportsCancellation);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TranscriptTranslation.TranslateAsync(
                Document(Spoken(0, 3, "hola")), translator, ct: cancellation.Token));
    }

    [Fact]
    public async Task TheFakeLoadsOnceAndReportsProgressThroughTheTranslationStage()
    {
        await using var translator = new FakeTranscriptTranslator();
        var reports = new List<TranscriptionProgress>();
        var document = Document(Spoken(0, 3, "uno"), Spoken(3, 6, "dos"));

        await TranscriptTranslation.TranslateAsync(
            document, translator, progress: new SynchronousProgress(reports.Add));

        await translator.LoadAsync();
        Assert.Equal(1, translator.LoadCount);
        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(TranscriptionStage.Translating, r.Stage));
        Assert.Equal(2, reports[^1].SegmentsCompleted);
    }

    [Fact]
    public async Task TheEnglishIsMarkedInEveryFormatThatCanCarryItAndTheRestAreUnchanged()
    {
        await using var translator = new FakeTranscriptTranslator();
        var source = Document(Spoken(0, 3, "hola qué tal"));
        var translated = await TranscriptTranslation.TranslateAsync(source, translator);

        var json = TranscriptFormats.Json.Format(translated);
        Assert.Contains("\"translatedTo\": \"en\"", json, StringComparison.Ordinal);
        Assert.Contains("\"translationModel\": \"fake-translator\"", json, StringComparison.Ordinal);

        var markdown = TranscriptFormats.Markdown.Format(translated);
        Assert.Contains("| Translated into | en |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Translation model | fake-translator |", markdown, StringComparison.Ordinal);

        var vtt = TranscriptFormats.Vtt.Format(translated);
        Assert.Contains("NOTE Translated into en by fake-translator.", vtt, StringComparison.Ordinal);

        // SubRip has no comment syntax and plain text has no header, so neither says anything the
        // provenance would change: for those two the .en in the file name is the whole of the
        // marker, which is why the writer puts it there.
        var unmarked = translated with { TranslatedTo = null, TranslationModelId = null };
        Assert.Equal(TranscriptFormats.Srt.Format(unmarked), TranscriptFormats.Srt.Format(translated));
        Assert.Equal(TranscriptFormats.PlainText.Format(unmarked), TranscriptFormats.PlainText.Format(translated));

        // And an untranslated document is byte-identical to what it always was.
        Assert.DoesNotContain("translatedTo", TranscriptFormats.Json.Format(source), StringComparison.Ordinal);
        Assert.DoesNotContain("Translated into", TranscriptFormats.Markdown.Format(source), StringComparison.Ordinal);
        Assert.StartsWith("WEBVTT\n\n1\n", TranscriptFormats.Vtt.Format(source), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWordTimedAndPlainWebVttStayIdenticalOnceTheTagsAreStripped()
    {
        // The note has to be in both or the invariant that catches a misplaced word timing breaks
        // on exactly the documents the note exists for.
        var document = new TranscriptDocument
        {
            Segments = [Spoken(0, 3, "hello there")],
            TranslatedTo = "en",
            TranslationModelId = "fake-translator",
        };

        var words = TranscriptFormats.WordTimedVtt.Format(document);
        var stripped = new string(Strip(words).ToArray());

        Assert.Contains("NOTE Translated into en", words, StringComparison.Ordinal);
        Assert.Equal(TranscriptFormats.Vtt.Format(document), stripped);

        static IEnumerable<char> Strip(string vtt)
        {
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
                    yield return c;
                }
            }
        }
    }

    private sealed class SynchronousProgress : IProgress<TranscriptionProgress>
    {
        private readonly Action<TranscriptionProgress> _report;

        public SynchronousProgress(Action<TranscriptionProgress> report) => _report = report;

        public void Report(TranscriptionProgress value) => _report(value);
    }

    /// <summary>
    /// A translator that breaks exactly one clause of the contract, so the driver's refusals are
    /// held by something that really does the wrong thing rather than by a comment.
    /// </summary>
    public sealed class MisbehavingTranslator : ITranscriptTranslator
    {
        public enum Fault
        {
            KeepsWords,
            DropsASegment,
            MovesASegment,
            ChangesTheSpeaker,
            NeedsTheSourceLanguage,
        }

        private readonly Fault _fault;

        public MisbehavingTranslator(Fault fault) => _fault = fault;

        public TranslatorCapabilities Capabilities => new()
        {
            EngineName = "misbehaving",
            ModelId = "misbehaving",
            TargetToken = ">>eng<<",
            RequiresSourceLanguage = _fault == Fault.NeedsTheSourceLanguage,
            PreservesWordTimings = false,
            SupportsCancellation = true,
        };

        public ValueTask LoadAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<TranscriptSegment> TranslateAsync(
            IReadOnlyList<TranscriptSegment> segments,
            TranslationOptions options,
            IProgress<TranscriptionProgress>? progress = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();

            for (var i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];

                if (_fault == Fault.DropsASegment && i == segments.Count - 1)
                {
                    yield break;
                }

                var wellBehaved = segment with { Text = "translated", Words = [] };

                yield return _fault switch
                {
                    Fault.KeepsWords => segment with { Text = "translated" },
                    Fault.MovesASegment => wellBehaved with { Start = segment.Start + TimeSpan.FromSeconds(1) },
                    Fault.ChangesTheSpeaker => wellBehaved with { Speaker = "Speaker 9" },
                    _ => wellBehaved,
                };
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
