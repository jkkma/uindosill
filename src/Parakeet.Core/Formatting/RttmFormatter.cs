using Parakeet.Core.Diarisation;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Formatting;

/// <summary>
/// The speaker turns as an RTTM file — the labeller's own output, not the words. This is the
/// format a diarisation scorer reads, so it is what connects a transcript made with the speaker
/// opt-in to <c>uindosill der</c> and the hand-labelled fixtures: run the product over a stretch,
/// write this, score it against the reference.
/// </summary>
/// <remarks>
/// Empty when the document carries no turns — a transcript made without the opt-in has nothing
/// to say here, and an empty file says so more honestly than a file of guessed turns from segment
/// boundaries would. The file id is the source name without its extension, whitespace made
/// underscores, because RTTM splits its fields on whitespace — and so is whitespace in a speaker's
/// display name, so <c>Speaker 1</c> is written <c>Speaker_1</c>.
/// </remarks>
public sealed class RttmFormatter : ITranscriptFormatter
{
    public string Id => "rttm";

    public string DisplayName => "RTTM speaker turns";

    public string FileExtension => ".rttm";

    public string Format(TranscriptDocument document, TranscriptFormatOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptFormatOptions.Default;

        if (document.SpeakerTurns.Count == 0)
        {
            return string.Empty;
        }

        // Display names carry a space ("Speaker 1"); RTTM splits fields on whitespace, so the
        // label goes out as Speaker_1 — the same rule the file id follows.
        var turns = document.SpeakerTurns.Select(t => t with { Speaker = Sanitise(t.Speaker) });
        return RttmFile.Write(turns, FileIdFor(document.SourceName), options.NewLine);
    }

    private static string Sanitise(string label) =>
        string.Join('_', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static string FileIdFor(string? sourceName) =>
        RttmFile.SanitiseFileId(
            string.IsNullOrWhiteSpace(sourceName) ? null : Path.GetFileNameWithoutExtension(sourceName));
}
