using CommunityToolkit.Mvvm.ComponentModel;
using Parakeet.Core.Transcription;

namespace Parakeet.App.ViewModels;

/// <summary>
/// A line's words, the term to pick out inside them, and the word being spoken right now, as one
/// value.
/// </summary>
/// <remarks>
/// One value rather than three bindings, because a highlight is a function of all of them and a
/// converter can only be handed one thing. It exists so the view models stay free of Avalonia's
/// text types: this says <em>what</em> to mark, and <c>HighlightConverter</c> in the view turns it
/// into the runs a <c>TextBlock</c> draws.
/// </remarks>
/// <param name="Text">The line as it is drawn.</param>
/// <param name="Term">What a search is looking for, or null.</param>
/// <param name="SpokenStart">Where the word being spoken starts in <paramref name="Text"/>.</param>
/// <param name="SpokenLength">How long it is, or 0 when no word is being spoken.</param>
public sealed record TextHighlight(string Text, string? Term, int SpokenStart = 0, int SpokenLength = 0);

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
/// <para>
/// It carries the segment's <em>word</em> times too, which is what lets the Ask tab mark the one
/// word being said inside the line being played. Those come off the <c>TranscriptWord</c> list
/// unchanged for the same reason, and where the engine reported none — or where the words do not
/// spell the text — nothing is marked rather than a word's position being guessed from how far
/// through the line the playhead is. That guess is exactly what <c>WordTimedVttFormatter</c>
/// refuses to write, calling it "a worthless guess about when a word is spoken".
/// </para>
/// </remarks>
public sealed partial class TranscriptLineViewModel : ObservableObject
{
    /// <summary>
    /// Where each of this line's words sits inside <see cref="Text"/>, and when it is said.
    /// Empty when the engine reported no word timings, which is a whole transcript at a time.
    /// </summary>
    private readonly WordSpan[] _spans;

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
    /// Which of this line's words is being said right now, as an index into its own timings, or
    /// -1 for none. <see cref="WordAt"/> is what answers it; nothing else should set it.
    /// </summary>
    /// <remarks>
    /// An index rather than the word itself, because what the view needs is where the word is in
    /// the text and not what it says — the text is already there, and re-sending it would be a
    /// second copy of the line's own words that could disagree with the first.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Marked))]
    private int _spokenWord = -1;

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

    public TranscriptLineViewModel(
        SpeakerViewModel? voice,
        string text,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<TranscriptWord>? words = null)
    {
        Voice = voice;
        Text = text;
        Start = start;
        End = end;
        _spans = Locate(text, words);
    }

    /// <summary>
    /// The voice this segment belongs to, or null on a transcript that was never labelled.
    /// </summary>
    /// <remarks>
    /// A reference rather than a copy of the name and the colour, and that is the whole of what
    /// makes a speaker renameable. Every line of one speaker points at one
    /// <see cref="SpeakerViewModel"/>, so the chip binds through it — <c>{Binding Voice.Name}</c> —
    /// and a rename raises one notification that exactly those lines are listening to. Copied onto
    /// each line instead, a rename would mean walking fifteen hundred rows to change four facts,
    /// and the copies could drift apart.
    /// </remarks>
    public SpeakerViewModel? Voice { get; }

    /// <summary>The speaker's name as the window shows it, or null on an unlabelled transcript.</summary>
    /// <remarks>
    /// Reads through <see cref="Voice"/> rather than holding a string, so it follows a rename. It
    /// raises nothing of its own: a binding that needs to follow the name binds
    /// <c>Voice.Name</c>, and one bound to this would draw once and never again. What it is for is
    /// the tests and the exporters, which read it once.
    /// </remarks>
    public string? Speaker => Voice?.Name;

    public string Text { get; }

    /// <summary>Where this segment starts in the recording.</summary>
    public TimeSpan Start { get; }

    /// <summary>Where it ends. Used to decide which line is being played, not drawn anywhere.</summary>
    public TimeSpan End { get; }

    /// <summary>The start as the window writes it — the label on the cue you click to seek.</summary>
    public string Timestamp => Timecode.Format(Start);

    /// <summary>Whether this line has word timings at all, and so can mark a word being said.</summary>
    public bool HasWordTimings => _spans.Length > 0;

