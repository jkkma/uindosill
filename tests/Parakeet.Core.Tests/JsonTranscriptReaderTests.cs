using Parakeet.Core.Diarisation;
using Parakeet.Core.Formatting;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class JsonTranscriptReaderTests
{
    private static TranscriptDocument Document() => new()
    {
        SourceName = "meeting.wav",
        AudioDuration = TimeSpan.FromSeconds(12),
        ModelId = "parakeet-tdt-0.6b-v3-q8_0",
        Quantisation = "q8_0",
        Backend = ComputeBackend.Vulkan,
        Language = "de",
        ProcessingTime = TimeSpan.FromSeconds(1.2),
        DecodeTime = TimeSpan.FromSeconds(0.9),
        SpeechDetector = "energy gate",
        SpeakerModelId = "sortformer-4spk-v2.1",
        SpeakerBackend = ComputeBackend.WebGpu,
        RequestedSpeakerCount = 2,
        SpeakerFolds =
        [
            new SpeakerFold { Dropped = "SPEAKER_02", Kept = "SPEAKER_00", OverlapSeconds = 0.4, RunnerUpSeconds = 57.6 },
            new SpeakerFold { Dropped = "SPEAKER_03", Kept = "SPEAKER_01", OverlapSeconds = 1.25, RunnerUpSeconds = null },
        ],
        SpeakerTurns =
        [
            new SpeakerTurn { Start = TimeSpan.FromSeconds(0.5), End = TimeSpan.FromSeconds(3), Speaker = "SPEAKER_00" },
            new SpeakerTurn { Start = TimeSpan.FromSeconds(5), End = TimeSpan.FromSeconds(8), Speaker = "SPEAKER_01" },
        ],
        TranslatedTo = "en",
        TranslationModelId = "opus-mt",
        TranslationBackend = ComputeBackend.Cuda,
        TranslationDecode = "beam 6, at most 512 new tokens, length penalty 1, early stopping off",
        Segments =
        [
            new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(0.5),
                End = TimeSpan.FromSeconds(3),
                Text = "first thing we should do",
                Speaker = "Speaker 1",
                Words =
                [
                    new TranscriptWord { Text = "first", Start = TimeSpan.FromSeconds(0.5), End = TimeSpan.FromSeconds(0.9), Confidence = 0.95f, Speaker = "Speaker 1" },
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
    public void ReadingWhatTheFormatterWroteReproducesTheDocument()
    {
        var original = Document();
        var read = JsonTranscriptReader.Read(TranscriptFormats.Json.Format(original));

        // Field-by-field rather than one record comparison: the document's list properties
        // compare by reference, so record equality would say "different" about two documents
        // that agree on everything. The byte-stability test below is the complete check; these
        // are the readable half of it.
        Assert.Equal(original.SourceName, read.SourceName);
        Assert.Equal(original.ModelId, read.ModelId);
        Assert.Equal(original.Quantisation, read.Quantisation);
        Assert.Equal(original.Backend, read.Backend);
        Assert.Equal(original.Language, read.Language);
        Assert.Equal(original.AudioDuration, read.AudioDuration);
        Assert.Equal(original.ProcessingTime, read.ProcessingTime);
        Assert.Equal(original.DecodeTime, read.DecodeTime);
        Assert.Equal(original.SpeechDetector, read.SpeechDetector);
        Assert.Equal(original.SpeakerModelId, read.SpeakerModelId);
        Assert.Equal(original.SpeakerBackend, read.SpeakerBackend);
        Assert.Equal(original.RequestedSpeakerCount, read.RequestedSpeakerCount);
        Assert.Equal(original.TranslatedTo, read.TranslatedTo);
        Assert.Equal(original.TranslationModelId, read.TranslationModelId);
        Assert.Equal(original.TranslationBackend, read.TranslationBackend);
        Assert.Equal(original.TranslationDecode, read.TranslationDecode);

        Assert.Equal(original.SpeakerFolds, read.SpeakerFolds);
        Assert.Equal(original.SpeakerTurns, read.SpeakerTurns);

        Assert.Equal(original.Segments.Count, read.Segments.Count);
        foreach (var (expected, actual) in original.Segments.Zip(read.Segments))
        {
            Assert.Equal(expected.Start, actual.Start);
            Assert.Equal(expected.End, actual.End);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.Speaker, actual.Speaker);
            Assert.Equal(expected.Words, actual.Words);
        }
    }

    [Fact]
    public void FormatReadFormatIsByteStable()
    {
        // The stronger property, and the one the export pin needs: a transcript reopened and
        // rewritten must hash identically, or a pin computed after a round trip would refuse
        // the very transcript it was computed from.
        var first = TranscriptFormats.Json.Format(Document());
        var second = TranscriptFormats.Json.Format(JsonTranscriptReader.Read(first));

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task TheFakeEnginesOutputSurvivesTheRoundTrip()
    {
        // The plan's named exit: a real pipeline product — real segmentation, real timings —
        // not a hand-built document.
        var audio = new ArrayAudioSource(TestAudio.Build((0.5, false), (2, true), (1.2, false), (2, true), (0.5, false)));
        await using var engine = new FakeTranscriptionEngine();
        var document = await TranscriptionRunner.RunAsync(engine, audio, sourceName: "fake.wav");

        Assert.NotEmpty(document.Segments);

        var json = TranscriptFormats.Json.Format(document);
        var read = JsonTranscriptReader.Read(json);

        Assert.Equal(document.Segments.Count, read.Segments.Count);
        Assert.Equal(document.SourceName, read.SourceName);
        Assert.Equal(document.Text, read.Text);

        // Byte-stability holds from the file, not from the live document: the file's stated
        // resolution is a millisecond, a live document's ticks are finer, and the first write is
        // where that precision is shed — a processingSec of 2.8 ms writes as 0.003, and the
        // realTimeFactor recomputed from the rounded figure differs in its last digit from one
        // computed from raw ticks. What the export pin needs is that a *file* reopened and
        // rewritten hashes identically, and that is the fixpoint asserted here.
        var rewritten = TranscriptFormats.Json.Format(read);
        Assert.Equal(rewritten, TranscriptFormats.Json.Format(JsonTranscriptReader.Read(rewritten)));
    }

    [Fact]
    public void TimesRoundTripExactly()
    {
        // 1.234 has no exact double; a reader that went through TimeSpan.FromSeconds(double)
        // could land a tick off and shift a citation's rendered time. The decimal path must not.
        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.FromMilliseconds(1234), End = TimeSpan.FromMilliseconds(5678), Text = "x" },
            ],
        };

        var read = JsonTranscriptReader.Read(TranscriptFormats.Json.Format(document));

        Assert.Equal(TimeSpan.FromMilliseconds(1234), read.Segments[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(5678), read.Segments[0].End);
    }

    [Fact]
    public void AnEmptyTranscriptReads()
    {
        var read = JsonTranscriptReader.Read(TranscriptFormats.Json.Format(TranscriptDocument.Empty));

        Assert.Empty(read.Segments);
        Assert.True(read.IsEmpty);
    }

    [Fact]
    public void AMetadataFreeFileReads()
    {
        // IncludeMetadata = false writes only text and segments; both halves of the format are
        // files this product has produced, so both must reopen.
        var bare = TranscriptFormats.Json.Format(
            Document(), TranscriptFormatOptions.Default with { IncludeMetadata = false });

        var read = JsonTranscriptReader.Read(bare);

        Assert.Equal(2, read.Segments.Count);
        Assert.Null(read.ModelId);
        Assert.Null(read.Backend);
    }

    [Fact]
    public void UnknownPropertiesAreSkipped()
    {
        // The formatter has grown fields over its life and will again; a reader that refused a
        // newer file would punish exactly the transcript it exists to reopen.
        const string json = """
            {
              "someFutureField": {"nested": [1, 2]},
              "text": "hello",
              "segments": [
                {"start": 0.5, "end": 1.0, "text": "hello", "futureFlag": true}
              ]
            }
            """;

        var read = JsonTranscriptReader.Read(json);

        Assert.Single(read.Segments);
        Assert.Equal("hello", read.Segments[0].Text);
    }

    [Fact]
    public void DerivedValuesAreRecomputedNotRead()
    {
        // A file whose realTimeFactor disagrees with its own durations cannot smuggle the
        // disagreement in: the document recomputes it from what is.
        const string json = """
            {
              "audioDurationSec": 10.0,
              "processingSec": 1.0,
              "realTimeFactor": 99.0,
              "text": "does not match the segments either",
              "segments": [
                {"start": 0, "end": 1.0, "text": "what the segments say"}
              ]
            }
            """;

        var read = JsonTranscriptReader.Read(json);

        Assert.Equal(0.1, read.RealTimeFactor!.Value, 6);
        Assert.Equal("what the segments say", read.Text);
    }

    [Fact]
    public void MissingSegmentsRefusesLoudly()
    {
        var ex = Assert.Throws<FormatException>(() => JsonTranscriptReader.Read("""{"text": "no segments"}"""));
        Assert.Contains("segments", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASegmentWithoutTimesRefusesLoudlyAndNamesTheSegment()
    {
        var ex = Assert.Throws<FormatException>(() => JsonTranscriptReader.Read(
            """{"segments": [{"start": 0, "end": 1, "text": "fine"}, {"text": "no times"}]}"""));

        Assert.Contains("segment 1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonJsonRefusesLoudly() =>
        Assert.Throws<FormatException>(() => JsonTranscriptReader.Read("WEBVTT\n\n00:00:00.500 --> ..."));

    [Fact]
    public void AnUnknownBackendRefusesRatherThanDroppingProvenance()
    {
        var ex = Assert.Throws<FormatException>(() => JsonTranscriptReader.Read(
            """{"backend": "quantum", "segments": []}"""));

        Assert.Contains("quantum", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryBackendSpellingTheFormatterWritesParsesBack()
    {
        foreach (var backend in Enum.GetValues<ComputeBackend>())
        {
            var document = TranscriptDocument.Empty with { Backend = backend };
            var read = JsonTranscriptReader.Read(TranscriptFormats.Json.Format(document));
            Assert.Equal(backend, read.Backend);
        }
    }
}
