using System.Text.Json;
using Parakeet.Core.Tidying;

namespace Parakeet.Cli;

/// <summary>
/// Writes <c>--tidy-trace</c>: every request the tidy stage made, on the stage's own clock, with
/// the moments the plain transcript and the tidied version were complete. The pace measurement
/// of docs/PHASES.md (*Decided 2026-09-02, late evening*) reads it; nothing shipped does.
/// </summary>
internal static class TidyTraceWriter
{
    public static async Task WriteAsync(
        string path,
        TidyStage stage,
        TidyOptions options,
        int segments,
        TimeSpan transcriptCompleteAt,
        TimeSpan? tidyCompleteAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(options);

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("shape", options.Shape == TidyShape.Pass ? "pass" : "tandem");
            writer.WriteString("unit", options.Unit switch
            {
                TidyUnitKind.JoinedRun => "run",
                TidyUnitKind.SentenceRun => "sentence",
                _ => "segment",
            });
            writer.WriteNumber("concurrency", options.Concurrency);
            writer.WriteNumber("segments", segments);
            writer.WriteNumber("units", stage.Units);
            writer.WriteNumber("transcriptCompleteSec", Seconds(transcriptCompleteAt));
            if (tidyCompleteAt is { } done)
            {
                writer.WriteNumber("tidyCompleteSec", Seconds(done));
            }
            else
            {
                writer.WriteNull("tidyCompleteSec");
            }

            writer.WriteStartArray("requests");
            foreach (var request in stage.Trace.OrderBy(t => t.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", request.Ordinal);
                writer.WriteStartArray("segments");
                foreach (var index in request.Segments)
                {
                    writer.WriteNumberValue(index);
                }

                writer.WriteEndArray();
                writer.WriteNumber("pieces", request.Pieces);
                writer.WriteNumber("words", request.Words);
                writer.WriteNumber("speechSec", Seconds(request.Speech));
                writer.WriteNumber("enqueuedSec", Seconds(request.EnqueuedAt));
                writer.WriteNumber("startedSec", Seconds(request.StartedAt));
                writer.WriteNumber("landedSec", Seconds(request.LandedAt));
                writer.WriteBoolean("accepted", request.Accepted);
                if (request.Refusal is { } why)
                {
                    writer.WriteString("refusal", why);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, buffer.ToArray(), ct).ConfigureAwait(false);
    }

    private static double Seconds(TimeSpan span) => Math.Round(span.TotalSeconds, 3);
}
