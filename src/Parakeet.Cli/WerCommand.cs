using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Parakeet.Core.Text;

namespace Parakeet.Cli;

/// <summary>
/// Scores transcripts against a human reference: word error rate, with the alignment and the
/// normalisation both stated. This is the measurement Phase 0 was missing — every quantisation
/// figure before it was divergence from f16, which says how far a variant is from the reference
/// model and nothing about whether either is right.
///
/// <para>The score that is called "WER" here is computed over
/// <see cref="TranscriptNormalizer.WordErrorRateTokens"/> — lower-cased, punctuation gone,
/// hyphens split, fillers dropped — and is not comparable to a leaderboard figure for the same
/// model, which uses a richer normaliser. A raw figure over whitespace tokens is printed beside
/// it so the effect of the normalisation is visible rather than assumed.</para>
/// </summary>
internal static partial class WerCommand
{
    private const string NlpHeaderPrefix = "token|speaker|";

    public static int Run(CliContext context, ParsedCommandLine parsed)
    {
        var referencePath = parsed.Value("reference");
        var referenceDirectory = parsed.Value("reference-dir");
        if (string.IsNullOrEmpty(referencePath) == string.IsNullOrEmpty(referenceDirectory))
        {
            context.WriteError("wer needs exactly one of --reference <file> (one human transcript, scored against every " +
                               "hypothesis) or --reference-dir <dir> (a reference per hypothesis, matched by file stem).");
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
            context.WriteError("wer needs at least one hypothesis: a transcript .json or .txt this tool wrote.");
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

        var show = 0;
        if (parsed.Value("show") is { Length: > 0 } showText)
        {
            if (!CommandLineParser.TryParseInt(showText, out show) || show < 0)
            {
                context.WriteError($"--show needs a non-negative integer, got '{showText}'.");
                return ExitCodes.UsageError;
            }
        }

        var keepFillers = parsed.HasFlag("keep-fillers");
        var byCharacter = parsed.HasFlag("cer");
        var keepPunctuation = parsed.HasFlag("keep-punctuation");
        var asJson = parsed.HasFlag("json");
        var format = parsed.Value("reference-format") ?? "auto";
        if (format is not ("auto" or "text" or "nlp"))
        {
            context.WriteError($"--reference-format must be auto, text or nlp, got '{format}'.");
            return ExitCodes.UsageError;
        }

        if (keepPunctuation && !byCharacter)
        {
            context.WriteError("--keep-punctuation applies to --cer only: the word rule removes punctuation as part of " +
                               "splitting tokens and cannot keep it.");
            return ExitCodes.UsageError;
        }

        if (keepFillers && byCharacter)
        {
            context.WriteError("--keep-fillers is a word rule and does not apply to --cer: dropping a filler needs a word " +
                               "to recognise, and the character rule has none. Score without it, or drop --cer.");
            return ExitCodes.UsageError;
        }

        // The two tokenisers and the two metrics stay paired here, once, so no path can normalise
        // one way and score the other. `raw` is what the recipe removed: whitespace tokens before
        // the word normalisation, and punctuation-bearing characters before the character strip.
        string[] Normalise(string text) => byCharacter
            ? TranscriptNormalizer.CharacterErrorRateTokens(text, keepPunctuation)
            : TranscriptNormalizer.WordErrorRateTokens(text, keepFillers);

        string[] Raw(string text) => byCharacter
            ? TranscriptNormalizer.CharacterErrorRateTokens(text, keepPunctuation: true)
            : RawTokens(text);

        ErrorCounts ScoreRaw(string[] reference, string[] hypothesis) => byCharacter
            ? ErrorCounts.Of(CharacterErrorRate.Score(reference, hypothesis))
            : ErrorCounts.Of(WordErrorRate.Score(reference, hypothesis));

        var unit = byCharacter ? "characters" : "words";

        // One reference for all, read once; or one per hypothesis, found by stem beside the others.
        var references = new Dictionary<string, Reference>(StringComparer.Ordinal);
        Reference LoadReference(string path)
        {
            if (!references.TryGetValue(path, out var reference))
            {
                var nlp = format == "nlp" || (format == "auto" && Path.GetExtension(path).Equals(".nlp", StringComparison.OrdinalIgnoreCase));
                var text = ReadReference(path, nlp);
                reference = new Reference(path, Normalise(text), Raw(text));
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
                referenceForThis = FindReferenceByStem(referenceDirectory!, hypothesisPath);
                if (referenceForThis is null)
                {
                    context.WriteError($"No reference for {Path.GetFileName(hypothesisPath)} in {referenceDirectory}: " +
                                       $"expected {Path.GetFileNameWithoutExtension(hypothesisPath)}.txt or .nlp there.");
                    return ExitCodes.UsageError;
                }
            }

            var reference = LoadReference(referenceForThis);
            var hypothesis = ReadHypothesis(hypothesisPath);
            var normalised = Normalise(hypothesis);
            var raw = Raw(hypothesis);

            // Aligned once. The edit script is what --show renders, and the counts come off the
            // same alignment rather than a second one — on eleven hours of transcript that matters,
            // and by character there are six times as many tokens to align.
            var ops = WordAlignment.Align(reference.Normalised, normalised);
            var summary = WordAlignment.Summarize(ops);
            var scoredNormalised = new ErrorCounts(
                reference.Normalised.Length, normalised.Length,
                summary.Substitutions, summary.Deletions, summary.Insertions, unit);

            results.Add(new Scored(hypothesisPath, reference, scoredNormalised, ScoreRaw(reference.Raw, raw), ops, normalised));
        }

        var total = ErrorCounts.Sum(results.Select(r => r.Normalised), unit);
        var totalRaw = ErrorCounts.Sum(results.Select(r => r.Raw), unit);

        if (asJson)
        {
            WriteJson(context, keepFillers, byCharacter, keepPunctuation, results, total, totalRaw);
            return ExitCodes.Success;
        }

        if (referencePath is { Length: > 0 })
        {
            var only = references[referencePath];
            context.WriteLine($"reference   {referencePath}");
            context.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"            {only.Normalised.Length:N0} {unit} after normalisation, {only.Raw.Length:N0} before it"));
        }
        else
        {
            context.WriteLine($"references  {referenceDirectory}: one per hypothesis, matched by file stem");
        }

