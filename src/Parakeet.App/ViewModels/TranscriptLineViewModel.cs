namespace Parakeet.App.ViewModels;

/// <summary>One segment of a transcript, as the window draws it: a speaker chip and the words.</summary>
/// <remarks>
/// <para>
/// This exists beside <see cref="JobViewModel.Transcript"/> rather than replacing it, and the two
/// are not redundant. The string is what a person copies out of the window, and it is what the
/// tests hold to the "Speaker 1: " shape; this is what the window draws, where the speaker is a
/// chip rather than a prefix. Building the chips by re-parsing the string would mean inventing a
/// parser for text that legitimately contains colons.
/// </para>
/// </remarks>
public sealed class TranscriptLineViewModel
{
    public TranscriptLineViewModel(string? speaker, string text, int chip)
    {
        Speaker = speaker;
        Text = text;
        Chip = chip;
    }

    /// <summary>The speaker's name, or null on a transcript that was never labelled.</summary>
    public string? Speaker { get; }

    public string Text { get; }

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
}
