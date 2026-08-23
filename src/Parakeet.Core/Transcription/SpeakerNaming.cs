namespace Parakeet.Core.Transcription;

/// <summary>
/// Puts a reader's names for the speakers onto a transcript.
/// </summary>
/// <remarks>
/// <para>
/// The diariser numbers voices — <c>Speaker 1</c>, <c>Speaker 2</c> — because numbering is all it
/// can do: who a voice belongs to is a fact about the world rather than about the audio. A reader
/// supplies the names, and this is what makes them reach an output file rather than staying on
/// screen.
/// </para>
/// <para>
/// <b>A copy, never a change in place.</b> The labels the engine produced are what tie a transcript
/// on screen to the transcript files already written and to any RTTM a scorer will read, so the
/// document that came out of the pipeline stays exactly as it was and this returns a second one
/// with the names on it. Nothing is renamed twice by accident, and the original is always still
/// there to compare against.
/// </para>
/// <para>
/// Words carry a speaker of their own as well as segments — <c>SpeakerAssignment</c> attributes per
/// word, and a segment that could not be cut apart says the speaker of most of it while its words
/// say the rest — so both are rewritten. So are the speaker turns, which are what an RTTM prints:
/// renaming the segments and leaving the turns would make one file disagree with another about the
/// same recording.
/// </para>
/// </remarks>
public static class SpeakerNaming
{
    /// <summary>
    /// <paramref name="document"/> with each speaker label replaced by the name
    /// <paramref name="names"/> gives it. Labels the map does not mention are left alone.
    /// </summary>
    /// <remarks>
    /// Returns the document itself when the map changes nothing, so the common case — a transcript
    /// nobody has renamed — allocates nothing and the caller needs no special path for it.
    /// </remarks>
    public static TranscriptDocument WithSpeakerNames(
        this TranscriptDocument document,
        IReadOnlyDictionary<string, string>? names)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (names is null || names.Count == 0)
        {
            return document;
        }

        // Ordinal, and only where the name actually differs: a map whose entries all say what the
        // label already said is not a rename, and rebuilding a three-hour transcript to apply it
        // would be work for nothing.
        var changes = names
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value)
                && !string.Equals(pair.Key, pair.Value, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        if (changes.Count == 0)
        {
            return document;
        }

        string? Rename(string? label) =>
            label is not null && changes.TryGetValue(label, out var name) ? name : label;

        return document with
        {
            Segments = [.. document.Segments.Select(segment => segment with
            {
                Speaker = Rename(segment.Speaker),
                Words = segment.Words.Count == 0
                    ? segment.Words
                    : [.. segment.Words.Select(word => word with { Speaker = Rename(word.Speaker) })],
            })],
            SpeakerTurns = [.. document.SpeakerTurns.Select(turn => turn with
            {
                Speaker = Rename(turn.Speaker) ?? turn.Speaker,
            })],
        };
    }
}