    /// <summary>The words, what to pick out in them, and which one is being said — what the view draws.</summary>
    public TextHighlight Marked =>
        SpokenWord >= 0 && SpokenWord < _spans.Length
            ? new(Text, SearchTerm, _spans[SpokenWord].Start, _spans[SpokenWord].Length)
            : new(Text, SearchTerm);

    /// <summary>
    /// Which word <paramref name="position"/> is inside, as an index for <see cref="SpokenWord"/>,
    /// or -1 when the playhead has not reached this line's first word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last word that has <em>started</em>, rather than the word whose own span contains the
    /// position. The two differ across the gap between one word and the next, and the difference
    /// is visible: at a tick every 100 ms, a rule that lit nothing in the gaps would blink the
    /// mark off between words on a normal sentence and off for a second at every pause. Holding
    /// the last word until the next one begins draws no word ahead of the moment being played,
    /// which is the rule <c>docs/PHASES.md</c> sets for the word-by-word view, and it is what
    /// <c>scripts/preview-words-vtt.html</c> — this repository's own prototype of the mark, and
    /// WebVTT's <c>:past</c>/<c>:future</c> beneath it — already does.
    /// </para>
    /// <para>
    /// Linear over the line's own words rather than a binary search: a segment is a sentence or
    /// two, so this is a handful of comparisons, and it runs on one line per tick rather than on
    /// the transcript.
    /// </para>
    /// </remarks>
    public int WordAt(TimeSpan position)
    {
        var found = -1;

        for (var i = 0; i < _spans.Length && _spans[i].From <= position; i++)
        {
            found = i;
        }

        return found;
    }

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
    public int Chip => Voice?.Chip ?? -1;

    public bool HasSpeaker => Voice is not null;

    /// <summary>Whether <paramref name="position"/> falls inside this segment.</summary>
    /// <remarks>
    /// Half-open, so two segments that touch cannot both claim the instant between them. A
    /// zero-length segment — which a decode can produce — matches nothing and simply never
    /// highlights, rather than matching everything from its start onward.
    /// </remarks>
    public bool Contains(TimeSpan position) => position >= Start && position < End;

    /// <summary>Where one word sits inside the line, and when it is said.</summary>
    private readonly record struct WordSpan(int Start, int Length, TimeSpan From);

    /// <summary>
    /// Finds each word inside <paramref name="text"/>, in order, so a time can be turned into a
    /// place in the line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Located rather than assumed. Joining the words with single spaces reproduces the segment's
    /// text on almost every segment this pipeline produces — <c>SpeakerAssignment</c> checks
    /// exactly that before it cuts a segment on a speaker change — but "almost every" is not
    /// every, and the failure of assuming it is silent: every word after the first disagreement
    /// lights up one word early, which looks like a transcript rather than like a bug.
    /// </para>
    /// <para>
    /// So each word is searched for from where the last one ended, at a word boundary, and one
    /// that is not there is skipped without moving the cursor — the words around it keep their
    /// places and it simply never lights. Forward-only, so a mark can never walk backwards through
    /// a line; boundary-aligned, so "read" cannot land inside "reader".
    /// </para>
    /// </remarks>
    private static WordSpan[] Locate(string text, IReadOnlyList<TranscriptWord>? words)
    {
        if (words is not { Count: > 0 })
        {
            return [];
        }

        var spans = new List<WordSpan>(words.Count);
        var at = 0;

        foreach (var word in words)
        {
            var token = word.Text.Trim();

            if (token.Length == 0)
            {
                continue;
            }

            var found = IndexOfToken(text, token, at);

            if (found < 0)
            {
                continue;
            }

            spans.Add(new WordSpan(found, token.Length, word.Start));
            at = found + token.Length;
        }

        return [.. spans];
    }

    /// <summary>
    /// The first occurrence of <paramref name="token"/> at or after <paramref name="from"/> that is
    /// not part of a longer word, or -1.
    /// </summary>
    private static int IndexOfToken(string text, string token, int from)
    {
        for (var at = from; at <= text.Length - token.Length; )
        {
            var found = text.IndexOf(token, at, StringComparison.Ordinal);

            if (found < 0)
            {
                return -1;
            }

            var end = found + token.Length;
            var before = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            var after = end == text.Length || !char.IsLetterOrDigit(text[end]);

            if (before && after)
            {
                return found;
            }

            at = found + 1;
        }

        return -1;
    }
}
