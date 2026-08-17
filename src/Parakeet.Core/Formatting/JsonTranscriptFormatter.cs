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

                // Present only when a labeller ran, like every speaker field below: a transcript made
                // without the opt-in serialises exactly as it did before the field existed.
                if (document.SpeakerModelId is { } speakerModel)
                {
                    writer.WriteString("speakerModel", speakerModel);
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

    internal static string FormatSeconds(TimeSpan value) =>
        Seconds(value).ToString(CultureInfo.InvariantCulture);
}
