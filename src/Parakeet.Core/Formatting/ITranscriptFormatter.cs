using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

public sealed record TranscriptFormatOptions
{
    public static TranscriptFormatOptions Default { get; } = new();

    /// <summary>
    /// Line ending. LF by default so output is byte-identical everywhere and diffable;
    /// every subtitle player and text editor Windows ships handles it.
    /// </summary>
    public string NewLine { get; init; } = "\n";

    public SubtitleOptions Subtitles { get; init; } = SubtitleOptions.Default;

    /// <summary>Prefix each paragraph with its start time in plain text and Markdown.</summary>
    public bool IncludeTimestamps { get; init; } = true;

    /// <summary>Include the provenance header in Markdown and JSON.</summary>
    public bool IncludeMetadata { get; init; } = true;
}

public interface ITranscriptFormatter
{
    /// <summary>Stable lowercase identifier used on the command line, e.g. <c>srt</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>File extension including the dot.</summary>
    string FileExtension { get; }

    string Format(TranscriptDocument document, TranscriptFormatOptions? options = null);
}