        if (byCharacter)
        {
            context.WriteLine($"normaliser  NFKC, whitespace dropped, brackets dropped, {(keepPunctuation ? "punctuation kept" : "punctuation removed")}, lower-cased");
            context.WriteLine("            characters are enumerated as runes, so a non-BMP kanji is one character and not two halves");
            context.WriteLine("            not NVIDIA's, kotoba's or Reazon's recipe: a figure from here compares to another from here");
            context.WriteLine("            CER and WER measure different things — never quote one beside the other");
        }
        else
        {
            context.WriteLine($"normaliser  lower-case, punctuation removed, hyphens split, brackets dropped, {(keepFillers ? "fillers kept" : "fillers (uh, um, hmm, mm, mhm, mmm) dropped")}");
            context.WriteLine("            not the leaderboard normaliser: numbers, spellings and contractions are compared as written");
        }

        var metric = byCharacter ? "CER" : "WER";
        context.WriteLine();
        context.WriteLine($"{"hypothesis",-40} {"ref " + unit,9} {unit,8} {metric,8} {"subs",7} {"dels",7} {"ins",7} {"raw " + metric,9}");

        foreach (var scored in results)
        {
            var label = Path.GetFileName(scored.Path);
            if (label.Length > 40) label = label[^40..];
            context.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{label,-40} {scored.Normalised.Reference,9:N0} {scored.Normalised.Hypothesis,8:N0} {Percent(scored.Normalised.Rate),8} {scored.Normalised.Substitutions,7:N0} {scored.Normalised.Deletions,7:N0} {scored.Normalised.Insertions,7:N0} {Percent(scored.Raw.Rate),9}"));
        }

        if (results.Count > 1)
        {
            context.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{"(all, summed)",-40} {total.Reference,9:N0} {total.Hypothesis,8:N0} {Percent(total.Rate),8} {total.Substitutions,7:N0} {total.Deletions,7:N0} {total.Insertions,7:N0} {Percent(totalRaw.Rate),9}"));
            if (referencePath is { Length: > 0 })
            {
                context.WriteLine("            summed over the same reference each time, read it only if these hypotheses are different files of one corpus");
            }
        }

        if (show > 0)
        {
            foreach (var scored in results)
            {
                context.WriteLine();
                context.WriteLine($"first {show} error sites in {Path.GetFileName(scored.Path)} (edits in capitals, * where one side has nothing):");
                WriteErrorSites(context, scored.Reference.Normalised, scored.HypothesisTokens, scored.Ops, show);
            }
        }

