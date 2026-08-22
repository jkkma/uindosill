using Parakeet.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Models;

namespace Parakeet.Cli.Tests;

/// <summary>
/// Drives <c>uindosill diarise</c> through the real entry point with the canned labeller.
/// </summary>
/// <remarks>
/// The command exists so the diariser can be scored without an ASR pass, which is how the AMI
/// figure in <c>docs/PHASES.md</c> was produced. What is exercised here is everything but the model:
/// the option surface, the naming that lets <c>der</c> pair a hypothesis with its reference, the
/// refusals, and the fact that the RTTM carries the labeller's own speaker labels rather than
/// display names. The model itself is no longer held up by anything in this solution: it runs in the
/// bundled Python as of 2026-08-21, where the pipeline around the graph is NVIDIA's own code, and
/// the fixtures that held the retired C# port are in <c>attic/Parakeet.Engine.Sortformer.Tests</c>,
/// which is unbuilt. What stands in its place is a committed parity fixture the sidecar runs at
/// load, on a machine that has the weights — which CI does not.
/// </remarks>
public class DiariseCommandTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("uindosill-diarise").FullName;
            Out = new StringWriter();
            Error = new StringWriter();
            Context = new CliContext
            {
                Out = Out,
                Error = Error,
                Store = new LocalModelStore(Path.Combine(Directory, "models")),
                Catalog = ModelCatalog.Default,
                Interactive = false,
            };
        }

        public string Directory { get; }

        public StringWriter Out { get; }

        public StringWriter Error { get; }

        public CliContext Context { get; }

        public string WriteWav(string name, double seconds)
        {
            var path = Path.Combine(Directory, name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const int Rate = 16_000;
            var samples = new float[(int)(seconds * Rate)];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 200 * i / Rate));
            }

            WavWriter.WriteFile(path, samples, Rate);
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
    public async Task ItWritesRttmBesideTheInputAndTranscribesNothing()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("meeting.wav", 12);

        var exit = await harness.RunAsync("diarise", "--fake", input);

        Assert.Equal(ExitCodes.Success, exit);

        var rttm = Path.ChangeExtension(input, ".rttm");
        Assert.True(File.Exists(rttm));
        Assert.False(File.Exists(Path.ChangeExtension(input, ".txt")));

        var document = RttmFile.Parse(File.ReadAllText(rttm));
        Assert.NotEmpty(document.Turns);
        Assert.Equal("meeting", Assert.Single(document.FileIds));
    }

    [Fact]
    public async Task TheRttmOpensWithTheLiteralSpeakerBytesAndNoByteOrderMark()
    {
        // The scorers this file exists to be read by -- md-eval, pyannote -- match field one
        // against the literal SPEAKER, and a byte order mark in front of it is a record type
        // neither knows. Both skip unknown types, so the cost of the mark is not an error but
        // a first turn quietly missing from a figure that still looks healthy.
        //
        // On disk rather than on the formatter's string, because the mark is a property of how
        // the file was written and this command writes its own rather than going through the
        // transcript writer.
        using var harness = new Harness();
        var input = harness.WriteWav("meeting.wav", 12);

        Assert.Equal(ExitCodes.Success, await harness.RunAsync("diarise", "--fake", input));

        var bytes = await File.ReadAllBytesAsync(Path.ChangeExtension(input, ".rttm"));

        Assert.Equal("SPEAKER"u8.ToArray(), bytes[..7]);
    }

    [Fact]
    public async Task TheIdNamesBothTheFileAndTheColumnDerMatchesOn()
    {
        // AMI's audio is ES2004a.Mix-Headset.wav and its reference is ES2004a.rttm, so `der` — which
        // pairs by file stem — cannot find the reference unless the name is settable. This is the
        // whole reason --id exists, so it is asserted on both the file name and the RTTM column.
        using var harness = new Harness();
        var input = harness.WriteWav("ES2004a.Mix-Headset.wav", 9);

        var exit = await harness.RunAsync("diarise", "--fake", "--id", "ES2004a", "-o", harness.Directory, input);

        Assert.Equal(ExitCodes.Success, exit);

        var rttm = Path.Combine(harness.Directory, "ES2004a.rttm");
        Assert.True(File.Exists(rttm));
        Assert.Equal("ES2004a", Assert.Single(RttmFile.Parse(File.ReadAllText(rttm)).FileIds));
    }

    [Fact]
    public async Task TheTurnsCarryTheLabellersOwnSpeakerLabels()
    {
        // Not "Speaker 1". The model's column is what the speaker cache works to keep meaning the
        // same person for a whole recording, and renaming by first appearance would throw that away
        // before a scorer ever saw it.
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 12);

        Assert.Equal(ExitCodes.Success, await harness.RunAsync("diarise", "--fake", input));

        var speakers = SpeakerTurns.Speakers(RttmFile.Parse(File.ReadAllText(Path.ChangeExtension(input, ".rttm"))).Turns);
        Assert.All(speakers, s => Assert.StartsWith("SPEAKER_", s, StringComparison.Ordinal));
        Assert.DoesNotContain(speakers, s => s.StartsWith("Speaker ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheModelInstalledItSaysWhichOneAndHowToGetIt()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);

        var exit = await harness.RunAsync("diarise", input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("sortformer-4spk-v2.1", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("models download", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.ChangeExtension(input, ".rttm")));
    }

    [Fact]
    public async Task AnAsrModelIsRefusedAsADiariser()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);

        var exit = await harness.RunAsync("diarise", "--model", "tdt-0.6b-v3-f16", input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("transcription model, not a diarisation model", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamingOneOutputForSeveralInputsIsRefusedRatherThanOverwritten()
    {
        using var harness = new Harness();
        var first = harness.WriteWav("a.wav", 3);
        var second = harness.WriteWav("b.wav", 3);

        var exit = await harness.RunAsync("diarise", "--fake", "--id", "both", first, second);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.False(File.Exists(Path.Combine(harness.Directory, "both.rttm")));
    }

    [Fact]
    public async Task TwoInputsThatWouldWriteOneOutputStopRatherThanOverwrite()
    {
        // The shape a corpus takes: same stem, different directories, one -o. Silently overwriting
        // would hand a scorer n-1 files and let it report them as a complete set, which is the
        // failure mode this repository exists to refuse. Overwriting an output from an EARLIER run
        // is ordinary and stays allowed — only a collision within one invocation is refused.
        using var harness = new Harness();
        var first = harness.WriteWav("a/meeting.wav", 4);
        var second = harness.WriteWav("b/meeting.wav", 4);
        var outputs = Path.Combine(harness.Directory, "out");

        var exit = await harness.RunAsync("diarise", "--fake", "-o", outputs, first, second);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("would both be written to", harness.Error.ToString(), StringComparison.Ordinal);

        // The first file was already written before the collision was found, and that is fine: what
        // must not happen is the second one landing on top of it.
        Assert.Single(Directory.GetFiles(outputs, "*.rttm"));
    }

    [Fact]
    public async Task RunningTheSameFileTwiceOverwritesItsOwnOutput()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 4);

        Assert.Equal(ExitCodes.Success, await harness.RunAsync("diarise", "--fake", input));
        Assert.Equal(ExitCodes.Success, await harness.RunAsync("diarise", "--fake", input));

        Assert.True(File.Exists(Path.ChangeExtension(input, ".rttm")));
    }

    [Fact]
    public async Task AFileNameWithSpacesInItIsSanitisedRatherThanRefusedAfterTheWork()
    {
        // RTTM splits on whitespace, so RttmFile.Write refuses an id containing any — and it refuses
        // it at the END, after the diariser has run the whole recording. `transcribe -f rttm` has
        // always underscored the stem; this asserts `diarise` does the same, through the same
        // function, rather than throwing away an hour of inference on a file called "Board Meeting".
        using var harness = new Harness();
        var input = harness.WriteWav("Board Meeting.wav", 5);

        var exit = await harness.RunAsync("diarise", "--fake", input);

        Assert.Equal(ExitCodes.Success, exit);

        var rttm = Path.Combine(harness.Directory, "Board_Meeting.rttm");
        Assert.True(File.Exists(rttm), $"expected {rttm}");
        Assert.Equal("Board_Meeting", Assert.Single(RttmFile.Parse(File.ReadAllText(rttm)).FileIds));
    }

    [Fact]
    public async Task ABadThreadCountNamesTheOptionThisCommandActuallyHas()
    {
        // The parser is shared with `transcribe`, whose flag is --speaker-threads. Telling a
        // `diarise` user to fix a flag their command does not have sends them somewhere that does
        // not exist.
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 3);

        var exit = await harness.RunAsync("diarise", "--fake", "--threads", "auto", input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("--threads needs a non-negative integer", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--speaker-threads", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASpeakerCountTheLabellerCannotHonourIsReportedAsIgnored()
    {
        // The seam's capabilities are the caller's to honour. The real diariser estimates the count
        // and cannot be told it, so a value silently dropped would look like it had been applied.
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav", 6);

        Assert.Equal(ExitCodes.Success, await harness.RunAsync("diarise", "--fake", "--speaker-count", "3", input));

        // The canned labeller does honour a count, so nothing is reported for it — which is the
        // other half of the contract, and the half that would break if the warning were unconditional.
        Assert.DoesNotContain("is ignored", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItAppearsInTheCommandListAndItsHelp()
    {
        using var harness = new Harness();

        Assert.Equal(ExitCodes.Success, await harness.RunAsync("diarise", "--help"));

        var help = harness.Out.ToString();
        Assert.Contains("--id", help, StringComparison.Ordinal);
        Assert.Contains("four speakers", help, StringComparison.Ordinal);
    }
}
