using System.Globalization;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Formatting;

namespace Parakeet.Cli;

/// <summary>
/// Turns an Audacity label export into RTTM: the converter the labelling workflow needs between
/// "one label track per speaker, exported" and "a reference file the scorer reads". Prints the
/// RTTM to stdout unless <c>--out</c> names a file, and a summary of what it read to stderr, so
/// the labeller can see at a glance whether the speakers and the totals are what they meant.
/// </summary>
internal static class RttmCommand
{
    public static int Run(CliContext context, ParsedCommandLine parsed)
    {
        if (parsed.Positionals.Count != 1)
        {
            context.WriteError("rttm needs exactly one Audacity label export (the .txt that Export Labels writes).");
            return ExitCodes.UsageError;
        }

        var labelsPath = parsed.Positionals[0];
        if (!File.Exists(labelsPath))
        {
            context.WriteError($"Labels not found: {labelsPath}");
            return ExitCodes.UsageError;
        }

        var bridge = TimeSpan.Zero;
        if (parsed.Value("bridge") is { Length: > 0 } bridgeText)
        {
            if (!CommandLineParser.TryParseDouble(bridgeText, out var seconds) || !double.IsFinite(seconds) || seconds < 0 || seconds > 3600)
            {
                context.WriteError($"--bridge needs a number of seconds between 0 and 3600, got '{bridgeText}'.");
                return ExitCodes.UsageError;
            }

            bridge = SpeakerTurns.FromSeconds(seconds);
        }

        var fileId = parsed.Value("file-id");
        if (string.IsNullOrWhiteSpace(fileId))
        {
            fileId = Path.GetFileNameWithoutExtension(labelsPath);
        }

        fileId = string.Join('_', fileId.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        AudacityLabelDocument labels;
        try
        {
            labels = AudacityLabels.Parse(File.ReadAllText(labelsPath), bridge);
        }
        catch (FormatException ex)
        {
            throw new CliUsageException($"{labelsPath}: {ex.Message}", ex);
        }

        var rttm = RttmFile.Write(labels.Turns, fileId);

        var outputPath = parsed.Value("out");
        if (outputPath is { Length: > 0 })
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(outputPath, rttm, TextOutput.Utf8NoBom);
        }
        else
        {
            context.Out.Write(rttm);
        }

        // The summary goes to stderr so the RTTM on stdout stays pipeable.
        var speakers = SpeakerTurns.Speakers(labels.Turns);
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFileName(labelsPath)}: {labels.LabelCount} labels → {labels.Turns.Count} turns, file id '{fileId}'");
        if (labels.PointLabelsDropped > 0)
        {
            summary += string.Create(CultureInfo.InvariantCulture,
                $", {labels.PointLabelsDropped} point label{(labels.PointLabelsDropped == 1 ? "" : "s")} dropped");
        }

        if (labels.LabelsMerged > 0)
        {
            var how = bridge > TimeSpan.Zero
                ? string.Create(CultureInfo.InvariantCulture, $"overlapping, touching or within {bridge.TotalSeconds:0.###} s")
                : "overlapping or touching";
            summary += string.Create(CultureInfo.InvariantCulture, $", {labels.LabelsMerged} merged into a same-speaker neighbour ({how})");
        }

        context.WriteError(summary);

        foreach (var speaker in speakers)
        {
            var speech = labels.Turns.Where(t => t.Speaker == speaker).Sum(t => t.Duration.TotalSeconds);
            var count = labels.Turns.Count(t => t.Speaker == speaker);
            context.WriteError(string.Create(CultureInfo.InvariantCulture, $"  {speaker,-24} {count,5} turns  {speech,8:F1} s"));
        }

        var overlapped = OverlappedSeconds(labels.Turns);
        context.WriteError(string.Create(CultureInfo.InvariantCulture,
            $"  {"overlap",-24} {overlapped,8:F1} s where two or more speakers talk at once"));

        if (outputPath is { Length: > 0 })
        {
            context.WriteError($"  wrote {outputPath}");
        }

        return ExitCodes.Success;
    }

    /// <summary>Seconds during which at least two distinct speakers are active.</summary>
    internal static double OverlappedSeconds(IReadOnlyList<SpeakerTurn> turns)
    {
        var events = new List<(double Time, int Delta, string Speaker)>();
        foreach (var turn in turns)
        {
            events.Add((turn.Start.TotalSeconds, +1, turn.Speaker));
            events.Add((turn.End.TotalSeconds, -1, turn.Speaker));
        }

        events.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Delta.CompareTo(b.Delta));

        var active = new Dictionary<string, int>(StringComparer.Ordinal);
        double total = 0;
        double? openedAt = null;
        foreach (var (time, delta, speaker) in events)
        {
            active.TryGetValue(speaker, out var count);
            count += delta;
            if (count == 0)
            {
                active.Remove(speaker);
            }
            else
            {
                active[speaker] = count;
            }

            if (active.Count >= 2 && openedAt is null)
            {
                openedAt = time;
            }
            else if (active.Count < 2 && openedAt is { } start)
            {
                total += time - start;
                openedAt = null;
            }
        }

        return total;
    }
}
