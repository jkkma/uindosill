using System.Globalization;
using System.Text;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>WebVTT subtitles.</summary>
public sealed class VttFormatter : ITranscriptFormatter
{
    public string Id => "vtt";

    public string DisplayName => "WebVTT subtitles";

    public string FileExtension => ".vtt";

    public string Format(TranscriptDocument document, TranscriptFormatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptFormatOptions.Default;

        var cues = SubtitleCueBuilder.Build(document.Segments, options.Subtitles);
        var builder = new StringBuilder();

        builder.Append("WEBVTT").Append(options.NewLine).Append(options.NewLine);

        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(options.NewLine);
            builder.Append(Timecode.ToVtt(cue.Start))
                   .Append(" --> ")
                   .Append(Timecode.ToVtt(cue.End))
                   .Append(options.NewLine);

            foreach (var line in cue.Lines)
            {
                // A cue payload line may not start with "-->" and a blank line ends the cue.
                builder.Append(line.Length == 0 ? " " : line).Append(options.NewLine);
            }

            builder.Append(options.NewLine);
        }

        return builder.ToString();
    }
}
