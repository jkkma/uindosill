using System.Globalization;
using System.Text;

namespace Parakeet.Core.Diarisation;

/// <summary>Speaker turns read from one RTTM file, with the file ids the lines carried.</summary>
public sealed record RttmDocument
{
    public required IReadOnlyList<SpeakerTurn> Turns { get; init; }

    /// <summary>
    /// The second field of every <c>SPEAKER</c> line, distinct, in order of first appearance. One
    /// is the normal case; the scorer treats more than one as a mistake worth stopping for.
    /// </summary>
    public required IReadOnlyList<string> FileIds { get; init; }

    /// <summary>Lines that were not <c>SPEAKER</c> records and were skipped, for the caller to report.</summary>
    public int SkippedLines { get; init; }
}

/// <summary>
/// The NIST Rich Transcription Time Marked format, as every diarisation scorer reads it and as
/// hand labels are committed here. One line per turn:
/// <c>SPEAKER &lt;file-id&gt; 1 &lt;onset&gt; &lt;duration&gt; &lt;NA&gt; &lt;NA&gt; &lt;speaker&gt; &lt;NA&gt; &lt;NA&gt;</c>,
/// whitespace-separated, seconds to three decimals, LF line endings.
/// </summary>
/// <remarks>
/// The reader is tolerant where the wild is: fields are split on any run of whitespace, a ninth
/// and tenth field are optional (md-eval's own examples have nine), and non-<c>SPEAKER</c> record
/// types are skipped rather than fatal. What it is strict about is the shape of the numbers: a
/// duration that does not parse, or is negative, stops the read — a scorer fed a guessed onset
/// produces a confident wrong number, which is worse than no number.
/// </remarks>
public static class RttmFile
{
    /// <summary>The <c>&lt;NA&gt;</c> placeholder every field this project does not use is written as.</summary>
    public const string NotApplicable = "<NA>";

    public static RttmDocument Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var turns = new List<SpeakerTurn>();
        var fileIds = new List<string>();
        var skipped = 0;
        var lineNumber = 0;

        foreach (var raw in content.Split('\n'))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith(';'))
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!string.Equals(fields[0], "SPEAKER", StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            if (fields.Length < 8)
            {
                throw new FormatException(
                    $"RTTM line {lineNumber} has {fields.Length} fields; a SPEAKER record needs at least eight " +
                    "(type, file id, channel, onset, duration, <NA>, <NA>, speaker).");
            }

            var onset = ParseSeconds(fields[3], lineNumber, "onset");
            var duration = ParseSeconds(fields[4], lineNumber, "duration");
            if (duration < 0)
            {
                throw new FormatException($"RTTM line {lineNumber} has a negative duration ({fields[4]}).");
            }

            var speaker = fields[7];
            if (speaker == NotApplicable)
            {
                throw new FormatException($"RTTM line {lineNumber} names no speaker (field 8 is {NotApplicable}).");
            }

            if (!fileIds.Contains(fields[1], StringComparer.Ordinal))
            {
                fileIds.Add(fields[1]);
            }

            turns.Add(new SpeakerTurn
            {
                Start = SpeakerTurns.FromSeconds(onset),
                End = SpeakerTurns.FromSeconds(onset + duration),
                Speaker = speaker,
            });
        }

        return new RttmDocument { Turns = turns, FileIds = fileIds, SkippedLines = skipped };
    }

    private static double ParseSeconds(string field, int lineNumber, string what)
    {
        if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new FormatException($"RTTM line {lineNumber}: {what} '{field}' is not a number of seconds.");
        }

        return value;
    }

    /// <summary>
    /// Turns a name into something <see cref="Write"/> will accept as a file id: whitespace runs
    /// become single underscores, and a name that is nothing but whitespace becomes
    /// <c>transcript</c>.
    /// </summary>
    /// <remarks>
    /// Beside the check that enforces the rule rather than in whichever caller happened to need it
    /// first. <c>Write</c> refuses whitespace because whitespace is RTTM's field separator, so any
    /// caller deriving an id from a file name has to sanitise — and a caller that forgets does not
    /// find out until it has already done the work. That is not hypothetical: <c>diarise</c> forgot,
    /// and ran a whole recording before throwing on <c>Board Meeting.wav</c>.
    /// </remarks>
    public static string SanitiseFileId(string? name)
    {
        var parts = (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "transcript" : string.Join('_', parts);
    }

    /// <summary>
    /// Writes turns as RTTM. Three decimals and invariant formatting, so a fixture diffs cleanly
    /// and reads the same on every machine; a speaker label with whitespace in it is refused,
    /// because whitespace is the field separator and the label would come back as two. See
    /// <see cref="SanitiseFileId"/> for turning a file name into an acceptable id.
    /// </summary>
    public static string Write(IEnumerable<SpeakerTurn> turns, string fileId, string newLine = "\n")
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentNullException.ThrowIfNull(newLine);

        if (fileId.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"An RTTM file id cannot contain whitespace ('{fileId}').", nameof(fileId));
        }

        var builder = new StringBuilder();
        foreach (var turn in turns.OrderBy(t => t.Start).ThenBy(t => t.End).ThenBy(t => t.Speaker, StringComparer.Ordinal))
        {
            turn.Validate();
            if (turn.Speaker.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException(
                    $"Speaker label '{turn.Speaker}' contains whitespace, which RTTM uses as its field separator. " +
                    "Rename the speaker (underscores are the convention).");
            }

            builder.Append("SPEAKER ")
                   .Append(fileId)
                   .Append(" 1 ")
                   .Append(Seconds(turn.Start))
                   .Append(' ')
                   .Append(Seconds(turn.Duration))
                   .Append(' ').Append(NotApplicable)
                   .Append(' ').Append(NotApplicable)
                   .Append(' ').Append(turn.Speaker)
                   .Append(' ').Append(NotApplicable)
                   .Append(' ').Append(NotApplicable)
                   .Append(newLine);
        }

        return builder.ToString();
    }

    private static string Seconds(TimeSpan value) =>
        Math.Round((decimal)value.TotalSeconds, 3, MidpointRounding.AwayFromZero).ToString("0.000", CultureInfo.InvariantCulture);
}
