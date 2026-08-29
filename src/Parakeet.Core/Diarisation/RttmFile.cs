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
/// produces a confident wrong number, which is worse than no number. The optional tail is held to
/// its own shape for the same reason: an eleventh field, or a word where a confidence belongs, is
/// what a speaker label with a space in it looks like after the split, and reading field eight
/// anyway would truncate that label at the space — merging two speakers into one and scoring the
/// merge without a word.
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

        // A byte order mark survives whenever content arrives here as bytes rather than
        // through a reader that strips it, and U+FEFF is not whitespace to Trim. Left in
        // place it makes the first field of the first line U+FEFF followed by SPEAKER, which
        // is not a record type this reader knows, and the tolerance below would skip that
        // line — dropping one turn and scoring the rest without a word. Tools still write
        // the mark, so it is stripped rather than refused.
        content = content.TrimStart('\uFEFF');

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

            // Each finite half fits a TimeSpan, but their sum is the turn's end and must too:
            // past the range the tick conversion saturates rather than throwing, and a saturated
            // end makes the turn's extent absurd with nothing said. Same rule as ParseSeconds.
            if (onset + duration > TimeSpan.MaxValue.TotalSeconds)
            {
                throw new FormatException(
                    $"RTTM line {lineNumber}: onset plus duration ({fields[3]} + {fields[4]}) is farther into a " +
                    "recording than a time can represent.");
            }

            var speaker = fields[7];
            if (speaker == NotApplicable)
            {
                throw new FormatException($"RTTM line {lineNumber} names no speaker (field 8 is {NotApplicable}).");
            }

            // The optional tail's own shape check. A SPEAKER record has at most ten fields, and
            // the ninth and tenth are a confidence and a lookahead time — <NA> or a number, in
            // every file a tool writes. What else lands here is the rest of a speaker label with
            // spaces in it, split into fields; taking field eight anyway would truncate that
            // label at the first space, merging distinct speakers into one and scoring the merge
            // without a word. Write refuses such labels going out; this is the same refusal
            // coming in. Best-effort, and honestly so: a label whose second word is itself a
            // number ("alice 2") splits into a tail this shape check cannot tell from a
            // confidence, and still truncates — RTTM's format holds no answer for that one.
            if (fields.Length > 10)
            {
                throw new FormatException(
                    $"RTTM line {lineNumber} has {fields.Length} fields where a SPEAKER record has at most ten. " +
                    "The usual cause is a speaker label containing spaces, which whitespace-splitting would " +
                    "silently truncate; rename the speaker (underscores are the convention).");
            }

            for (var extra = 8; extra < fields.Length; extra++)
            {
                if (fields[extra] != NotApplicable
                    && !double.TryParse(fields[extra], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    throw new FormatException(
                        $"RTTM line {lineNumber}: field {extra + 1} ('{fields[extra]}') is neither {NotApplicable} " +
                        "nor a number. The usual cause is a speaker label containing spaces, which " +
                        "whitespace-splitting would silently truncate; rename the speaker " +
                        "(underscores are the convention).");
                }
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

        // Finite is not enough: the tick conversion these values are headed for saturates rather
        // than throwing past TimeSpan's range in either direction, and a saturated onset makes
        // Start == End == TimeSpan.MaxValue (or MinValue, for the negative sign) — a zero-length
        // turn that contributes nothing to a score, silently. That is the same
        // one-turn-quietly-missing failure the byte-order-mark handling above exists to prevent,
        // and an onset with an exponent typo in it earns the same answer as one that does not
        // parse: the read stops. Small negative onsets stay tolerated, as they always were.
        if (value > TimeSpan.MaxValue.TotalSeconds || value < TimeSpan.MinValue.TotalSeconds)
        {
            throw new FormatException(
                $"RTTM line {lineNumber}: {what} '{field}' is farther into a recording than a time can represent.");
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
