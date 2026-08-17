using System.Globalization;
using System.Text;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>
/// WebVTT subtitles carrying an inline timestamp per word, which is what a player needs to
/// highlight the word being spoken inside a line of context.
/// </summary>
/// <remarks>
/// <para>
/// The payload is the plain <see cref="VttFormatter"/> payload with tags inserted into it and
/// nothing else changed: strip every <c>&lt;…&gt;</c> from this output and it is byte-identical to
/// the <c>vtt</c> output for the same document, header and cue numbering included. A test asserts
/// it, and it is the invariant that catches a word landing against the wrong line after a wrap —
/// the one failure in this format that produces a file which looks entirely correct.
/// </para>
/// <para>
/// Each word is wrapped in a <c>&lt;c&gt;</c> span. That is not decoration and it is not optional:
/// the WebVTT specification's own styling table records that bare text between timestamps matches
/// neither <c>::cue(:past)</c> nor <c>::cue(:future)</c> — its example is annotated <i>"No match
/// (no elements)"</i> — because those pseudo-classes select elements and a run of text between two
/// timestamps is not one. Without a span per word the highlight this format exists for cannot be
/// styled at all.
/// </para>
/// <para>
/// Nothing here escapes <c>&amp;</c> or <c>&lt;</c>, because <see cref="VttFormatter"/> does not
/// either and these two outputs have to stay comparable. A transcript word containing either
/// character would already corrupt the plain <c>vtt</c> file; it is a property of that formatter,
/// not something introduced here.
/// </para>
/// </remarks>
public sealed class WordTimedVttFormatter : ITranscriptFormatter
{
    public string Id => "vtt-words";

    public string DisplayName => "WebVTT subtitles with word timings";

    /// <summary>
    /// Deliberately not <c>.vtt</c>. Asking for <c>-f vtt,vtt-words</c> with one extension between
    /// them collides in <c>TranscriptWriter.ResolvePath</c>, and under the default rename policy
    /// the second one lands as <c>name (2).vtt</c> with nothing to say which is which.
    /// </summary>
    public string FileExtension => ".words.vtt";

    public string Format(TranscriptDocument document, TranscriptFormatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptFormatOptions.Default;

        var cues = SubtitleCueBuilder.Build(document.Segments, options.Subtitles);
        var builder = new StringBuilder();

        // Plain "WEBVTT" with no header text, so the strip-the-tags comparison against the vtt
        // output covers the whole file rather than only the cue payloads. What this file is gets
        // said by its name.
        builder.Append("WEBVTT").Append(options.NewLine).Append(options.NewLine);

        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            builder.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(options.NewLine);
            builder.Append(Timecode.ToVtt(cue.Start))
                   .Append(" --> ")
                   .Append(Timecode.ToVtt(cue.End))
                   .Append(options.NewLine);

            // The speaker prefix is bare text before the first span, in both branches, exactly where
            // VttFormatter puts it — so stripping every tag from this output still reproduces the
            // plain vtt byte for byte. Bare text matches neither :past nor :future, which is right:
            // a name is not spoken, so it never lights up.
            var prefix = options.Subtitles.SpeakerPrefix(cue.Speaker);

            if (cue.LineWords.Count == 0)
            {
                // The engine reported no word timestamps for the segment behind this cue, so
                // there is nothing to tag. The cue builder times such a segment by each chunk's
                // share of the characters, which is a reasonable guess about when to show a line
                // and a worthless one about when a word is spoken — so no timings are emitted
                // rather than character-share timings dressed up as measured ones.
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
            }
            else
            {
                builder.Append(prefix);
                AppendTimedLines(builder, cue, options.NewLine);
            }

            builder.Append(options.NewLine);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes the cue payload as <c>&lt;c&gt;word&lt;/c&gt;</c> spans separated by a timestamp
    /// apiece, the first word of the cue taking no timestamp.
    /// </summary>
    /// <remarks>
    /// The first word takes none because a timestamp equal to the cue's own start is a semantic
    /// no-op — the cue is already displayed — and is exactly what FFmpeg's validator rejects. Its
    /// span is still emitted, so it becomes <c>:past</c> once the second timestamp elapses.
    /// </remarks>
    private static void AppendTimedLines(StringBuilder builder, SubtitleCue cue, string newLine)
    {
        // Compared as whole truncated milliseconds rather than as TimeSpans, because a parser
        // compares what was written down. Two words 300 microseconds apart are strictly increasing
        // as TimeSpans and render to the same millisecond, and FFmpeg's reader rejects an inline
        // timestamp that is not strictly greater than the one before it.
        var cueEnd = Milliseconds(cue.End);
        var previous = Milliseconds(cue.Start);
        var isFirstWord = true;

        for (var line = 0; line < cue.LineWords.Count; line++)
        {
            if (line > 0)
            {
                builder.Append(newLine);
            }

            var words = cue.LineWords[line];
            for (var i = 0; i < words.Count; i++)
            {
                if (!isFirstWord)
                {
                    var at = Milliseconds(words[i].Start);

                    // Skipped, not nudged into range, when Tidy has moved the cue out from under
                    // it: the word keeps its place in the text and simply lights up with the one
                    // before it. Clamping instead would write a timestamp into the file that the
                    // model never produced, to satisfy a constraint the model was not consulted
                    // about. Strictly inside the cue and strictly increasing is the whole rule.
                    if (at > previous && at < cueEnd)
                    {
                        builder.Append('<').Append(Timecode.ToVtt(words[i].Start)).Append('>');
                        previous = at;
                    }

                    if (i > 0)
                    {
                        builder.Append(' ');
                    }
                }

                builder.Append("<c>").Append(words[i].Text.Trim()).Append("</c>");
                isFirstWord = false;
            }
        }

        builder.Append(newLine);
    }

    /// <summary>
    /// Whole milliseconds, truncated and floored at zero — the value <see cref="Timecode.ToVtt"/>
    /// will render, so that ordering is decided on the bytes rather than on what they came from.
    /// </summary>
    private static long Milliseconds(TimeSpan value) =>
        value <= TimeSpan.Zero ? 0 : value.Ticks / TimeSpan.TicksPerMillisecond;
}
