using System.Diagnostics;
using System.Globalization;
using Parakeet.Core.Formatting;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;
using Parakeet.Engine.Python;

namespace Parakeet.Cli;

/// <summary>
/// English, and nothing else: text in, text out, no audio and no ASR.
/// </summary>
/// <remarks>
/// <para>
/// <c>transcribe --translate</c> already writes English, but it transcribes first, and the ASR pass
/// costs orders of magnitude more than the translator does. Holding the decode loop to a reference
/// corpus through that path would mean hours of Parakeet decoding to produce text the ASR
/// contributes nothing to — and worse, it would mean having audio for the reference sentences,
/// which for a text corpus like FLEURS' transcripts nobody does. This is the path the translation
/// measurements are run through, and it is the same translator behind the same seam, so what is
/// measured is what the product runs.
/// </para>
/// <para>
/// <b>One line in, one line out, in order.</b> A blank line comes back blank rather than being
/// dropped, for the same reason the translation contract yields an empty segment rather than
/// skipping it: a file whose line numbers no longer line up is a file nothing can be scored
/// against, and the misalignment is invisible until somebody reads the two side by side.
/// </para>
/// <para>
/// There is deliberately no beam, context or length option. Those are the degrees of freedom that
/// decide what English comes out, every published figure for this model was produced at one setting
/// of them, and a flag would make it easy to produce a number that describes nothing.
/// </para>
/// </remarks>
internal static class TranslateCommand
{
    public static async Task<int> RunAsync(CliContext context, ParsedCommandLine parsed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parsed);

        if (parsed.Positionals.Count == 0)
        {
            context.WriteError("translate needs at least one text file.");
            return ExitCodes.UsageError;
        }

        var id = parsed.Value("id");
        if (id is { Length: > 0 } && parsed.Positionals.Count > 1)
        {
            context.WriteError("--id names one output, so it takes one input file.");
            return ExitCodes.UsageError;
        }

        foreach (var path in parsed.Positionals)
        {
            if (!File.Exists(path))
            {
                context.WriteError($"File not found: {path}");
                return ExitCodes.UsageError;
            }
        }

        await using var translator = await TranslatorFactory.CreateAsync(
            context,
            new TranslatorRequest
            {
                Fake = parsed.HasFlag("fake"),
                ModelId = parsed.Value("model"),
                ModelPath = parsed.Value("model-path"),
                Threads = TranscribeCommand.ParseThreads(parsed.Value("threads"), "--threads"),
                Backend = parsed.Value("backend"),
                AllowUnverifiedBackend = parsed.HasFlag("backend-unverified"),
                BackendOption = "--backend",
            },
            ct).ConfigureAwait(false);

        // The factory has already loaded it, so these capabilities are the sidecar's own answers
        // rather than this side's expectation of them.
        TranslatorFactory.Check(translator, []);

        var capabilities = translator.Capabilities;
        context.WriteError(
            $"{capabilities.ModelId ?? capabilities.EngineName}: into {TranslationTarget.LanguageTag} only, " +
            $"{capabilities.Backend.ToString().ToLowerInvariant()}, no word timings" +
            $"{(capabilities.MaxSourceTokens is { } cap ? $", sources over {cap} tokens refused" : string.Empty)}.");

        // The search, beside the graph. The graphs are pinned and the search over them is not, so a
        // scoring run that records only which checkpoint ran has recorded half of what produced its
        // English — beam width alone moved this project's own measured output.
        if (translator.Capabilities.DecodeDescription is { } decode)
        {
            context.WriteError($"Decode: {decode}.");
        }

