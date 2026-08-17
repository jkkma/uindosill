using System.Text;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>Readable plain text: one paragraph per segment, optionally timestamped, named when speakers are known.</summary>
public sealed class PlainTextFormatter : ITranscriptFormatter
{
    public string Id => "txt";

    public string DisplayName => "Plain text";

    public string FileExtension => ".txt";

    public string Format(TranscriptDocument document, TranscriptFormatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptFormatOptions.Default;

        var builder = new StringBuilder();
        foreach (var segment in document.Segments)
        {
            if (segment.IsEmpty)
            {
                continue;
            }

            if (options.IncludeTimestamps)
            {
                builder.Append('[').Append(Timecode.ToClock(segment.Start)).Append("] ");
            }

            // "Speaker 1: " when a labeller ran; nothing when it did not, so the output is unchanged.
            if (segment.Speaker is { } speaker)
            {
                builder.Append(speaker).Append(": ");
            }

            builder.Append(segment.Text.Trim()).Append(options.NewLine);
        }

        return builder.ToString();
    }
}
