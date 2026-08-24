using System.Globalization;
using System.Text;
using System.Text.Json;
using Parakeet.Core.Formatting;
using Parakeet.Core.Retrieval;

namespace Parakeet.Cli;

/// <summary>
/// Searches a transcript the way the Ask panel does — the same window builder, the same
/// tokenizer, the same BM25 — and prints the ranked windows with their citation ids. This is the
/// verb that lets <c>scripts/measure-answers.ps1</c> measure recall@k against the product's own
/// index instead of a reimplementation of it, which is the whole reason it exists: a recall
/// figure from any other tokenizer would measure the wrong thing.
/// </summary>
internal static class RetrieveCommand
{
    public static int Run(CliContext context, ParsedCommandLine parsed)
    {
        if (parsed.Positionals.Count != 1)
        {
            context.WriteError("retrieve needs exactly one transcript: the .json this tool wrote.");
            return ExitCodes.UsageError;
        }

        var transcriptPath = parsed.Positionals[0];
        if (!File.Exists(transcriptPath))
        {
            context.WriteError($"Transcript not found: {transcriptPath}");
            return ExitCodes.UsageError;
        }

        var questions = parsed.Values("question");
        if (questions.Count == 0)
        {
            context.WriteError("retrieve needs at least one --question.");
            return ExitCodes.UsageError;
        }

        var top = 10;
        if (parsed.Value("top") is { Length: > 0 } topText)
        {
            if (!int.TryParse(topText, NumberStyles.Integer, CultureInfo.InvariantCulture, out top) || top < 1 || top > 1000)
            {
                context.WriteError($"--top needs an integer between 1 and 1000, got '{topText}'.");
                return ExitCodes.UsageError;
            }
        }

        Core.Transcription.TranscriptDocument document;
        try
        {
            document = JsonTranscriptReader.Read(File.ReadAllText(transcriptPath));
        }
        catch (FormatException ex)
        {
            throw new CliUsageException($"{transcriptPath}: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new CliUsageException($"{transcriptPath}: {ex.Message}", ex);
        }

        var options = parsed.HasFlag("wide") ? TranscriptWindowOptions.Wide : TranscriptWindowOptions.Default;
        var windows = TranscriptWindowBuilder.Build(document, options);
        var retriever = new Bm25Retriever(windows);

        // An empty hit list is a real answer — it is what the abstain path is made of — so a
        // question nothing matches still succeeds; the caller reads the empty array.
        var results = new List<(string Question, IReadOnlyList<RetrievalHit> Hits)>();
        foreach (var question in questions)
        {
            results.Add((question, retriever.Retrieve(question, top)));
        }

        if (parsed.HasFlag("json"))
        {
            WriteJson(context, transcriptPath, document.Segments.Count, windows.Count, options, top, results);
        }
        else
        {
            WriteText(context, document.Segments.Count, windows.Count, options, results);
        }

        return ExitCodes.Success;
    }

    private static void WriteJson(
        CliContext context,
        string transcriptPath,
        int segmentCount,
        int windowCount,
        TranscriptWindowOptions options,
        int top,
        List<(string Question, IReadOnlyList<RetrievalHit> Hits)> results)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("transcript", Path.GetFullPath(transcriptPath));
            writer.WriteNumber("segments", segmentCount);
            writer.WriteNumber("windows", windowCount);
            writer.WriteNumber("windowSeconds", options.WindowLength.TotalSeconds);
            writer.WriteNumber("strideSeconds", options.Stride.TotalSeconds);
            writer.WriteNumber("top", top);
            writer.WriteStartArray("results");
            foreach (var (question, hits) in results)
            {
                writer.WriteStartObject();
                writer.WriteString("question", question);
                writer.WriteStartArray("hits");
                for (var rank = 0; rank < hits.Count; rank++)
                {
                    var hit = hits[rank];
                    writer.WriteStartObject();
                    writer.WriteNumber("rank", rank + 1);
                    writer.WriteString("citation", hit.Window.CitationId);
                    writer.WriteNumber("firstSegment", hit.Window.FirstSegment);
                    writer.WriteNumber("lastSegment", hit.Window.LastSegment);
                    writer.WriteNumber("startSec", Math.Round(hit.Window.Start.TotalSeconds, 6));
                    writer.WriteNumber("endSec", Math.Round(hit.Window.End.TotalSeconds, 6));
                    writer.WriteNumber("score", Math.Round(hit.Score, 6));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        context.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteText(
        CliContext context,
        int segmentCount,
        int windowCount,
        TranscriptWindowOptions options,
        List<(string Question, IReadOnlyList<RetrievalHit> Hits)> results)
    {
        context.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{segmentCount} segments in {windowCount} windows of ~{options.WindowLength.TotalSeconds:0} s at {options.Stride.TotalSeconds:0} s stride"));
        foreach (var (question, hits) in results)
        {
            context.WriteLine();
            context.WriteLine($"? {question}");
            if (hits.Count == 0)
            {
                context.WriteLine("  no window matches — the abstain path's input, not an error");
                continue;
            }

            for (var rank = 0; rank < hits.Count; rank++)
            {
                var hit = hits[rank];
                var preview = hit.Window.Text.Length > 80 ? hit.Window.Text[..80] + "…" : hit.Window.Text;
                context.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {rank + 1,2}. {hit.Window.CitationId,-12} [{Clock(hit.Window.Start)}–{Clock(hit.Window.End)}]  {hit.Score,8:F3}  {preview}"));
            }
        }
    }

    private static string Clock(TimeSpan time) => time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
