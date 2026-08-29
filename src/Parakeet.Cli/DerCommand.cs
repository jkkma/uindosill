using System.Globalization;
using System.Text;
using System.Text.Json;
using Parakeet.Core.Diarisation;

namespace Parakeet.Cli;

/// <summary>
/// Scores speaker turns against a hand-labelled reference: diarisation error rate, with the
/// convention stated on every line. This is the diarisation harness's instrument — the number the
/// ship gate is written in — and it is validated against pyannote.metrics on committed fixture
/// pairs (<c>tests/fixtures/diarisation/scorer/</c>) before any figure from it is trusted.
///
/// <para>Three numbers per hypothesis, always together: the headline (collar 0.25 s, overlap
/// included — the convention of the one external benchmark the study keeps comparable), the strict
/// number (collar 0, same overlap setting), and the same components over reference-overlap regions
/// only, so how the system does on crosstalk is measured rather than averaged away.</para>
/// </summary>
internal static class DerCommand
{
    public static int Run(CliContext context, ParsedCommandLine parsed)
    {
        var referencePath = parsed.Value("reference");
        var referenceDirectory = parsed.Value("reference-dir");
        if (string.IsNullOrEmpty(referencePath) == string.IsNullOrEmpty(referenceDirectory))
        {
            context.WriteError("der needs exactly one of --reference <file.rttm> (one reference, scored against every " +
                               "hypothesis) or --reference-dir <dir> (a <stem>.rttm per hypothesis, matched by file stem).");
            return ExitCodes.UsageError;
        }

        if (referencePath is { Length: > 0 } && !File.Exists(referencePath))
        {
            context.WriteError($"Reference not found: {referencePath}");
            return ExitCodes.UsageError;
        }

        if (referenceDirectory is { Length: > 0 } && !Directory.Exists(referenceDirectory))
        {
            context.WriteError($"Reference directory not found: {referenceDirectory}");
            return ExitCodes.UsageError;
        }

        if (parsed.Positionals.Count == 0)
        {
            context.WriteError("der needs at least one hypothesis: an RTTM file of speaker turns.");
            return ExitCodes.UsageError;
        }

        foreach (var hypothesisPath in parsed.Positionals)
        {
            if (!File.Exists(hypothesisPath))
            {
                context.WriteError($"Hypothesis not found: {hypothesisPath}");
                return ExitCodes.UsageError;
            }
        }

        var collar = DiarisationScoringOptions.Default.Collar;
        if (parsed.Value("collar") is { Length: > 0 } collarText)
        {
            // An hour is already absurd for a collar; the cap keeps a typo from overflowing TimeSpan.
            if (!CommandLineParser.TryParseDouble(collarText, out var seconds) || !double.IsFinite(seconds) || seconds < 0 || seconds > 3600)
            {
                context.WriteError($"--collar needs a number of seconds between 0 and 3600, got '{collarText}'.");
                return ExitCodes.UsageError;
            }

            collar = SpeakerTurns.FromSeconds(seconds);
        }

        var headline = new DiarisationScoringOptions { Collar = collar, SkipOverlap = parsed.HasFlag("skip-overlap") };
        var strict = headline with { Collar = TimeSpan.Zero };
        var asJson = parsed.HasFlag("json");

        var references = new Dictionary<string, Reference>(StringComparer.Ordinal);
        Reference LoadReference(string path)
        {
            if (!references.TryGetValue(path, out var reference))
            {
                reference = new Reference(path, ReadRttm(path));
                references.Add(path, reference);
            }

            return reference;
        }

        var results = new List<Scored>();
        foreach (var hypothesisPath in parsed.Positionals)
        {
            var referenceForThis = referencePath;
            if (referenceForThis is null or "")
            {
                referenceForThis = Path.Combine(referenceDirectory!, Path.GetFileNameWithoutExtension(hypothesisPath) + ".rttm");
                if (!File.Exists(referenceForThis))
                {
                    context.WriteError($"No reference for {Path.GetFileName(hypothesisPath)} in {referenceDirectory}: " +
                                       $"expected {Path.GetFileNameWithoutExtension(hypothesisPath)}.rttm there.");
                    return ExitCodes.UsageError;
                }
            }

            var reference = LoadReference(referenceForThis);
            var hypothesis = ReadRttm(hypothesisPath);

            var warnings = new List<string>();
            if (hypothesis.FileIds.Count > 0 && reference.Document.FileIds.Count > 0
                && !hypothesis.FileIds.SequenceEqual(reference.Document.FileIds, StringComparer.Ordinal))
            {
                warnings.Add($"file id differs: reference says '{string.Join(",", reference.Document.FileIds)}', hypothesis says '{string.Join(",", hypothesis.FileIds)}'. Scored anyway, the id is informational, but check the pairing.");
            }

            DiarisationScore score;
            DiarisationScore strictScore;
            try
            {
                score = DiarisationErrorRate.Score(reference.Document.Turns, hypothesis.Turns, headline);
                strictScore = DiarisationErrorRate.Score(reference.Document.Turns, hypothesis.Turns, strict);
            }
            catch (InvalidOperationException ex)
            {
                // The mapping search gave up: its message names the speaker counts and what to do.
                throw new CliUsageException($"{Path.GetFileName(hypothesisPath)}: {ex.Message}", ex);
            }

            warnings.AddRange(score.Warnings);

            results.Add(new Scored(hypothesisPath, reference, hypothesis, score, strictScore, warnings));
        }

        if (asJson)
        {
            WriteJson(context, headline, results);
            return ExitCodes.Success;
        }

        WriteTable(context, headline, referencePath, referenceDirectory, references, results);
        return ExitCodes.Success;
    }

