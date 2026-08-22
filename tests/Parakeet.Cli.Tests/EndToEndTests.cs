using Parakeet.Audio;
using Parakeet.Core.Models;

namespace Parakeet.Cli.Tests;

/// <summary>
/// Drives the real entry point end to end against the canned engine: real WAVE parsing, real
/// segmentation, real formatters, real files on disk. The only thing that is not real is the
/// model, which is what makes these runnable in CI.
/// </summary>
public class EndToEndTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(ModelCatalog? catalog = null)
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("uindosill-cli").FullName;
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

        public string WriteWav(string name, params (double Seconds, bool Loud)[] parts)
        {
            var path = Path.Combine(Directory, name);
            var rate = 16_000;
            var samples = new List<float>();
            var random = new Random(11);

            foreach (var (seconds, loud) in parts)
            {
                var count = (int)(seconds * rate);
                for (var i = 0; i < count; i++)
                {
                    samples.Add(loud
                        ? (float)(0.5 * Math.Sin(2 * Math.PI * 200 * i / rate))
                        : (float)(random.NextDouble() * 0.001 - 0.0005));
                }
            }

            WavWriter.WriteFile(path, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples), rate);
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

    [Fact]
    public async Task TranscribeWritesEveryRequestedFormat()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.5, false), (2.5, true), (0.6, false));

        var exit = await harness.RunAsync("transcribe", "--fake", "-f", "txt,srt,vtt,json,md", input);

        Assert.Equal(ExitCodes.Success, exit);
        foreach (var extension in new[] { ".txt", ".srt", ".vtt", ".json", ".md" })
        {
            var path = Path.ChangeExtension(input, extension);
            Assert.True(File.Exists(path), $"{extension} was not written");
            Assert.NotEmpty(await File.ReadAllTextAsync(path));
        }
    }

    [Fact]
    public async Task NoWrittenFormatOpensWithAByteOrderMark()
    {
        // One encoding for every format the product writes: UTF-8, no mark. RTTM is the format
        // where a mark is a scoring bug rather than a cosmetic one -- its first field is a
        // record type a scorer compares to the literal SPEAKER -- but no format here wants one,
        // and pinning all of them is what stops the next writer reintroducing it in one.
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.5, false), (2.5, true), (0.6, false));

        var exit = await harness.RunAsync(
            "transcribe", "--fake", "--speakers", "-f", "txt,srt,vtt,vtt-words,json,md,rttm", input);

        Assert.Equal(ExitCodes.Success, exit);

        var written = System.IO.Directory.GetFiles(harness.Directory, "clip*")
            .Where(p => !p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(7, written.Count);
        foreach (var path in written)
        {
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.NotEmpty(bytes);
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                $"{Path.GetFileName(path)} opens with a UTF-8 byte order mark");
        }
    }

    [Fact]
    public async Task SubtitlesContainRealTimecodes()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.4, false), (3, true), (0.5, false));

        await harness.RunAsync("transcribe", "--fake", "-f", "srt", input);
        var srt = await File.ReadAllTextAsync(Path.ChangeExtension(input, ".srt"));

        Assert.StartsWith("1\n", srt, StringComparison.Ordinal);
        Assert.Contains(" --> ", srt, StringComparison.Ordinal);
        Assert.Matches(@"\d{2}:\d{2}:\d{2},\d{3} --> \d{2}:\d{2}:\d{2},\d{3}", srt);
    }

    [Fact]
    public async Task OutputDirectoryIsHonoured()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.4, false), (2, true));
        var outputDirectory = Path.Combine(harness.Directory, "transcripts");

        var exit = await harness.RunAsync("transcribe", "--fake", "-o", outputDirectory, input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "clip.txt")));
    }

    [Fact]
    public async Task ExistingOutputIsRenamedRatherThanClobbered()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.4, false), (2, true));
        await File.WriteAllTextAsync(Path.ChangeExtension(input, ".txt"), "please keep me");

        await harness.RunAsync("transcribe", "--fake", input);

        Assert.Equal("please keep me", await File.ReadAllTextAsync(Path.ChangeExtension(input, ".txt")));
        Assert.True(File.Exists(Path.Combine(harness.Directory, "clip (2).txt")));
    }

    [Fact]
    public async Task OneBadFileDoesNotStopTheRestAndTheExitCodeSaysSo()
    {
        using var harness = new Harness();
        var good = harness.WriteWav("good.wav", (0.4, false), (2, true));
        var missing = Path.Combine(harness.Directory, "not-here.wav");

        var exit = await harness.RunAsync("transcribe", "--fake", missing, good);

        Assert.Equal(ExitCodes.PartialFailure, exit);
        Assert.True(File.Exists(Path.ChangeExtension(good, ".txt")));
        Assert.Contains("not-here.wav", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SilentFileIsExplainedRatherThanLeftAsAnEmptyTranscript()
    {
        using var harness = new Harness();
        var path = Path.Combine(harness.Directory, "silent.wav");
        WavWriter.WriteFile(path, new float[16_000 * 3], 16_000);

        var exit = await harness.RunAsync("transcribe", "--fake", path);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("digitally silent", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFormatIsRejectedBeforeAnyWork()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.4, false), (1, true));

        var exit = await harness.RunAsync("transcribe", "--fake", "-f", "docx", input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("Unknown format", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreadsOptionSaysPlainlyThatItDoesNothing()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.4, false), (1.5, true));

        await harness.RunAsync("transcribe", "--fake", "--threads", "4", input);

        Assert.Contains("takes no thread count", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContradictoryOverwriteFlagsAreRejected()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.4, false), (1, true));

        var exit = await harness.RunAsync("transcribe", "--fake", "--overwrite", "--skip-existing", input);

        Assert.Equal(ExitCodes.UsageError, exit);
    }

    [Fact]
    public async Task SegmentCapBeyondTheSafeRangeIsRefused()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.4, false), (1, true));

        var exit = await harness.RunAsync("transcribe", "--fake", "--max-segment", "3600", input);

        Assert.Equal(ExitCodes.UsageError, exit);
    }

    [Fact]
    public async Task FixedWindowModeStillTranscribesQuietMaterial()
    {
        using var harness = new Harness();

        // Well below the speech threshold: the detector correctly finds nothing, and --no-vad is
        // the documented way to get it transcribed anyway.
        var path = Path.Combine(harness.Directory, "quiet.wav");
        var samples = new float[16_000 * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.0008 * Math.Sin(2 * Math.PI * 180 * i / 16_000.0));
        }

        WavWriter.WriteFile(path, samples, 16_000);

        await harness.RunAsync("transcribe", "--fake", path);
        var withVad = await File.ReadAllTextAsync(Path.ChangeExtension(path, ".txt"));

        await harness.RunAsync("transcribe", "--fake", "--no-vad", "-o", harness.Directory, path);
        var withoutVad = await File.ReadAllTextAsync(Path.Combine(harness.Directory, "quiet (2).txt"));

        Assert.Empty(withVad.Trim());
        Assert.NotEmpty(withoutVad.Trim());
    }

    [Fact]
    public async Task ModelsListShowsTheDirectory()
    {
        using var harness = new Harness();

        var exit = await harness.RunAsync("models", "list");
        var output = harness.Out.ToString();

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Model directory:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoShippedEntryIsFlaggedAsUnverified()
    {
        // Every shipped entry pins a digest per file, so nothing here can say "no digest" — and as
        // of 2026-08-20 every one of them is verified too. The translation entry was the last
        // exception: its nine files were published that day, and every LFS oid the repository
        // publishes matched the digest the gate run recorded, so the listing carries no warning.
        //
        // This is deliberately a claim about the DATA. The claim about the FLAG — that it appears
        // when it should — is the test below, against a catalogue built for the purpose, because a
        // flag exercised only by whatever the shipped manifest happens to contain stops being
        // exercised the moment the manifest changes. Between 2026-08-20's two commits this file
        // held the opposite assertion, which is exactly that trap.
        using var harness = new Harness();

        await harness.RunAsync("models", "list");
        var output = harness.Out.ToString();

        Assert.DoesNotContain("no digest", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unverified catalogue entry", output, StringComparison.Ordinal);

        // Still listed — listed without a warning is the point, not listed at all. The id is the
        // durable half of that: the display name was "OPUS-MT Bible-Big multilingual to English -
        // ONNX fp32" until 2026-08-20, when the entries were renamed for the people reading the
        // Models tab, and a listing test has no business pinning a name chosen for readability.
        Assert.Contains("opus-mt-tc-bible-big-mul-en-fp32", output, StringComparison.Ordinal);
        Assert.Contains("English translation", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnpinnedEntryIsStillFlaggedOnEveryLine()
    {
        // The shipped catalogue is fully pinned, so the flag can only be exercised against an
        // entry that is not. Asserting it against the shipped data would have made this test a
        // claim about the data rather than about the flag.
        var catalog = ModelCatalog.Parse("""
            {
              "schema": 1,
              "models": [
                {
                  "id": "test-unpinned",
                  "family": "parakeet-tdt-0.6b-v3",
                  "displayName": "Unpinned test entry",
                  "quantisation": "q8_0",
                  "fileName": "test-unpinned.gguf",
                  "url": "https://example.invalid/test-unpinned.gguf",
                  "sha256": null,
                  "verified": false,
                  "license": "CC-BY-4.0",
                  "attributionId": "nvidia-parakeet-tdt-0.6b-v3",
                  "languages": ["en"]
                }
              ]
            }
            """);

        using var harness = new Harness(catalog);

        var exit = await harness.RunAsync("models", "list");
        var output = harness.Out.ToString();

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("unverified catalogue entry", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsPathPrintsSomewhereOutsideTheInstallDirectory()
    {
        using var harness = new Harness();

        await harness.RunAsync("models", "path");

        var path = harness.Out.ToString().Trim();
        Assert.NotEmpty(path);
        Assert.DoesNotContain(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoticeCommandPrintsEverySevenElementRequirement()
    {
        using var harness = new Harness();

        await harness.RunAsync("notice");
        var output = harness.Out.ToString();

        Assert.Contains("NVIDIA Corporation", output, StringComparison.Ordinal);
        Assert.Contains("Modified:", output, StringComparison.Ordinal);
        Assert.Contains("without warranties", output, StringComparison.Ordinal);
        Assert.Contains("creativecommons.org/licenses/by/4.0", output, StringComparison.Ordinal);
        Assert.Contains("technological measures", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormatsCommandListsEveryFormat()
    {
        using var harness = new Harness();

        await harness.RunAsync("formats");
        var output = harness.Out.ToString();

        foreach (var id in Parakeet.Core.Formatting.TranscriptFormats.Ids)
        {
            Assert.Contains(id, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UnknownCommandIsAUsageError()
    {
        using var harness = new Harness();

        var exit = await harness.RunAsync("transcirbe", "file.wav");

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("Unknown command", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArgumentsPrintsUsageAndFails()
    {
        using var harness = new Harness();

        var exit = await harness.RunAsync();

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("Usage: uindosill", harness.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpSucceedsAndDoesNotAdvertiseTheInternalProbeCommand()
    {
        using var harness = new Harness();

        var exit = await harness.RunAsync("--help");
        var output = harness.Out.ToString();

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("transcribe", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  probe ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestingAModelThatIsNotInstalledSaysHowToInstallIt()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.4, false), (1, true));

        var exit = await harness.RunAsync("transcribe", input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("models download", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BenchNeedsAFileAndSaysWhySyntheticAudioIsNotEnough()
    {
        using var harness = new Harness();

        var exit = await harness.RunAsync("bench");

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("text-to-speech", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BenchReportsColdLoadSeparatelyFromDecode()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.4, false), (2, true), (0.4, false));

        var exit = await harness.RunAsync("bench", "--fake", "--repeat", "1", input);
        var output = harness.Out.ToString();

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("cold load", output, StringComparison.Ordinal);
        Assert.Contains("RTF", output, StringComparison.Ordinal);
        Assert.Contains("not settable", output, StringComparison.Ordinal);
    }
}
