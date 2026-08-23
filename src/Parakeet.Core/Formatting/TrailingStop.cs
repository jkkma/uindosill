using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>
/// Takes the sentence-final full stop off the end of a line — a subtitle cue, or a transcript line
/// on screen — and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Asked for on 2026-08-23, in as many words: "real subtitles don't have dots at the end." Applied
/// where text is drawn or written <em>as subtitles</em> — the last line of every cue
/// <see cref="SubtitleCueBuilder"/> makes, so SRT, VTT and the word-timed VTT agree, and the lines
/// the window draws — and not to the transcript formats (TXT, JSON, Markdown), which carry the text
/// as the model wrote it, nor to the document, which is what the sentence splitter and the word
/// times are computed from.
/// </para>
/// <para>
/// Only the full stop. A question mark or an exclamation mark carries meaning a reader needs; an
/// ellipsis — <c>…</c>, or a stop with another stop before it — marks a trailing-off and stays. A
/// closing quote or bracket after the stop stays and the stop inside it goes: <c>sagte er."</c>
/// reads <c>sagte er"</c>. An abbreviation that happens to end a line — <c>Mr.</c> on a bad cut —
/// loses its stop too; the cut is the defect there, not the stripping.
/// </para>
/// </remarks>
public static class TrailingStop
{
    /// <summary>
    /// <paramref name="text"/> without one sentence-final full stop, trailing whitespace gone with
    /// it — or <paramref name="text"/> itself, the same instance, when there is nothing to take.
    /// </summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.AsSpan().TrimEnd();
        var end = trimmed.Length;

        while (end > 0 && IsClosing(trimmed[end - 1]))
        {
            end--;
        }

        if (end == 0 || trimmed[end - 1] != '.')
        {
            return text;
        }

        if (end >= 2 && trimmed[end - 2] == '.')
        {
            // An ellipsis spelled out, which is a trailing-off and not a stop.
            return text;
        }

        return string.Concat(trimmed[..(end - 1)], trimmed[end..]);
    }

    /// <summary>
    /// <paramref name="word"/> with <see cref="Strip(string)"/> applied to its text — the same
    /// instance when nothing changed, so a caller can keep the list it has.
    /// </summary>
    public static TranscriptWord Strip(TranscriptWord word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var text = Strip(word.Text);
        return ReferenceEquals(text, word.Text) ? word : word with { Text = text };
    }

    private static bool IsClosing(char c) =>
        c is '"' or '\'' or '»' or '«' or '”' or '“' or '’' or ')' or ']' or '」' or '』';
}