        return await TranslateFilesAsync(
            context, translator, parsed.Positionals, parsed.Value("out"), id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The verb's work once the translator exists: one output per input, line for line.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RunAsync"/> so that a test can hand it a translator with a token
    /// limit, which the <c>--fake</c> one has no flag for — and a flag whose only purpose is to make
    /// a fake refuse would be a flag in the product for the tests' sake.
    /// </remarks>
    internal static async Task<int> TranslateFilesAsync(
        CliContext context,
        ITranscriptTranslator translator,
        IReadOnlyList<string> inputs,
        string? outputDirectory,
        string? id,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(inputs);

        if (outputDirectory is { Length: > 0 })
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // Two inputs can name one output — a/es.txt and b/es.txt with -o, which is the shape a
        // corpus takes. Overwriting an output from an earlier run is ordinary and stays allowed;
        // overwriting one written moments ago in this run is a file the user asked for and will not
        // get, so it stops here rather than leaving a scorer to report n-1 files as a complete set.
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var totalLines = 0;
        var totalElapsed = TimeSpan.Zero;

        foreach (var path in inputs)
        {
            ct.ThrowIfCancellationRequested();

            var stem = id is { Length: > 0 } ? id : Path.GetFileNameWithoutExtension(path);
            var destination = Path.Combine(
                outputDirectory is { Length: > 0 } ? outputDirectory : Path.GetDirectoryName(path) ?? ".",
                stem + ".en.txt");

            if (written.TryGetValue(destination, out var earlier))
            {
                context.WriteError(
                    $"{path} and {earlier} would both be written to {destination}. Give them different names, " +
                    "run them separately, or use --id on one file at a time.");
                return ExitCodes.UsageError;
            }

            // A destination that is also an input — `translate a.txt a.en.txt`, where the second
            // input is the first one's output name — would be overwritten before it is read. Until
            // 2026-08-22 only a second destination was checked against, not the inputs.
            var destinationFull = Path.GetFullPath(destination);
            if (inputs.FirstOrDefault(input => string.Equals(Path.GetFullPath(input), destinationFull, PathComparison)) is { } clobbered)
            {
                context.WriteError(
                    $"{path} would be written to {destination}, which is also an input ({clobbered}) and would be " +
                    "overwritten before it is read. Rename it, leave it out, or use --id or --out to put the output " +
                    "somewhere else.");
                return ExitCodes.UsageError;
            }

            written.Add(destination, path);

            var lines = File.ReadAllLines(path);
            var segments = ToSegments(lines);

            var started = Stopwatch.GetTimestamp();
            var english = new List<string>(lines.Length);

            try
            {
                await foreach (var segment in translator
                    .TranslateAsync(segments, TranslationOptions.Default, progress: null, ct)
                    .ConfigureAwait(false))
                {
                    english.Add(segment.Text);
                }
            }
            catch (SegmentTooLongException refused)
            {
                // The refusal the help promises, by line number — counted from one, because a line
                // number is what the user has in front of them, where the exception counts segments
                // from zero for the transcript case. Until 2026-08-22 this was a stack trace.
                var detail = refused.Limit > 0
                    ? $"line {refused.SegmentIndex + 1} is {refused.Tokens} tokens, past this translator's limit of {refused.Limit}"
                    : $"line {refused.SegmentIndex + 1}: {refused.Message}";
                context.WriteError(
                    $"{path}: {detail}. It is refused rather than truncated: a shortened source comes back as fluent " +
                    "English with no sign that anything was dropped, so split the line and run again. Nothing was " +
                    "written for this file.");
                return ExitCodes.RuntimeError;
            }
            catch (InvalidOperationException broken)
            {
                // The translator broke its own contract — a defect to report in its own words, not
                // a trace to decode.
                context.WriteError($"{path}: {broken.Message}");
                return ExitCodes.RuntimeError;
            }

            var elapsed = Stopwatch.GetElapsedTime(started);

            if (english.Count != lines.Length)
            {
                // The driver enforces this for a transcript; this command builds its own segments,
                // so it checks its own counts rather than trusting them.
                context.WriteError(
                    $"{path}: {lines.Length} lines in and {english.Count} out. A pass that loses lines loses text.");
                return ExitCodes.RuntimeError;
            }

            File.WriteAllLines(destination, english, TextOutput.Utf8NoBom);

            // The same flag `transcribe --translate` carries, because the failure it points at is a
            // property of the translation and not of where the text came from. Reported per line
            // rather than through TranslationNumerals.Describe: that names segments by timestamp,
            // and this command's segments are one synthetic second apiece, so "[00:03]" would be a
            // time that never existed where a line number is what the user has in front of them.
            var lostNumbers = new List<string>();
            for (var i = 0; i < english.Count; i++)
            {
                var missing = TranslationNumerals.Missing(lines[i], english[i]);
                if (missing.Count > 0)
                {
                    lostNumbers.Add($"line {i + 1} ({string.Join(", ", missing)})");
                }
            }

            if (lostNumbers.Count > 0)
            {
                context.WriteError(
                    $"{stem}: {lostNumbers.Count} of {lines.Length} lines carry a number the English does not, " +
                    string.Join("; ", lostNumbers.Take(5)) +
                    (lostNumbers.Count > 5 ? $"; and {lostNumbers.Count - 5} more" : string.Empty) +
                    ". A date or a quantity that changed in translation reads as confidently as one that did not.");
            }

            totalLines += lines.Length;
            totalElapsed += elapsed;

            context.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{stem}: {lines.Length} lines in {elapsed.TotalSeconds:F1} s " +
                $"({elapsed.TotalSeconds / Math.Max(1, lines.Length):F3} s/line) -> {destination}"));
        }

        if (inputs.Count > 1)
        {
            context.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{inputs.Count} files: {totalLines} lines in {totalElapsed.TotalMinutes:F1} min = " +
                $"{totalElapsed.TotalSeconds / Math.Max(1, totalLines):F3} s/line"));
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// One segment per line, timed by line number.
    /// </summary>
    /// <remarks>
    /// The times are the line's index and are honestly synthetic — there is no audio here and no
    /// clock to take them from. They exist because the seam takes segments and a segment has a
    /// start and an end, and the translator is required to hand them back untouched, which this
    /// command relies on to keep the output in the input's order.
    /// </remarks>
    /// <summary>Windows paths compare without case; elsewhere they do not.</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static IReadOnlyList<TranscriptSegment> ToSegments(IReadOnlyList<string> lines)
    {
        var segments = new List<TranscriptSegment>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            segments.Add(new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(i),
                End = TimeSpan.FromSeconds(i + 1),
                Text = lines[i],
                SourceSegmentIndex = i,
            });
        }

        return segments;
    }
}
