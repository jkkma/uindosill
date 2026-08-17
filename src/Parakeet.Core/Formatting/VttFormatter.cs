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

            // The speaker, once, in front of the first line, as plain text: WebVTT's <v> voice span
            // was considered and not used — see SubtitleOptions.SpeakerPrefixFormat. Empty when the
            // document carries no speakers, so an unlabelled transcript is byte-identical to before.
            var prefix = options.Subtitles.SpeakerPrefix(cue.Speaker);
            for (var line = 0; line < cue.Lines.Count; line++)
            {
                if (line == 0)
                {
                    builder.Append(prefix);
                }

                // A cue payload line may not start with "-->" and a blank line ends the cue.
                var text = cue.Lines[line];
                var blank = text.Length == 0 && (line > 0 || prefix.Length == 0);
                builder.Append(blank ? " " : text).Append(options.NewLine);
            }

            builder.Append(options.NewLine);
        }

        return builder.ToString();
    }
}