    /// <summary>
    /// One RTTM in, turns out. A file that carries several file ids is refused: the scorer pairs
    /// one reference file with one hypothesis file, and a multi-file RTTM scored as one would
    /// silently mix stretches.
    /// </summary>
    internal static RttmDocument ReadRttm(string path)
    {
        RttmDocument document;
        try
        {
            document = RttmFile.Parse(File.ReadAllText(path));
        }
        catch (FormatException ex)
        {
            throw new CliUsageException($"{path}: {ex.Message}", ex);
        }

        if (document.FileIds.Count > 1)
        {
            throw new CliUsageException(
                $"{path} carries {document.FileIds.Count} file ids ({string.Join(", ", document.FileIds)}); " +
                "score one file per RTTM.");
        }

        return document;
    }

    private sealed record Reference(string Path, RttmDocument Document);

    private sealed record Scored(
        string Path,
        Reference Reference,
        RttmDocument Hypothesis,
        DiarisationScore Headline,
        DiarisationScore Strict,
        IReadOnlyList<string> Warnings);

    // ── output ───────────────────────────────────────────────────────────────────────────────

    private static void WriteTable(
        CliContext context,
        DiarisationScoringOptions headline,
        string? referencePath,
        string? referenceDirectory,
        Dictionary<string, Reference> references,
        List<Scored> results)
    {
        if (referencePath is { Length: > 0 })
        {
            var only = references[referencePath];
            context.WriteLine($"reference   {referencePath}");
            context.WriteLine(DescribeTurns(only.Document.Turns));
        }
        else
        {
            context.WriteLine($"references  {referenceDirectory}: one <stem>.rttm per hypothesis");
        }

        context.WriteLine($"convention  {headline.Describe()}");
        context.WriteLine("            pyannote.metrics semantics: the collar is a total width centred on each reference boundary;");
        context.WriteLine("            NIST md-eval and NeMo quote a half-width, so their \"collar 0.25\" is --collar 0.5 here.");
        context.WriteLine($"            beside it: the strict number at collar 0, and the same components over reference-overlap regions only");
        context.WriteLine();
        context.WriteLine($"{"hypothesis",-32} {"DER",8} {"miss",7} {"FA",7} {"conf",7} {"DER@c0",8} {"ovl DER",8} {"ovl miss",8} {"ref s",8} {"spk r/h",7}");

        var labels = Labels(results.Select(r => r.Path).ToList());
        foreach (var scored in results)
        {
            context.WriteLine(Row(labels[scored.Path], scored.Headline, scored.Strict));
        }

        if (results.Count > 1)
        {
            var headlineTotal = DiarisationErrorRate.Aggregate(results.Select(r => r.Headline.Overall));
            var strictTotal = DiarisationErrorRate.Aggregate(results.Select(r => r.Strict.Overall));
            var overlapTotal = DiarisationErrorRate.Aggregate(results.Select(r => r.Headline.OverlapRegions));
            context.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{"(all, summed)",-32} {Percent(headlineTotal.Rate),8} {Percent(headlineTotal.MissedRate),7} {Percent(headlineTotal.FalseAlarmRate),7} {Percent(headlineTotal.ConfusionRate),7} {Percent(strictTotal.Rate),8} {Percent(overlapTotal.Rate),8} {Percent(overlapTotal.MissedRate),8} {headlineTotal.ReferenceSpeech,8:F1} {"",7}"));
            context.WriteLine("            summed components over summed reference speech: the set's DER, weighting a long file more than a short one");
        }

        foreach (var scored in results)
        {
            context.WriteLine();
            context.WriteLine($"{labels[scored.Path]}:");
            context.WriteLine($"  hypothesis  {DescribeTurns(scored.Hypothesis.Turns).Trim()}");
            if (referenceDirectory is { Length: > 0 })
            {
                context.WriteLine($"  reference   {scored.Reference.Path}");
                context.WriteLine($"              {DescribeTurns(scored.Reference.Document.Turns).Trim()}");
            }

            var mapping = scored.Headline.Mapping.Count == 0
                ? "(no hypothesis speaker co-occurs with any reference speaker)"
                : string.Join(", ", scored.Headline.Mapping.OrderBy(kv => kv.Value, StringComparer.Ordinal).Select(kv => $"{kv.Key}→{kv.Value}"));
            var unmapped = scored.Headline.HypothesisSpeakers.Where(s => !scored.Headline.Mapping.ContainsKey(s)).ToList();
            context.WriteLine($"  mapping     {mapping}" + (unmapped.Count > 0 ? $"; unmatched: {string.Join(", ", unmapped)}" : string.Empty));
            context.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  overlap     {scored.Headline.OverlapRegions.ReferenceSpeech:F1} s of reference speech falls where two or more reference speakers talk at once " +
                $"({(scored.Headline.Overall.ReferenceSpeech > 0 ? 100 * scored.Headline.OverlapRegions.ReferenceSpeech / scored.Headline.Overall.ReferenceSpeech : 0):F1}% of it); " +
                $"there: DER {Percent(scored.Headline.OverlapRegions.Rate)}, miss {Percent(scored.Headline.OverlapRegions.MissedRate)}, confusion {Percent(scored.Headline.OverlapRegions.ConfusionRate)}"));

