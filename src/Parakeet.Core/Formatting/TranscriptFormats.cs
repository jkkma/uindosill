using System.Diagnostics.CodeAnalysis;

namespace Parakeet.Core.Formatting;

/// <summary>The output formats the product supports, looked up by id or extension.</summary>
public static class TranscriptFormats
{
    public static ITranscriptFormatter PlainText { get; } = new PlainTextFormatter();

    public static ITranscriptFormatter Srt { get; } = new SrtFormatter();

    public static ITranscriptFormatter Vtt { get; } = new VttFormatter();

    public static ITranscriptFormatter WordTimedVtt { get; } = new WordTimedVttFormatter();

    public static ITranscriptFormatter Json { get; } = new JsonTranscriptFormatter();

    public static ITranscriptFormatter Markdown { get; } = new MarkdownFormatter();

    /// <summary>Speaker turns only; empty unless the transcript was made with speaker labelling on.</summary>
    public static ITranscriptFormatter Rttm { get; } = new RttmFormatter();

    public static IReadOnlyList<ITranscriptFormatter> All { get; } =
        [PlainText, Srt, Vtt, WordTimedVtt, Json, Markdown, Rttm];

    public static IReadOnlyList<string> Ids { get; } = [.. All.Select(f => f.Id)];

    public static bool TryGet(string id, [NotNullWhen(true)] out ITranscriptFormatter? formatter)
    {
        formatter = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var normalised = id.Trim().TrimStart('.').ToLowerInvariant();
        normalised = normalised switch
        {
            "text" or "plain" => "txt",
            "markdown" => "md",
            "webvtt" => "vtt",
            "webvtt-words" or "words" => "vtt-words",
            "subrip" => "srt",
            _ => normalised,
        };

        formatter = All.FirstOrDefault(f => f.Id == normalised);
        return formatter is not null;
    }

    public static ITranscriptFormatter Get(string id) =>
        TryGet(id, out var formatter)
            ? formatter
            : throw new ArgumentException(
                $"Unknown transcript format '{id}'. Known formats: {string.Join(", ", Ids)}.", nameof(id));

    /// <summary>
    /// The ids in <paramref name="ids"/> as this registry spells them — aliases resolved, a
    /// leading dot and case dropped — with a spelling that names a format already named left out,
    /// in first-seen order. Throws, as <see cref="Get"/> does, on a spelling that names nothing.
    /// </summary>
    /// <remarks>
    /// A list of formats is read in more than one place — a guard that refuses a format under an
    /// option, a guard that refuses it without one, the writer that resolves each id to a file —
    /// and <see cref="TryGet"/> accepts several spellings for each. Until 2026-08-22 only the
    /// writer resolved them, so a guard comparing the typed spelling against a canonical id let
    /// <c>words</c> past a refusal written for <c>vtt-words</c> and <c>.rttm</c> past one written
    /// for <c>rttm</c>, and <c>vtt,webvtt</c> wrote one file twice under two names. One pass
    /// through here, once, and every reader sees the same list.
    /// </remarks>
    public static IReadOnlyList<string> Canonical(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var canonical = new List<string>();
        foreach (var id in ids)
        {
            var spelled = Get(id).Id;
            if (!canonical.Contains(spelled, StringComparer.Ordinal))
            {
                canonical.Add(spelled);
            }
        }

        return canonical;
    }
}
