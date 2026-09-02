using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>
/// Machine-readable transcript with timestamps. Written with <see cref="Utf8JsonWriter"/>
/// rather than a serialiser so the property order and number rounding are part of the
/// contract instead of an artefact of reflection.
/// </summary>
public sealed class JsonTranscriptFormatter : ITranscriptFormatter
{
    public string Id => "json";

    public string DisplayName => "JSON with timestamps";

    public string FileExtension => ".json";

    public string Format(TranscriptDocument document, TranscriptFormatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptFormatOptions.Default;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            if (options.IncludeMetadata)
            {
                WriteStringOrNull(writer, "source", document.SourceName);
                WriteStringOrNull(writer, "model", document.ModelId);
                WriteStringOrNull(writer, "quantisation", document.Quantisation);
                WriteStringOrNull(writer, "backend", document.Backend?.ToString().ToLowerInvariant());
                WriteStringOrNull(writer, "language", document.Language);
                WriteSecondsOrNull(writer, "audioDurationSec", document.AudioDuration);
                WriteSecondsOrNull(writer, "processingSec", document.ProcessingTime);

                if (document.RealTimeFactor is { } rtf)
                {
                    writer.WriteNumber("realTimeFactor", Round(rtf, 4));
                }

                // The model's own share of processingSec, when the engine measured it: the read and
                // the segmentation are inside the wall figure and not inside this one. Absent from a
                // document that predates it or an engine that does not time itself.
                WriteSecondsOrNull(writer, "decodeSec", document.DecodeTime);

                if (document.DecodeRealTimeFactor is { } decodeRtf)
                {
                    writer.WriteNumber("decodeRealTimeFactor", Round(decodeRtf, 4));
                }

                // What cut the recording into the pieces the model decoded — the gate, a neural
                // detector with its runtime, or fixed windows. Present only when the engine
                // reported it, so a transcript written before 2026-08-23 serialises as it did;
                // absent, the cut was the gate's or --no-vad's, and the flags that made it are the
                // only record.
                if (document.SpeechDetector is { } speechDetector)
                {
                    writer.WriteString("speechDetector", speechDetector);
                }

                // Present only when a labeller ran, like every speaker field below: a transcript made
                // without the opt-in serialises exactly as it did before the field existed.
                if (document.SpeakerModelId is { } speakerModel)
                {
                    writer.WriteString("speakerModel", speakerModel);
                }

                // Which provider produced those labels, not merely which model was loaded. The two
                // are separate answers here in a way they are not for the ASR engine above: the
                // diariser's device is resolved inside the sidecar. That providers *can* disagree
                // was measured on the diariser retired 2026-08-27 — AMI test scoring 16.3324% DER on
                // the CPU, 16.3319% on WebGPU and 16.1021% on CUDA — and whether the pipeline that
                // replaced it has the same property is unmeasured, which is why the field is still
                // written rather than dropped.
                if (document.SpeakerBackend is { } speakerBackend)
                {
                    writer.WriteString("speakerBackend", speakerBackend.ToString().ToLowerInvariant());
                }

                // And which runtime, when the labeller has more than one. The second diariser's
                // torch embedder and ONNX Runtime's CPU provider both write "cpu" above and do not
                // produce the same labels, so the line before this one is ambiguous without it.
                if (document.SpeakerEmbeddingBackend is { Length: > 0 } speakerEmbeddingBackend)
                {
                    writer.WriteString("speakerEmbeddingBackend", speakerEmbeddingBackend);
                }

                // What the caller asked for, beside what produced it — and present even when it
                // changed nothing, because that is the case it exists for. A run made with
                // --speaker-count 2 that the model had already satisfied merges nothing, and
                // without this it archives byte-for-byte identical to a run given no count at all.
                if (document.RequestedSpeakerCount is { } requestedSpeakers)
                {
                    writer.WriteNumber("requestedSpeakerCount", requestedSpeakers);
                }

                // And what honouring it cost, which is an edit made to the model's output rather
                // than a fact about what produced it: two labels the diariser kept apart, joined
                // because a number was supplied. Each merge carries the evidence it was made on —
                // "overlapSec" near zero is one voice that drifted onto a second label, while a
                // large one whose "runnerUpSec" is barely larger is two people the count has just
                // put under one name. Both labels are the labeller's own cluster ids, as they were
                // before display renaming: "from" names something that no longer exists in
                // "speakerTurns" below, its turns having been moved under "into".
                if (document.SpeakerFolds.Count > 0)
                {
                    writer.WriteStartArray("speakerFolds");
                    foreach (var fold in document.SpeakerFolds)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("from", fold.Dropped);
                        writer.WriteString("into", fold.Kept);
                        writer.WriteNumber("overlapSec", Round(fold.OverlapSeconds, 3));

                        // Explicitly null rather than absent when the merged pair was the only pair
                        // there was: "there was nothing to compare it with" is a different answer
                        // to a reader than a key they have to guess the meaning of missing.
                        if (fold.RunnerUpSeconds is { } runnerUp)
                        {
                            writer.WriteNumber("runnerUpSec", Round(runnerUp, 3));
                        }
                        else
                        {
                            writer.WriteNull("runnerUpSec");
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                // Likewise present only when a translation pass ran. "text" and every segment below
                // are the English in that case, and these two fields are the only thing that says so.
                if (document.TranslatedTo is { } target)
                {
                    writer.WriteString("translatedTo", target);
                }

                if (document.TranslationModelId is { } translationModel)
                {
                    writer.WriteString("translationModel", translationModel);
                }

                // And which provider wrote it, for the same reason and with a sharper edge: a
                // translator that diverges does not shift a score, it returns different sentences.
                if (document.TranslationBackend is { } translationBackend)
                {
                    writer.WriteString("translationBackend", translationBackend.ToString().ToLowerInvariant());
                }

                // And the search over the graph — beam, length cap, penalty, early stopping — because
                // the graphs are pinned and the search is not, and a transcript that names the
                // checkpoint has named half of what produced its English.
                if (document.TranslationDecode is { } translationDecode)
                {
                    writer.WriteString("translationDecode", translationDecode);
                }

                // Present only when the tidy ran. The words below are spoken words in spoken
                // order except where a word carries "replacedFrom", and these fields are what
                // says a second model edited the text at all.
                if (document.TidyModelId is { } tidyModel)
                {
                    writer.WriteString("tidyModel", tidyModel);
                }

                if (document.TidyBackend is { } tidyBackend)
                {
                    writer.WriteString("tidyBackend", tidyBackend.ToString().ToLowerInvariant());
                }

                if (document.TidyRefusedSegments is { } tidyRefused)
                {
                    writer.WriteNumber("tidyRefusedSegments", tidyRefused);
                }
            }

            writer.WriteString("text", document.Text);

            writer.WriteStartArray("segments");
            foreach (var segment in document.Segments)
            {
                writer.WriteStartObject();
                writer.WriteNumber("start", Seconds(segment.Start));
                writer.WriteNumber("end", Seconds(segment.End));
                writer.WriteString("text", segment.Text);

                if (segment.Speaker is { } segmentSpeaker)
                {
                    writer.WriteString("speaker", segmentSpeaker);
                }

                if (segment.MeanConfidence is { } confidence)
                {
                    writer.WriteNumber("conf", Round(confidence, 4));
                }

                if (segment.Words.Count > 0)
                {
                    writer.WriteStartArray("words");
                    foreach (var word in segment.Words)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("w", word.Text);
                        writer.WriteNumber("start", Seconds(word.Start));
                        writer.WriteNumber("end", Seconds(word.End));
                        if (word.Confidence is { } wordConfidence)
                        {
                            writer.WriteNumber("conf", Round(wordConfidence, 4));
                        }

                        if (word.Speaker is { } wordSpeaker)
                        {
                            writer.WriteString("speaker", wordSpeaker);
                        }

                        // The one place a tidied word may not be what was said, and the reader
                        // is told which and what was said instead.
                        if (word.ReplacedFrom is { } replacedFrom)
                        {
                            writer.WriteString("replacedFrom", replacedFrom);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            // The labeller's own output, distinct from the per-word attribution above: what an RTTM
            // file carries and what a diarisation scorer reads.
            if (document.SpeakerTurns.Count > 0)
            {
                writer.WriteStartArray("speakerTurns");
                foreach (var turn in document.SpeakerTurns)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("start", Seconds(turn.Start));
                    writer.WriteNumber("end", Seconds(turn.End));
                    writer.WriteString("speaker", turn.Speaker);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return options.NewLine == "\n" ? json : json.ReplaceLineEndings(options.NewLine);
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteSecondsOrNull(Utf8JsonWriter writer, string name, TimeSpan? value)
    {
        if (value is { } span)
        {
            writer.WriteNumber(name, Seconds(span));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    // Three decimals, matching the millisecond resolution the engine reports. Anything
    // beyond that is noise dressed up as precision.
    private static decimal Seconds(TimeSpan value) => Round(value.TotalSeconds, 3);

    private static decimal Round(double value, int digits) =>
        Math.Round((decimal)value, digits, MidpointRounding.AwayFromZero);

    private static decimal Round(float value, int digits) =>
        Math.Round((decimal)value, digits, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The writer's three-decimal seconds in the pin's rendering — <c>0.######</c>, so no
    /// trailing zeros — which is how <c>scripts/measure-answers.ps1</c> re-renders the JSON it
    /// hashes. <see cref="Transcription.TranscriptDocument.SegmentsSha256"/> hashes through this
    /// method so the in-memory document and its JSON export hash identically: tick-exact times
    /// hashed directly disagreed with the exported three-decimal ones on any segment boundary
    /// off the millisecond grid, which the last segment of nearly every real recording is
    /// (found 2026-08-30).
    /// </summary>
    internal static string FormatSeconds(TimeSpan value) =>
        Seconds(value).ToString("0.######", CultureInfo.InvariantCulture);
}
