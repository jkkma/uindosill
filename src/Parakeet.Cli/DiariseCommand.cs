using System.Diagnostics;
using System.Globalization;
using Parakeet.Audio;
using Parakeet.Core.Diarisation;

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

        await using var labeller = LabellerFactory.Create(
            context,
            new LabellerRequest
            {
                Fake = parsed.HasFlag("fake"),
                ModelId = parsed.Value("model"),
                ModelPath = parsed.Value("model-path"),
                Threads = TranscribeCommand.ParseThreads(parsed.Value("threads"), "--threads"),
            },
            options);

        await labeller.LoadAsync(ct).ConfigureAwait(false);

        if (labeller.Capabilities.MaxSpeakers is { } cap)
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
            var turns = await labeller.LabelAsync(audio, options, progress: null, ct).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);

            File.WriteAllText(destination, RttmFile.Write(turns, stem), System.Text.Encoding.UTF8);

            var speakers = SpeakerTurns.Speakers(turns).Count;
            var speech = turns.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Duration);
            totalElapsed += elapsed;
            totalAudio += duration ?? TimeSpan.Zero;

            context.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{stem}: {turns.Count} turns, {speakers} speakers, {speech.TotalSeconds:F1} s of speech" +
                $"{(duration is { } d ? $" over {d.TotalMinutes:F1} min" : string.Empty)}, " +
                $"{elapsed.TotalSeconds:F1} s" +
                $"{(duration is { TotalSeconds: > 0 } known ? $" ({known.TotalSeconds / elapsed.TotalSeconds:F0}x realtime)" : string.Empty)} " +
                $"-> {destination}"));
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
