using System.Diagnostics;
using System.Globalization;
using Parakeet.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Formatting;

namespace Parakeet.Cli;

/// <summary>
/// Speaker turns, and nothing else: audio in, RTTM out, no transcription.
/// </summary>
/// <remarks>
/// <para>
/// <c>transcribe --speakers -f rttm</c> already writes speaker turns, but it transcribes first, and
/// the ASR pass costs orders of magnitude more than the diariser does. Scoring the diariser against
/// a reference corpus through that path would mean nine hours of Parakeet decoding to produce a
/// file the ASR contributes nothing to. This is the path the measurement uses, and it is the same
/// labeller behind the same seam — so what it scores is what the product runs.
/// </para>
/// <para>
/// Pairs with <c>uindosill der</c>, which matches hypotheses to references by file stem. That is
/// what <c>--id</c> is for: AMI's audio is <c>ES2004a.Mix-Headset.wav</c> and its reference is
/// <c>ES2004a.rttm</c>, so the name has to be settable rather than derived.
/// </para>
/// </remarks>
internal static class DiariseCommand
{
    public static async Task<int> RunAsync(CliContext context, ParsedCommandLine parsed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parsed);

        if (parsed.Positionals.Count == 0)
        {
            context.WriteError("diarise needs at least one audio file.");
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

        var options = new SpeakerLabellingOptions
        {
            SpeakerCount = ParseSpeakerCount(parsed),

            // Raw labels, not "Speaker 1": the model's own column is what the speaker cache works to
            // keep stable across a recording, and a scorer wants to see what the model produced.
            DisplayNameFormat = null,
        };
        options.Validate();

        await using var labeller = await LabellerFactory.CreateAsync(
            context,
            new LabellerRequest
            {
                Fake = parsed.HasFlag("fake"),
                ModelId = parsed.Value("model"),
                ModelPath = parsed.Value("model-path"),
                Threads = TranscribeCommand.ParseThreads(parsed.Value("threads"), "--threads"),
                Backend = parsed.Value("backend"),
                AllowUnverifiedBackend = parsed.HasFlag("backend-unverified"),
                BackendOption = "--backend",
            },
            options,
            ct).ConfigureAwait(false);

        await labeller.LoadAsync(ct).ConfigureAwait(false);

        // The standing note about the cap, for a run that did not ask for a count. When one was
        // asked for and it is above the cap, LabellerFactory has already said so in the terms that
        // name the user's own number, and saying the same thing twice in weaker words dilutes it.
        if (labeller.Capabilities.MaxSpeakers is { } cap
            && SpeakerLabelling.DescribeUnreachableCount(labeller.Capabilities, options.SpeakerCount) is null)
        {
            context.WriteError(
                $"{labeller.Capabilities.ModelId}: at most {cap} speakers can be told apart; a further voice is " +
                "merged into one of them rather than reported.");
        }

        var outputDirectory = parsed.Value("out");
        if (outputDirectory is { Length: > 0 })
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var totalAudio = TimeSpan.Zero;
        var totalElapsed = TimeSpan.Zero;

        // Two inputs can name one output — `a/meeting.wav` and `b/meeting.wav` with -o, which is
        // exactly the shape a corpus takes. Overwriting an output from an EARLIER run is ordinary
        // and stays allowed; overwriting one written moments ago in this run is a file the user
        // asked for and will not get, so it stops here rather than at the scorer, which would have
        // silently reported n-1 files as a complete set.
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in parsed.Positionals)
        {
            ct.ThrowIfCancellationRequested();

            // Sanitised, because RTTM splits on whitespace and `RttmFile.Write` refuses an id with
            // any in it — after the diariser has run the whole recording. `transcribe -f rttm` has
            // always done this; this command has to as well, and through the same function so the
            // two cannot drift.
            var stem = RttmFile.SanitiseFileId(id is { Length: > 0 } ? id : Path.GetFileNameWithoutExtension(path));
            var destination = Path.Combine(
                outputDirectory is { Length: > 0 } ? outputDirectory : Path.GetDirectoryName(path) ?? ".",
                stem + ".rttm");

            if (written.TryGetValue(destination, out var earlier))
            {
                context.WriteError(
                    $"{path} and {earlier} would both be written to {destination}. Give them different names, " +
                    "run them separately, or use --id on one file at a time.");
                return ExitCodes.UsageError;
            }

            written.Add(destination, path);

            var started = Stopwatch.GetTimestamp();
            await using var audio = AudioSources.Open(path);
            var duration = audio.Duration;

            // Per file, because it is a fact about the file. Opening a container reads its header,
            // not its audio, so this still lands before the labeller has decoded a sample.
            if (SpeakerLabelling.DescribeDurationRisk(labeller.Capabilities, duration) is { } longRun)
            {
                context.WriteError($"WARNING: {stem}: {longRun}");
            }

            var raw = await labeller.LabelAsync(audio, options, progress: null, ct).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);

