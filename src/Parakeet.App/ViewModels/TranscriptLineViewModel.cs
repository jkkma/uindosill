using CommunityToolkit.Mvvm.ComponentModel;

namespace Parakeet.App.ViewModels;

/// <summary>
/// A line's words and the term to pick out inside them, as one value.
/// </summary>
/// <remarks>
/// One value rather than two bindings, because a highlight is a function of both and a converter
/// can only be handed one thing. It exists so the view models stay free of Avalonia's text types:
/// this says <em>what</em> to mark, and <c>HighlightConverter</c> in the view turns it into the
/// runs a <c>TextBlock</c> draws.
/// </remarks>
public sealed record TextHighlight(string Text, string? Term);

/// <summary>One segment of a transcript, as the window draws it: a speaker chip and the words.</summary>
/// <remarks>
/// <para>
/// This exists beside <see cref="JobViewModel.Transcript"/> rather than replacing it, and the two
/// are not redundant. The string is what a person copies out of the window, and it is what the
/// tests hold to the "Speaker 1: " shape; this is what the window draws, where the speaker is a
/// chip rather than a prefix. Building the chips by re-parsing the string would mean inventing a
/// parser for text that legitimately contains colons.
/// </para>
/// <para>
/// It carries its own start and end, which the Transcribe tab does not draw and the Ask tab is
/// built on: there, a segment is a place in the recording rather than a paragraph, and clicking it
/// seeks. The times come off the <c>TranscriptSegment</c> unchanged — the window never computes a
/// timestamp of its own, which is the rule <c>docs/V2-ASK-THE-TRANSCRIPT.md</c> sets for citations
/// and applies just as well to the transcript they cite.
/// </para>
/// </remarks>
public sealed partial class TranscriptLineViewModel : ObservableObject
{
    /// <summary>
    /// Whether the recording is inside this segment right now. Drives the highlight in the Ask
    /// tab, and is false everywhere else — nothing on the Transcribe tab is playing.
    /// </summary>
    /// <remarks>
    /// Observable, which is what makes this class an <see cref="ObservableObject"/> at all. The
    /// rest of it is immutable and stays that way: a line's words and its times are a property of
    /// the transcript, and only "is this the one being played" belongs to the moment.
    /// </remarks>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    /// What to pick out inside this line, or null to draw it plain.
    /// </summary>
    /// <remarks>
    /// Set only on lines that actually contain the term, and cleared off them when it changes. A
    /// search that wrote the term onto every line would rebuild every paragraph in the transcript
    /// on every keystroke — fifteen hundred of them on a three-hour recording, all but a handful
    /// re-rendering to exactly what they already said.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Marked))]
    private string? _searchTerm;

    /// <summary>Whether this is the hit the search is standing on, as opposed to one of the rest.</summary>
    [ObservableProperty]
    private bool _isCurrentMatch;

    public TranscriptLineViewModel(string? speaker, string text, int chip, TimeSpan start, TimeSpan end)
    {
        Speaker = speaker;
        Text = text;
        Chip = chip;
        Start = start;
        End = end;
    }

    /// <summary>The speaker's name, or null on a transcript that was never labelled.</summary>
    public string? Speaker { get; }

    public string Text { get; }

    /// <summary>Where this segment starts in the recording.</summary>
    public TimeSpan Start { get; }

    /// <summary>Where it ends. Used to decide which line is being played, not drawn anywhere.</summary>
    public TimeSpan End { get; }

    /// <summary>The start as the window writes it — the label on the cue you click to seek.</summary>
    public string Timestamp => Timecode.Format(Start);

    /// <summary>The words and what to pick out in them, which is what the view draws.</summary>
    public TextHighlight Marked => new(Text, SearchTerm);

    /// <summary>Whether <paramref name="term"/> appears in this line, however it is cased.</summary>
    /// <remarks>
    /// A substring rather than a word: somebody searching a transcript for a name they half heard
    /// is better served by too many hits than by none, and the transcript is the machine's spelling
    /// of what was said rather than the speaker's.
    /// </remarks>
    public bool Mentions(string term) => Text.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which of the four chip styles this speaker gets, or -1 when there is no speaker.
    /// </summary>
    /// <remarks>
    /// Four, and the set is closed, because four is the diariser's architectural ceiling rather
    /// than a setting — so there is no fifth style to fall through to and no need for one. The
    /// index is assigned in order of first appearance in the transcript, which is also the order
    /// the diariser numbers speakers in.
    /// </remarks>
    public int Chip { get; }

    public bool HasSpeaker => Speaker is not null;

    /// <summary>Whether <paramref name="position"/> falls inside this segment.</summary>
    /// <remarks>
    /// Half-open, so two segments that touch cannot both claim the instant between them. A
    /// zero-length segment — which a decode can produce — matches nothing and simply never
    /// highlights, rather than matching everything from its start onward.
    /// </remarks>
    public bool Contains(TimeSpan position) => position >= Start && position < End;
}
