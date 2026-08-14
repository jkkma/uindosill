using System.Diagnostics.CodeAnalysis;

namespace Parakeet.Core.Formatting;

/// <summary>The output formats the product supports, looked up by id or extension.</summary>
public static class TranscriptFormats
{
    public static ITranscriptFormatter PlainText { get; } = new PlainTextFormatter();

    public static ITranscriptFormatter Srt { get; } = new SrtFormatter();

    public static ITranscriptFormatter Vtt { get; } = new VttFormatter();

    public static ITranscriptFormatter WordTimedVtt { get; } = new WordTimedVttFormatter();

    public static ITranscriptFormatter Json { get; } = new JsonTranscriptFormatter();

    public static ITranscriptFormatter Markdown { get; } = new MarkdownFormatter();

    public static IReadOnlyList<ITranscriptFormatter> All { get; } =
        [PlainText, Srt, Vtt, WordTimedVtt, Json, Markdown];

    public static IReadOnlyList<string> Ids { get; } = [.. All.Select(f => f.Id)];

    public static bool TryGet(string id, [NotNullWhen(true)] out ITranscriptFormatter? formatter)
    {
        formatter = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var normalised = id.Trim().TrimStart('.').ToLowerInvariant();
        normalised = normalised switch
        {
            "text" or "plain" => "txt",
            "markdown" => "md",
            "webvtt" => "vtt",
            "webvtt-words" or "words" => "vtt-words",
            "subrip" => "srt",
            _ => normalised,
        };

        formatter = All.FirstOrDefault(f => f.Id == normalised);
        return formatter is not null;
    }

    public static ITranscriptFormatter Get(string id) =>
        TryGet(id, out var formatter)
            ? formatter
            : throw new ArgumentException(
                $"Unknown transcript format '{id}'. Known formats: {string.Join(", ", Ids)}.", nameof(id));
}