            // Same repair as `transcribe --speakers`, which gets it inside SpeakerLabelling.LabelAsync.
            // This command drives the labeller directly, so it applies it here rather than inheriting
            // it, and says what it did: a merge the user's own flag asked for is not a silent one.
            IReadOnlyList<SpeakerFold> merges = [];
            var turns = options.SpeakerCount is { } wanted
                ? SpeakerTurns.FoldDownTo(raw, wanted, out merges)
                : raw;

            foreach (var merge in merges)
            {
                context.WriteError($"{stem}: merged {merge.Describe()}.");
            }

            if (merges.Count > 0)
            {
                context.WriteError(
                    $"{stem}: folded to the {SpeakerTurns.Speakers(turns).Count} speakers you asked for. The margin " +
                    "is the evidence, not the raw seconds: two hosts of a long recording overlap for minutes however " +
                    "you cut them, so what matters is how far behind the next-closest pair was. A merge with little " +
                    "or no margin means the count you gave has probably put two people under one name.");
            }

            File.WriteAllText(destination, RttmFile.Write(turns, stem), TextOutput.Utf8NoBom);

            var speakers = SpeakerTurns.Speakers(turns).Count;
            var speech = turns.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Duration);
            totalElapsed += elapsed;
            totalAudio += duration ?? TimeSpan.Zero;

            // The two optional clauses are built under the invariant culture on their own: a nested
            // `$"…"` inside a hole is its own interpolated string, formatted in the current culture,
            // and until 2026-08-22 this line read "0.0 s of speech over 0,0 min" on a comma-decimal
            // machine — the one shape of this bug the analyzers cannot see.
            var over = duration is { } d
                ? string.Create(CultureInfo.InvariantCulture, $" over {d.TotalMinutes:F1} min")
                : string.Empty;
            var realtime = duration is { TotalSeconds: > 0 } known
                ? string.Create(CultureInfo.InvariantCulture, $" ({known.TotalSeconds / elapsed.TotalSeconds:F0}x realtime)")
                : string.Empty;

            context.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{stem}: {turns.Count} turns, {speakers} speakers, {speech.TotalSeconds:F1} s of speech{over}, " +
                $"{elapsed.TotalSeconds:F1} s{realtime} -> {destination}"));
        }

        if (parsed.Positionals.Count > 1 && totalAudio > TimeSpan.Zero && totalElapsed > TimeSpan.Zero)
        {
            context.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{parsed.Positionals.Count} files: {totalAudio.TotalHours:F2} h of audio in " +
                $"{totalElapsed.TotalMinutes:F1} min = {totalAudio.TotalSeconds / totalElapsed.TotalSeconds:F0}x realtime"));
        }

        return ExitCodes.Success;
    }

    private static int? ParseSpeakerCount(ParsedCommandLine parsed)
    {
        if (parsed.Value("speaker-count") is not { Length: > 0 } text)
        {
            return null;
        }

        if (!CommandLineParser.TryParseInt(text, out var value) || value < 1)
        {
            throw new CliUsageException($"--speaker-count needs a positive integer, got '{text}'.");
        }

        return value;
    }
}
