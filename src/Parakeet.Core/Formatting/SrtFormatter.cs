using System.Globalization;
using System.Text;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>SubRip subtitles.</summary>
public sealed class SrtFormatter : ITranscriptFormatter
{
    public string Id => "srt";

    public string DisplayName => "SubRip subtitles";

    public string FileExtension => ".srt";

    public string Format(TranscriptDocument document, TranscriptFormatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptFormatOptions.Default;

        var cues = SubtitleCueBuilder.Build(document.Segments, options.Subtitles);
        var builder = new StringBuilder();

        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(options.NewLine);
            builder.Append(Timecode.ToSrt(cue.Start))
                   .Append(" --> ")
                   .Append(Timecode.ToSrt(cue.End))
                   .Append(options.NewLine);

            foreach (var line in cue.Lines)
            {
                builder.Append(line).Append(options.NewLine);
            }

            builder.Append(options.NewLine);
        }

        return builder.ToString();
    }
}
