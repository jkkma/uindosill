using CommunityToolkit.Mvvm.ComponentModel;

namespace Parakeet.App.ViewModels;

/// <summary>
/// One voice in a recording: the label the diariser gave it, the colour it was assigned, and the
/// name a reader has put on it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One object per speaker, not per line.</b> A three-hour recording is fifteen hundred segments
/// and a handful of voices. A name written onto every line would mean fifteen hundred property
/// notifications to change a handful of facts, and fifteen hundred copies of
/// one string that could disagree with each other. So the lines hold a reference to this and bind
/// through it — see <see cref="TranscriptLineViewModel.Voice"/> — and renaming a speaker raises one
/// notification that every cue of that speaker is already listening to, and none that any other
/// cue is.
/// </para>
/// <para>
/// The same object serves both panes of a translated transcript, because
/// <see cref="JobViewModel"/> builds the map once from the spoken document and hands it to both.
/// That is not a convenience: it is the reason a rename cannot make the two panes disagree, which
/// is the defect the shared chip map was introduced on 2026-08-22 to fix.
/// </para>
/// <para>
/// <b>The name is for reading, and does not reach the files.</b> Nothing here is written back to a
/// transcript on disk, and nothing survives the run: a second pass over the same audio need not
/// give "Speaker 1" to the same person, so restoring a name would be this window asserting an
/// identity it has no way to check. <see cref="AskViewModel.RenameNotice"/> is what says so, and it
/// says so only once somebody has actually renamed something.
/// </para>
/// </remarks>
public sealed class SpeakerViewModel : ObservableObject
{
    private string _name;

    public SpeakerViewModel(string label, int chip)
    {
        Label = label;
        Chip = chip;
        _name = label;
    }

    /// <summary>
    /// What the diariser called this voice — <c>Speaker 1</c>, or a labeller's own string. Kept
    /// whatever the reader types over it, because it is the name the saved transcript files carry
    /// and the only thing that ties what is on screen to what is on disk.
    /// </summary>
    public string Label { get; }

    /// <summary>Which of the four colours this voice was assigned, in order of first appearance.</summary>
    public int Chip { get; }

    /// <summary>
    /// What the window calls this voice. The reader's name for it where they have given one, and
    /// <see cref="Label"/> where they have not.
    /// </summary>
    /// <remarks>
    /// Trimmed, and blank means "put it back": a field somebody has emptied is somebody undoing a
    /// rename, not somebody asking for a nameless speaker. Whitespace is trimmed rather than
    /// rejected because a name pasted out of a document arrives with some on it more often than
    /// not, and a chip reading " Ada " is a bug nobody would report.
    /// </remarks>
    public string Name
    {
        get => _name;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? Label : value.Trim();

            if (SetProperty(ref _name, trimmed))
            {
                OnPropertyChanged(nameof(IsRenamed));
            }
        }
    }

    /// <summary>Whether a reader has given this voice a name of their own.</summary>
    public bool IsRenamed => !string.Equals(Name, Label, StringComparison.Ordinal);
}
