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

        builder.Append(Header(document, options.NewLine));

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

    /// <summary>
    /// <c>WEBVTT</c>, and a <c>NOTE</c> saying so when the cues are a translation rather than what
    /// was said.
    /// </summary>
    /// <remarks>
    /// WebVTT has a comment syntax, so it can carry the marker in-band; SubRip has none and is
    /// covered by the <c>.en</c> infix in its name instead, as plain text is. Shared with the
    /// word-timed WebVTT formatter rather than written twice, because those two outputs are held
    /// byte-identical to each other once the timing tags are stripped, and a header in one of them
    /// only would break that on exactly the documents this note exists for. It carries no markup,
    /// for the same reason.
    /// </remarks>
    internal static string Header(TranscriptDocument document, string newLine)
    {
        var builder = new StringBuilder();
        builder.Append("WEBVTT").Append(newLine).Append(newLine);

        if (document.TranslatedTo is { } target)
        {
            var by = document.TranslationModelId is { } model ? $" by {model}" : string.Empty;
            builder.Append("NOTE Translated into ").Append(target).Append(by)
                   .Append(". The text is a translation of the speech; the times are the speech.")
                   .Append(newLine).Append(newLine);
        }

        if (document.TidyModelId is { } tidyModel)
        {
            builder.Append("NOTE Tidied by ").Append(tidyModel)
                   .Append(". Fillers and false starts were taken out; every word kept is a spoken word, in spoken order.")
                   .Append(newLine).Append(newLine);
        }

        return builder.ToString();
    }
}