        return ExitCodes.Success;
    }

    private static string? FindReferenceByStem(string directory, string hypothesisPath)
    {
        var stem = Path.GetFileNameWithoutExtension(hypothesisPath);
        foreach (var extension in new[] { ".txt", ".nlp" })
        {
            var candidate = Path.Combine(directory, stem + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Percent(double rate) =>
        double.IsNaN(rate) ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{100 * rate:F2}%");

    private sealed record Reference(string Path, string[] Normalised, string[] Raw);

    /// <summary>
    /// One metric's counts carrying the unit they were counted in.
    ///
    /// <para><see cref="WordErrorRateResult"/> and <see cref="CharacterErrorRateResult"/> are
    /// deliberately different types so that mixing a word rate and a character rate has to be a
    /// decision rather than an accident. This is the one place the two meet, and it keeps the unit
    /// with the numbers precisely so that nothing below can print a character rate under a "words"
    /// heading — which is the mistake those two types exist to make hard.</para>
    /// </summary>
    private readonly record struct ErrorCounts(
        int Reference, int Hypothesis, int Substitutions, int Deletions, int Insertions, string Unit)
    {
        public int Errors => Substitutions + Deletions + Insertions;

        /// <summary>NaN over an empty reference, for the reason both metric types give.</summary>
        public double Rate => Reference == 0 ? double.NaN : (double)Errors / Reference;

        public static ErrorCounts Of(WordErrorRateResult r) => new(
            r.ReferenceWords, r.HypothesisWords, r.Substitutions, r.Deletions, r.Insertions, "words");

        public static ErrorCounts Of(CharacterErrorRateResult r) => new(
            r.ReferenceCharacters, r.HypothesisCharacters, r.Substitutions, r.Deletions, r.Insertions, "characters");

        /// <summary>
        /// The corpus figure: counts summed, so a long file weighs more than a short one — the same
        /// rule <see cref="WordErrorRate.Aggregate"/> and <see cref="CharacterErrorRate.Aggregate"/>
        /// state, applied here to the unit-tagged view the printer needs.
        /// </summary>
        public static ErrorCounts Sum(IEnumerable<ErrorCounts> counts, string unit)
        {
            int reference = 0, hypothesis = 0, substitutions = 0, deletions = 0, insertions = 0;
            foreach (var c in counts)
            {
                reference += c.Reference;
                hypothesis += c.Hypothesis;
                substitutions += c.Substitutions;
                deletions += c.Deletions;
                insertions += c.Insertions;
            }

            return new ErrorCounts(reference, hypothesis, substitutions, deletions, insertions, unit);
        }
    }

    private sealed record Scored(
        string Path,
        Reference Reference,
        ErrorCounts Normalised,
        ErrorCounts Raw,
        IReadOnlyList<AlignmentOp> Ops,
        string[] HypothesisTokens);

    // ── reading ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A hypothesis is something this tool wrote: the transcript JSON (its <c>text</c> field is the
    /// joined transcript) or the <c>.txt</c> output, whose <c>[hh:mm:ss]</c> line prefixes are not
    /// speech and are stripped. Anything else is read as plain text.
    /// </summary>
    internal static string ReadHypothesis(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".json")
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }

            throw new CliUsageException($"{path} is not a transcript JSON this tool wrote: it has no top-level \"text\" string.");
        }

        var content = File.ReadAllText(path);
        return extension == ".txt" ? TimestampPrefix().Replace(content, string.Empty) : content;
    }

    /// <summary>
    /// A reference is plain text, or an Earnings-22 <c>.nlp</c> file: pipe-separated, one token
    /// per line, header <c>token|speaker|ts|endTs|punctuation|case|tags|wer_tags</c>. The token
    /// column already carries the original casing; the punctuation column is what followed the
    /// token, appended here so the raw figure sees the transcript as written.
    /// </summary>
    internal static string ReadReference(string path, bool nlp)
    {
        var content = File.ReadAllText(path);
        if (!nlp)
        {
            // Human transcripts often carry [hh:mm:ss] markers at line starts, and this tool's own
            // .txt output always does. Neither is speech.
            return TimestampPrefix().Replace(content, string.Empty);
        }

        // The header names the columns, and the two layouts in the wild differ: the README's
        // token|speaker|ts|endTs|punctuation|case|tags|wer_tags, and the files' own
        // token|speaker|ts|endTs|punctuation|prepunctuation|case|tags. Reading by name serves both.
        var builder = new StringBuilder(content.Length);
        var tokenColumn = -1;
        var punctuationColumn = -1;
        var prepunctuationColumn = -1;
        var first = true;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (first)
            {
                first = false;
                if (!line.StartsWith(NlpHeaderPrefix, StringComparison.Ordinal))
                {
                    throw new CliUsageException(
                        $"{path} does not start with the .nlp header ('{NlpHeaderPrefix}...'); pass --reference-format text to read it as plain text.");
                }

                var names = line.Split('|');
                tokenColumn = Array.IndexOf(names, "token");
                punctuationColumn = Array.IndexOf(names, "punctuation");
                prepunctuationColumn = Array.IndexOf(names, "prepunctuation");
                continue;
            }

            var columns = line.Split('|');
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            if (prepunctuationColumn >= 0 && prepunctuationColumn < columns.Length)
            {
                builder.Append(columns[prepunctuationColumn]);
            }

            builder.Append(columns[tokenColumn]);
            if (punctuationColumn >= 0 && punctuationColumn < columns.Length)
            {
                builder.Append(columns[punctuationColumn]);
            }
        }

        return builder.ToString();
    }

    private static string[] RawTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── output ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs of consecutive edits, each shown with three words of context either side, reference
    /// over hypothesis. Enough to see whether the errors are numbers, names, fillers or real words.
    /// </summary>
    private static void WriteErrorSites(
        CliContext context, string[] reference, string[] hypothesis, IReadOnlyList<AlignmentOp> ops, int limit)
    {
        const int Context = 3;
        var shown = 0;
        var i = 0;
        while (i < ops.Count && shown < limit)
        {
            if (ops[i].Kind == AlignmentOpKind.Match)
            {
                i++;
                continue;
            }

            var end = i;
            while (end < ops.Count && ops[end].Kind != AlignmentOpKind.Match)
            {
                end++;
            }

            var from = Math.Max(0, i - Context);
            var to = Math.Min(ops.Count, end + Context);
            var referenceLine = new StringBuilder("  ref: ");
            var hypothesisLine = new StringBuilder("  hyp: ");

            for (var k = from; k < to; k++)
            {
                var op = ops[k];
                var refWord = op.ReferenceIndex >= 0 ? reference[op.ReferenceIndex] : "";
                var hypWord = op.HypothesisIndex >= 0 ? hypothesis[op.HypothesisIndex] : "";
                var width = Math.Max(refWord.Length, hypWord.Length);
                if (op.Kind != AlignmentOpKind.Match)
                {
                    refWord = op.ReferenceIndex >= 0 ? refWord.ToUpperInvariant() : "*";
                    hypWord = op.HypothesisIndex >= 0 ? hypWord.ToUpperInvariant() : "*";
                    width = Math.Max(refWord.Length, hypWord.Length);
                }

                referenceLine.Append(refWord.PadRight(width)).Append(' ');
                hypothesisLine.Append(hypWord.PadRight(width)).Append(' ');
            }

            var at = ops[i].ReferenceIndex >= 0 ? ops[i].ReferenceIndex : (i > 0 ? Math.Max(0, ops[i - 1].ReferenceIndex) : 0);
            context.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  at reference word {at:N0}:"));
            context.WriteLine(referenceLine.ToString().TrimEnd());
            context.WriteLine(hypothesisLine.ToString().TrimEnd());
            shown++;
            i = end;
        }
    }

    private static void WriteJson(
        CliContext context, bool keepFillers, bool byCharacter, bool keepPunctuation,
        List<Scored> results, ErrorCounts total, ErrorCounts totalRaw)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            // Which metric produced every number below. A reader that ignores this and assumes a
            // word rate is the failure this field exists to prevent, which is also why the
            // per-result counts carry their unit rather than a `referenceWords` key that would be
            // a lie by half the time.
            writer.WriteString("metric", byCharacter ? "cer" : "wer");
            writer.WriteString("unit", byCharacter ? "characters" : "words");
            writer.WriteString("normaliser", byCharacter
                ? (keepPunctuation ? "nfkc, punctuation kept" : "nfkc, punctuation removed")
                : (keepFillers ? "basic, fillers kept" : "basic, fillers dropped"));
            writer.WriteStartArray("hypotheses");
            foreach (var scored in results)
            {
                writer.WriteStartObject();
                writer.WriteString("path", scored.Path);
                writer.WriteString("reference", scored.Reference.Path);
                WriteResult(writer, "normalised", scored.Normalised);
                WriteResult(writer, "raw", scored.Raw);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("summed");
            WriteResult(writer, "normalised", total);
            WriteResult(writer, "raw", totalRaw);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        context.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteResult(Utf8JsonWriter writer, string name, ErrorCounts result)
    {
        writer.WriteStartObject(name);
        writer.WriteString("unit", result.Unit);
        writer.WriteNumber("reference", result.Reference);
        writer.WriteNumber("hypothesis", result.Hypothesis);

        // Kept for readers written before the character metric existed, and emitted only where the
        // name is true. In character mode there is no `referenceWords` key at all, so a consumer
        // that silently expects one fails loudly instead of reading characters as words.
        if (result.Unit == "words")
        {
            writer.WriteNumber("referenceWords", result.Reference);
            writer.WriteNumber("hypothesisWords", result.Hypothesis);
        }

        writer.WriteNumber("substitutions", result.Substitutions);
        writer.WriteNumber("deletions", result.Deletions);
        writer.WriteNumber("insertions", result.Insertions);
        writer.WriteNumber("errors", result.Errors);
        if (double.IsNaN(result.Rate))
        {
            writer.WriteNull("rate");
        }
        else
        {
            writer.WriteNumber("rate", Math.Round(result.Rate, 6));
        }

        writer.WriteEndObject();
    }

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\]\s*", RegexOptions.Multiline)]
    private static partial Regex TimestampPrefix();
}