            foreach (var warning in scored.Warnings)
            {
                context.WriteLine($"  note        {warning}");
            }
        }
    }

    private static string Row(string label, DiarisationScore headline, DiarisationScore strict) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{label,-32} {Percent(headline.Overall.Rate),8} {Percent(headline.Overall.MissedRate),7} {Percent(headline.Overall.FalseAlarmRate),7} {Percent(headline.Overall.ConfusionRate),7} {Percent(strict.Overall.Rate),8} {Percent(headline.OverlapRegions.Rate),8} {Percent(headline.OverlapRegions.MissedRate),8} {headline.Overall.ReferenceSpeech,8:F1} {headline.ReferenceSpeakers.Count + "/" + headline.HypothesisSpeakers.Count,7}");

    /// <summary>
    /// One label per hypothesis: the file name, or — when two hypotheses share one, which is what
    /// scoring the same stretch from two systems looks like — the parent directory and the file
    /// name, so the rows can be told apart. The last 32 characters either way.
    /// </summary>
    private static Dictionary<string, string> Labels(List<string> paths)
    {
        var byName = paths.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in byName)
        {
            foreach (var path in group)
            {
                var label = group.Count() == 1
                    ? Path.GetFileName(path)
                    : Path.Combine(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty), Path.GetFileName(path));
                labels[path] = label.Length > 32 ? label[^32..] : label;
            }
        }

        return labels;
    }

    private static string DescribeTurns(IReadOnlyList<SpeakerTurn> turns)
    {
        var speakers = SpeakerTurns.Speakers(turns);
        var speech = turns.Sum(t => t.Duration.TotalSeconds);
        var extent = turns.Count == 0 ? 0 : turns.Max(t => t.End.TotalSeconds) - turns.Min(t => t.Start.TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture,
            $"            {speakers.Count} speaker{(speakers.Count == 1 ? "" : "s")} ({string.Join(", ", speakers)}), {turns.Count} turns, {speech:F1} s of speech over {extent:F1} s");
    }

    private static string Percent(double rate) =>
        double.IsNaN(rate) ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{100 * rate:F2}%");

    private static void WriteJson(CliContext context, DiarisationScoringOptions headline, List<Scored> results)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("convention");
            writer.WriteNumber("collarSeconds", headline.Collar.TotalSeconds);
            writer.WriteString("collarSemantics", "pyannote.metrics: total width centred on each reference boundary (md-eval and NeMo quote the half-width)");
            writer.WriteBoolean("skipOverlap", headline.SkipOverlap);
            writer.WriteString("description", headline.Describe());
            writer.WriteEndObject();

            writer.WriteStartArray("hypotheses");
            foreach (var scored in results)
            {
                writer.WriteStartObject();
                writer.WriteString("path", scored.Path);
                writer.WriteString("reference", scored.Reference.Path);
                writer.WriteString("fileId", scored.Reference.Document.FileIds.FirstOrDefault() ?? scored.Hypothesis.FileIds.FirstOrDefault());
                WriteComponents(writer, "headline", scored.Headline.Overall);
                WriteComponents(writer, "strict", scored.Strict.Overall);
                WriteComponents(writer, "overlapRegions", scored.Headline.OverlapRegions);
                writer.WriteNumber("scoredSeconds", Round(scored.Headline.ScoredSeconds));

                writer.WriteStartArray("referenceSpeakers");
                foreach (var speaker in scored.Headline.ReferenceSpeakers)
                {
                    writer.WriteStringValue(speaker);
                }

                writer.WriteEndArray();

                writer.WriteStartArray("hypothesisSpeakers");
                foreach (var speaker in scored.Headline.HypothesisSpeakers)
                {
                    writer.WriteStringValue(speaker);
                }

                writer.WriteEndArray();

                writer.WriteStartObject("mapping");
                foreach (var pair in scored.Headline.Mapping.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(pair.Key, pair.Value);
                }

                writer.WriteEndObject();

                writer.WriteStartArray("warnings");
                foreach (var warning in scored.Warnings)
                {
                    writer.WriteStringValue(warning);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartObject("summed");
            WriteComponents(writer, "headline", DiarisationErrorRate.Aggregate(results.Select(r => r.Headline.Overall)));
            WriteComponents(writer, "strict", DiarisationErrorRate.Aggregate(results.Select(r => r.Strict.Overall)));
            WriteComponents(writer, "overlapRegions", DiarisationErrorRate.Aggregate(results.Select(r => r.Headline.OverlapRegions)));
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        context.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteComponents(Utf8JsonWriter writer, string name, DiarisationErrorComponents components)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("referenceSpeechSeconds", Round(components.ReferenceSpeech));
        writer.WriteNumber("missedSeconds", Round(components.Missed));
        writer.WriteNumber("falseAlarmSeconds", Round(components.FalseAlarm));
        writer.WriteNumber("confusionSeconds", Round(components.Confusion));
        writer.WriteNumber("correctSeconds", Round(components.Correct));
        WriteRate(writer, "rate", components.Rate);
        WriteRate(writer, "missedRate", components.MissedRate);
        WriteRate(writer, "falseAlarmRate", components.FalseAlarmRate);
        WriteRate(writer, "confusionRate", components.ConfusionRate);
        writer.WriteEndObject();
    }

    private static void WriteRate(Utf8JsonWriter writer, string name, double rate)
    {
        if (double.IsNaN(rate))
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, Math.Round(rate, 6));
        }
    }

    // Six decimals of a second is a microsecond, pyannote.core's own segment precision; anything
    // finer is floating-point residue from the collar arithmetic.
    private static double Round(double seconds) => Math.Round(seconds, 6);
}
