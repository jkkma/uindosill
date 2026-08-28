using System.Text;
using System.Text.Json;
using Parakeet.Audio;
using Parakeet.Core.Models;

namespace Parakeet.Cli.Tests;

/// <summary>
/// Drives <c>uindosill der</c>, <c>uindosill rttm</c> and <c>transcribe --speakers</c> through the
/// real entry point against files on disk: RTTM pairs written here by hand so every figure can be
/// checked, an Audacity export as the labelling workflow produces it, and the canned engine plus
/// the canned labeller for the opt-in end to end.
/// </summary>
public class DerCommandTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(ModelCatalog? catalog = null)
        {
            Directory = TestTemp.NewDirectory("uindosill-der");
            Out = new StringWriter();
            Error = new StringWriter();
            Context = new CliContext
            {
                Out = Out,
                Error = Error,
                Store = new LocalModelStore(Path.Combine(Directory, "models")),
                Catalog = catalog ?? ModelCatalog.Default,
                Interactive = false,
            };
        }

        public string Directory { get; }

        public StringWriter Out { get; }

        public StringWriter Error { get; }

        public CliContext Context { get; }

        public string Write(string name, string content)
        {
            var path = Path.Combine(Directory, name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>
        /// As <see cref="Write"/>, with a UTF-8 byte order mark in front: the shape a file
        /// written by another tool arrives in.
        /// </summary>
        public string WriteWithByteOrderMark(string name, string content)
        {
            var path = Path.Combine(Directory, name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(content)]);
            return path;
        }

        public string WriteWav(string name, double seconds)
        {
            var path = Path.Combine(Directory, name);
            var rate = 16_000;
            var samples = new float[(int)(seconds * rate)];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 200 * i / rate));
            }

            WavWriter.WriteFile(path, samples, rate);
            return path;
        }

        public Task<int> RunAsync(params string[] args) =>
            CliEntryPoint.RunAsync(args, Context, CancellationToken.None);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // Reference A [0,10], B [8,20]; hypothesis x [0,9], y [9,20]: at collar 0.25 the reference speech
    // is 21 s and 1.75 s of the overlap is missed — the pair the Core tests compute by hand.
    private const string Reference =
        "SPEAKER stretch 1 0.000 10.000 <NA> <NA> A <NA> <NA>\n" +
        "SPEAKER stretch 1 8.000 12.000 <NA> <NA> B <NA> <NA>\n";

    private const string Hypothesis =
        "SPEAKER stretch 1 0.000 9.000 <NA> <NA> x <NA> <NA>\n" +
        "SPEAKER stretch 1 9.000 11.000 <NA> <NA> y <NA> <NA>\n";

    [Fact]
    public async Task DerPrintsTheThreeNumbersAndNamesTheConvention()
    {
        using var harness = new Harness();
        var reference = harness.Write("stretch.ref.rttm", Reference);
        var hypothesis = harness.Write("stretch.rttm", Hypothesis);

        var exit = await harness.RunAsync("der", "--reference", reference, hypothesis);
        var output = harness.Out.ToString();

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("collar 0.25 s (0.125 s either side of each reference boundary), overlap included", output, StringComparison.Ordinal);
        Assert.Contains("pyannote.metrics semantics", output, StringComparison.Ordinal);
        Assert.Contains("--collar 0.5 here", output, StringComparison.Ordinal);
        Assert.Contains("8.33%", output, StringComparison.Ordinal);      // 1.75 / 21 headline
        Assert.Contains("9.09%", output, StringComparison.Ordinal);      // 2 / 22 strict, collar 0
        Assert.Contains("50.00%", output, StringComparison.Ordinal);     // the overlap region: half of it missed
        Assert.Contains("x→A, y→B", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReferenceAndHypothesisWithByteOrderMarksScoreTheSameFigure()
    {
        // The scorer reads what other tools wrote, and a good many of them write UTF-8 with a
        // mark. Tolerating it is the point: the alternative is not a refusal but a first turn
        // dropped as an unknown record type, which moves the figure without moving the exit
        // code. Pinned against the same 8.33% the mark-free pair above scores.
        using var harness = new Harness();
        var reference = harness.WriteWithByteOrderMark("stretch.ref.rttm", Reference);
        var hypothesis = harness.WriteWithByteOrderMark("stretch.rttm", Hypothesis);

        var exit = await harness.RunAsync("der", "--reference", reference, hypothesis);
        var output = harness.Out.ToString();

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("8.33%", output, StringComparison.Ordinal);
        Assert.Contains("x\u2192A, y\u2192B", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DerJsonCarriesEveryBlockAndTheSummedRow()
    {
        using var harness = new Harness();
        var reference = harness.Write("stretch.ref.rttm", Reference);
        var hypothesis = harness.Write("stretch.rttm", Hypothesis);

        var exit = await harness.RunAsync("der", "--reference", reference, "--json", hypothesis, hypothesis);

        Assert.Equal(ExitCodes.Success, exit);
        using var json = JsonDocument.Parse(harness.Out.ToString());
        var root = json.RootElement;

        Assert.Equal(0.25, root.GetProperty("convention").GetProperty("collarSeconds").GetDouble());
        Assert.False(root.GetProperty("convention").GetProperty("skipOverlap").GetBoolean());

        var first = root.GetProperty("hypotheses")[0];
        Assert.Equal(21.0, first.GetProperty("headline").GetProperty("referenceSpeechSeconds").GetDouble(), 6);
        Assert.Equal(1.75, first.GetProperty("headline").GetProperty("missedSeconds").GetDouble(), 6);
        Assert.Equal(22.0, first.GetProperty("strict").GetProperty("referenceSpeechSeconds").GetDouble(), 6);
        Assert.Equal(3.5, first.GetProperty("overlapRegions").GetProperty("referenceSpeechSeconds").GetDouble(), 6);
        Assert.Equal("A", first.GetProperty("mapping").GetProperty("x").GetString());
        Assert.Equal("stretch", first.GetProperty("fileId").GetString());

        Assert.Equal(42.0, root.GetProperty("summed").GetProperty("headline").GetProperty("referenceSpeechSeconds").GetDouble(), 6);
    }

    [Fact]
    public async Task DerMatchesReferencesByStemFromADirectory()
    {
        using var harness = new Harness();
        harness.Write("refs/a.rttm", Reference);
        harness.Write("refs/b.rttm", Reference);
        var a = harness.Write("hyps/a.rttm", Hypothesis);
        var b = harness.Write("hyps/b.rttm", Hypothesis);

        var exit = await harness.RunAsync("der", "--reference-dir", Path.Combine(harness.Directory, "refs"), a, b);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("(all, summed)", harness.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DerHonoursTheCollarAndSkipOverlap()
    {
        using var harness = new Harness();
        var reference = harness.Write("stretch.ref.rttm", Reference);
        var hypothesis = harness.Write("stretch.rttm", Hypothesis);

        await harness.RunAsync("der", "--reference", reference, "--collar", "0", "--skip-overlap", "--json", hypothesis);

        using var json = JsonDocument.Parse(harness.Out.ToString());
        var headline = json.RootElement.GetProperty("hypotheses")[0].GetProperty("headline");
        Assert.Equal(18.0, headline.GetProperty("referenceSpeechSeconds").GetDouble(), 6);   // 22 minus the 2 s × 2 speakers of overlap
        Assert.Equal(0.0, headline.GetProperty("rate").GetDouble(), 6);
    }

    [Theory]
    [InlineData("der")]                                                    // no reference, no hypothesis
    [InlineData("der", "--reference", "x.rttm")]                           // no hypothesis
    [InlineData("der", "--reference", "x.rttm", "--reference-dir", ".", "h.rttm")]   // both
    [InlineData("der", "--reference", "x.rttm", "--collar", "-1", "h.rttm")]
    public async Task DerUsageErrorsAreUsageErrors(params string[] args)
    {
        using var harness = new Harness();
        harness.Write("x.rttm", Reference);
        harness.Write("h.rttm", Hypothesis);
        var withPaths = args.Select(a => a is "x.rttm" or "h.rttm" ? Path.Combine(harness.Directory, a) : a).ToArray();

        var exit = await harness.RunAsync(withPaths);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.NotEmpty(harness.Error.ToString());
    }

    [Fact]
    public async Task AMalformedRttmIsRefusedByNameAndLine()
    {
        using var harness = new Harness();
        var reference = harness.Write("stretch.ref.rttm", Reference);
        var broken = harness.Write("broken.rttm", "SPEAKER stretch 1 0.000 -4 <NA> <NA> x <NA> <NA>\n");

        var exit = await harness.RunAsync("der", "--reference", reference, broken);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("broken.rttm", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("line 1", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RttmConvertsAnAudacityExportAndSummarisesIt()
    {
        using var harness = new Harness();
        var labels = harness.Write("two-hosts-a.txt",
            "0.500000\t6.200000\tHost A\n" +
            "\\\t-1.000000\t-1.000000\n" +
            "6.000000\t9.800000\tHost B\n" +
            "9.900000\t9.900000\tHost A\n" +
            "9.900000\t10.300000\tHost A\n");
        var output = Path.Combine(harness.Directory, "fixtures", "two-hosts-a.rttm");

        var exit = await harness.RunAsync("rttm", labels, "--out", output);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(
            "SPEAKER two-hosts-a 1 0.500 5.700 <NA> <NA> Host_A <NA> <NA>\n" +
            "SPEAKER two-hosts-a 1 6.000 3.800 <NA> <NA> Host_B <NA> <NA>\n" +
            "SPEAKER two-hosts-a 1 9.900 0.400 <NA> <NA> Host_A <NA> <NA>\n",
            await File.ReadAllTextAsync(output));
        var summary = harness.Error.ToString();
        Assert.Contains("4 labels → 3 turns", summary, StringComparison.Ordinal);
        Assert.Contains("1 point label dropped", summary, StringComparison.Ordinal);
        Assert.Contains("Host_A", summary, StringComparison.Ordinal);
        Assert.Contains("0.2 s where two or more speakers talk at once", summary, StringComparison.Ordinal);
        Assert.Empty(harness.Out.ToString());   // --out: nothing on stdout
    }

    [Fact]
    public async Task RttmWritesToStdoutWithoutOutAndTakesAFileIdAndBridge()
    {
        using var harness = new Harness();
        var labels = harness.Write("labels.txt", "0.0\t5.0\tA\n7.3\t9.0\tA\n");

        var exit = await harness.RunAsync("rttm", labels, "--file-id", "stretch 01", "--bridge", "0.5");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal("SPEAKER stretch_01 1 0.000 5.000 <NA> <NA> A <NA> <NA>\nSPEAKER stretch_01 1 7.300 1.700 <NA> <NA> A <NA> <NA>\n", harness.Out.ToString());

        harness.Out.GetStringBuilder().Clear();
        await harness.RunAsync("rttm", labels, "--file-id", "s", "--bridge", "2.5");
        Assert.Equal("SPEAKER s 1 0.000 9.000 <NA> <NA> A <NA> <NA>\n", harness.Out.ToString());
    }

    [Fact]
    public async Task TranscribeWithSpeakersNamesTheVoicesInEveryFormat()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 9);

        var exit = await harness.RunAsync("transcribe", "--fake", "--speakers", "-f", "txt,srt,vtt,vtt-words,json,md,rttm", input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("canned speaker labeller", harness.Error.ToString(), StringComparison.Ordinal);

        var txt = await File.ReadAllTextAsync(Path.ChangeExtension(input, ".txt"));
        Assert.Contains("] Speaker 1: ", txt, StringComparison.Ordinal);
        Assert.Contains("] Speaker 2: ", txt, StringComparison.Ordinal);

        var srt = await File.ReadAllTextAsync(Path.ChangeExtension(input, ".srt"));
        Assert.Contains("\nSpeaker 1: ", srt, StringComparison.Ordinal);
        Assert.Contains("\nSpeaker 2: ", srt, StringComparison.Ordinal);

        var vtt = await File.ReadAllTextAsync(Path.ChangeExtension(input, ".vtt"));
        Assert.Contains("\nSpeaker 1: ", vtt, StringComparison.Ordinal);

        var timed = await File.ReadAllTextAsync(Path.Combine(harness.Directory, "call.words.vtt"));
        Assert.Contains("Speaker 1: <c>", timed, StringComparison.Ordinal);

        var md = await File.ReadAllTextAsync(Path.ChangeExtension(input, ".md"));
        Assert.Contains("**Speaker 1:**", md, StringComparison.Ordinal);
        Assert.Contains("| Speaker labels | fake-speakers |", md, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(input, ".json")));
        Assert.Equal("fake-speakers", json.RootElement.GetProperty("speakerModel").GetString());
        Assert.True(json.RootElement.GetProperty("speakerTurns").GetArrayLength() >= 2);
        Assert.Equal("Speaker 1", json.RootElement.GetProperty("segments")[0].GetProperty("speaker").GetString());

        var rttm = await File.ReadAllTextAsync(Path.ChangeExtension(input, ".rttm"));
        Assert.StartsWith("SPEAKER call 1 0.000 4.000 <NA> <NA> Speaker_1 <NA> <NA>\n", rttm, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheFlagNothingAboutSpeakersAppears()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 6);

        var exit = await harness.RunAsync("transcribe", "--fake", "-f", "txt,json", input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.DoesNotContain("Speaker", await File.ReadAllTextAsync(Path.ChangeExtension(input, ".txt")), StringComparison.Ordinal);
        Assert.DoesNotContain("speaker", await File.ReadAllTextAsync(Path.ChangeExtension(input, ".json")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AskingForRttmWithoutSpeakersIsRefusedRatherThanWritingAnEmptyFile()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 4);

        var exit = await harness.RunAsync("transcribe", "--fake", "-f", "txt,rttm", input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("-f rttm carries speaker turns", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.ChangeExtension(input, ".rttm")));
        Assert.False(File.Exists(Path.ChangeExtension(input, ".txt")));   // refused before anything ran
    }

    [Fact]
    public async Task SpeakersWithoutTheDiariserInstalledSaysWhichModelAndStops()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);
        harness.Write("models/tdt-0.6b-v3-f16.gguf", "not a model");   // so the engine resolves; the diariser is what is missing

        var exit = await harness.RunAsync("transcribe", "--speakers", "--model-path", Path.Combine(harness.Directory, "models", "tdt-0.6b-v3-f16.gguf"), input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("pyannote-speaker-diarization-community-1", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("models download", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.ChangeExtension(input, ".txt")));
    }

    [Fact]
    public async Task TheRefusalNamesTheDiariserEvenWhenTheAsrModelIsAlsoMissing()
    {
        // Both are missing here, and only one of them is what --speakers asked for. "Download the
        // ASR model you have not got" is the wrong answer to "--speakers", so the diariser is
        // resolved first and it is the diariser the message is about.
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);

        var exit = await harness.RunAsync("transcribe", "--speakers", input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("pyannote-speaker-diarization-community-1", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("tdt-0.6b-v3", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpeakersWithFakeStillNeedsNothingInstalled()
    {
        // The canned labeller is what keeps the whole opt-in — the second read, the assignment, the
        // formatters, the rttm format — exercisable on a machine with no weights at all. Adding a
        // real diariser must not quietly make --fake depend on one.
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);

        var exit = await harness.RunAsync("transcribe", "--fake", "--speakers", "-f", "rttm", input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(Path.ChangeExtension(input, ".rttm")));
    }

    [Fact]
    public async Task ScoringTwoSystemsOnOneStretchTellsTheRowsApart()
    {
        using var harness = new Harness();
        var reference = harness.Write("stretch.ref.rttm", Reference);
        var a = harness.Write("sherpa/stretch.rttm", Hypothesis);
        var b = harness.Write("sortformer/stretch.rttm", Hypothesis);

        var exit = await harness.RunAsync("der", "--reference", reference, a, b);
        var output = harness.Out.ToString();

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains(Path.Combine("sherpa", "stretch.rttm"), output, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("sortformer", "stretch.rttm"), output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1e400")]      // parses as infinity
    [InlineData("100000")]     // finite, and past the cap
    public async Task AnAbsurdCollarIsAUsageErrorRatherThanAnOverflow(string collar)
    {
        using var harness = new Harness();
        var reference = harness.Write("stretch.ref.rttm", Reference);
        var hypothesis = harness.Write("stretch.rttm", Hypothesis);

        var exit = await harness.RunAsync("der", "--reference", reference, "--collar", collar, hypothesis);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("between 0 and 3600", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpeakerCountNeedsSpeakersAndAPositiveNumber()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);

        Assert.Equal(ExitCodes.UsageError, await harness.RunAsync("transcribe", "--fake", "--speaker-count", "2", input));
        Assert.Contains("only means something with --speakers", harness.Error.ToString(), StringComparison.Ordinal);

        Assert.Equal(ExitCodes.UsageError, await harness.RunAsync("transcribe", "--fake", "--speakers", "--speaker-count", "0", input));

        // Twelve seconds is three four-second turns: with a count of three, a third voice appears.
        var longer = harness.WriteWav("longer.wav", 12);
        Assert.Equal(ExitCodes.Success, await harness.RunAsync("transcribe", "--fake", "--speakers", "--speaker-count", "3", "-f", "rttm", longer));
        var rttm = await File.ReadAllTextAsync(Path.ChangeExtension(longer, ".rttm"));
        Assert.Contains("Speaker_3", rttm, StringComparison.Ordinal);
    }

    private const string CatalogueWithADiariser = """
        {
          "schema": 1,
          "models": [
            {
              "id": "asr-a", "family": "f", "displayName": "ASR A", "quantisation": "f16", "fileName": "a.gguf",
              "url": "https://example.test/a.gguf", "sizeBytes": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
              "verified": true, "license": "CC-BY-4.0", "attributionId": "nvidia-parakeet-tdt-0.6b-v3", "recommended": false
            },
            {
              "id": "diar-x", "task": "diarisation", "family": "sortformer", "displayName": "Sortformer v2 int8", "quantisation": "int8", "fileName": "d.onnx",
              "url": "https://example.test/d.onnx", "sizeBytes": 10, "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
              "verified": true, "license": "CC-BY-4.0", "attributionId": "nvidia-parakeet-tdt-0.6b-v3", "recommended": true
            }
          ]
        }
        """;

    [Fact]
    public async Task ADiarisationModelIsNeverSelectableAsTheAsrModel()
    {
        using var harness = new Harness(ModelCatalog.Parse(CatalogueWithADiariser));
        var input = harness.WriteWav("call.wav", 3);
        harness.Write("models/a.gguf", "x");
        harness.Write("models/d.onnx", "x");

        // Explicitly, by id: refused with the reason.
        var exit = await harness.RunAsync("transcribe", "--model", "diar-x", input);
        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("diarisation model, not a transcription model", harness.Error.ToString(), StringComparison.Ordinal);

        // Implicitly, as the recommended entry: the recommended flag on it does not reach transcribe.
        Assert.Equal("asr-a", harness.Context.Catalog.Recommended?.Id);
        Assert.Equal(["diar-x"], harness.Context.Catalog.DiarisationModels.Select(m => m.Id));

        // And the listing says which is which.
        harness.Error.GetStringBuilder().Clear();
        await harness.RunAsync("models", "list");
        Assert.Contains("diarisation model — not selectable for transcribe", harness.Out.ToString(), StringComparison.Ordinal);
    }
}
