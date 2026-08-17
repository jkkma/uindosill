using System.Globalization;

namespace Parakeet.Core.Diarisation;

/// <summary>What the converter made of an Audacity label export, and what it noticed on the way.</summary>
public sealed record AudacityLabelDocument
{
    /// <summary>The turns, after same-speaker overlaps (and, if asked, short gaps) were merged.</summary>
    public required IReadOnlyList<SpeakerTurn> Turns { get; init; }

    /// <summary>Labels as read, before any merging: what the labeller actually drew.</summary>
    public required int LabelCount { get; init; }

    /// <summary>Point labels — start equal to end — dropped, because a click is not speech.</summary>
    public required int PointLabelsDropped { get; init; }

    /// <summary>How many labels the merge step folded into a neighbour of the same speaker.</summary>
    public required int LabelsMerged { get; init; }
}

/// <summary>
/// Reads Audacity's <em>Export Labels</em> format — one tab-separated <c>start end text</c> line per
/// label, all label tracks merged and sorted by time — into speaker turns, the label text being
/// the speaker's name. This is the whole of the "labels to RTTM" converter the measurement plan
/// asked for; <see cref="RttmFile.Write"/> does the other half.
/// </summary>
/// <remarks>
/// <para>
/// The labelling convention this reads against: one label track per speaker with the speaker's
/// name as every label's text, each speaker labelled independently so overlap falls out for free.
/// Since the export merges every track into one file, the text is the only thing that says whose
/// label a line is — an empty label text is therefore an error, not a default.
/// </para>
/// <para>
/// Audacity 2.3 and later may follow each label line with a spectral-selection line beginning
/// with a backslash (<c>\ low high</c>); those are skipped. Times are read as seconds with any
/// number of decimals — the export writes six.
/// </para>
/// </remarks>
public static class AudacityLabels
{
    public static AudacityLabelDocument Parse(string content, TimeSpan bridge = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var labels = new List<SpeakerTurn>();
        var points = 0;
        var lineNumber = 0;

        foreach (var raw in content.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.StartsWith('\\'))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 3)
            {
                throw new FormatException(
                    $"Audacity labels line {lineNumber} is not 'start<TAB>end<TAB>label': '{line}'.");
            }

            var start = ParseSeconds(fields[0], lineNumber, "start");
            var end = ParseSeconds(fields[1], lineNumber, "end");
            var speaker = string.Join('\t', fields[2..]).Trim();

            if (speaker.Length == 0)
            {
                throw new FormatException(
                    $"Audacity labels line {lineNumber} has no text. Every label must carry the speaker's name — " +
                    "the export merges all tracks, so the text is the only thing that says who a label belongs to.");
            }

            if (end < start)
            {
                throw new FormatException($"Audacity labels line {lineNumber} ends ({fields[1]}) before it starts ({fields[0]}).");
            }

            if (end == start)
            {
                points++;
                continue;
            }

            labels.Add(new SpeakerTurn
            {
                Start = SpeakerTurns.FromSeconds(start),
                End = SpeakerTurns.FromSeconds(end),
                Speaker = SanitiseSpeaker(speaker),
            });
        }

        var merged = SpeakerTurns.Merge(labels, bridge);

        return new AudacityLabelDocument
        {
            Turns = merged,
            LabelCount = labels.Count + points,
            PointLabelsDropped = points,
            LabelsMerged = labels.Count - merged.Count,
        };
    }

    /// <summary>
    /// RTTM separates fields on whitespace, so a speaker called "Host A" would come back as two
    /// fields. Interior whitespace becomes underscores; nothing else about the name is touched.
    /// </summary>
    internal static string SanitiseSpeaker(string speaker) =>
        string.Join('_', speaker.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static double ParseSeconds(string field, int lineNumber, string what)
    {
        if (!double.TryParse(field.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new FormatException($"Audacity labels line {lineNumber}: {what} '{field}' is not a non-negative number of seconds.");
        }

        return value;
    }
}
