using Parakeet.Core.Models;
using Parakeet.Core.Translation;

namespace Parakeet.Cli.Tests;

/// <summary>
/// <c>uindosill translate</c>: text in, English out, no audio.
/// </summary>
/// <remarks>
/// The sibling of <c>uindosill diarise</c> and it exists for the same reason. The translator can be
/// reached through <c>transcribe --translate</c>, but only behind an ASR pass that costs orders of
/// magnitude more and contributes nothing to a translation — and the corpus the decode loop is held
/// to is a set of written sentences with no audio at all. A component that can only be run through
/// a three-hour transcription is a component nobody scores.
/// </remarks>
public sealed class TranslateVerbTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Directory = TestTemp.NewDirectory("uindosill-translate-verb");
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

        public string Write(string name, params string[] lines)
        {
            var path = Path.Combine(Directory, name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines);
            return path;
        }

        public string Path_(string name) => Path.Combine(Directory, name);

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
    public async Task OneLineInIsOneLineOutInOrderWithTheBlanksKept()
    {
        // The canned translator, so this needs nothing installed. What is being checked is the
        // command's own contract rather than the model's: the line count, the order, and that a
        // blank line survives as a blank line. A file whose line numbers no longer line up is a
        // file nothing can be scored against, and the misalignment is invisible until somebody
        // reads the two side by side.
        using var harness = new Harness();
        var input = harness.Write("es.txt", "Caracas es la capital.", string.Empty, "Buenos días.");

        var exit = await harness.RunAsync("translate", "--fake", input);

        Assert.Equal(ExitCodes.Success, exit);

        var english = File.ReadAllLines(harness.Path_("es.en.txt"));
        Assert.Equal(3, english.Length);
        Assert.Equal("[en] Caracas es la capital.", english[0]);
        Assert.Equal(string.Empty, english[1]);
        Assert.Equal("[en] Buenos días.", english[2]);
    }

    [Fact]
    public async Task ALinePastTheTokenLimitIsRefusedByLineNumberRatherThanWithAStackTrace()
    {
        // The help promises a refusal that names itself. Until 2026-08-22 the refusal was a
        // SegmentTooLongException nothing caught, and the user got a stack trace with a 0-based
        // segment index in it. Driven through the verb's own loop with a translator that has a
        // limit, because the canned --fake one has none and a flag to give it one would be a flag
        // in the product for the tests' sake.
        using var harness = new Harness();
        var input = harness.Write("long.txt", "one two", "one two three four five six");
        await using var translator = new FakeTranscriptTranslator(new FakeTranslatorOptions { MaxSourceTokens = 4 });

        var exit = await TranslateCommand.TranslateFilesAsync(
            harness.Context, translator, [input], outputDirectory: null, id: null, CancellationToken.None);

        Assert.Equal(ExitCodes.RuntimeError, exit);
        var error = harness.Error.ToString();
        Assert.Contains("line 2", error, StringComparison.Ordinal);
        Assert.Contains("limit of 4", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Segment 1", error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", error, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Path_("long.en.txt")));
    }

    [Fact]
    public async Task ADestinationThatIsAlsoAnInputIsRefusedBeforeItIsOverwritten()
    {
        // `translate a.txt a.en.txt`: the second input is the first one's output name, so it was
        // overwritten with the first file's English before it was read — silently, until
        // 2026-08-22, because only a second destination was checked against and never an input.
        using var harness = new Harness();
        var first = harness.Write("a.txt", "Hola.");
        var second = harness.Write("a.en.txt", "Adiós.");

        var exit = await harness.RunAsync("translate", "--fake", first, second);

        Assert.Equal(ExitCodes.UsageError, exit);
        var error = harness.Error.ToString();
        Assert.Contains("also an input", error, StringComparison.Ordinal);
        Assert.Contains(second, error, StringComparison.Ordinal);
        Assert.Equal("Adiós.", File.ReadAllText(second).Trim());
    }

    [Fact]
    public async Task TheOutputIsNamedForTheInputAndCanBeRenamedAndRedirected()
    {
        using var harness = new Harness();
        var input = harness.Write("sources.txt", "Hola.");

        var exit = await harness.RunAsync("translate", "--fake", "--id", "es", "-o", harness.Path_("out"), input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(harness.Path_("out/es.en.txt")));
        Assert.False(File.Exists(harness.Path_("sources.en.txt")));
    }

    [Fact]
    public async Task TwoInputsThatWouldWriteOneOutputAreRefusedBeforeEitherIsWritten()
    {
        // The shape a corpus takes: a/es.txt and b/es.txt with -o. Silently overwriting the first
        // leaves a scorer reporting n-1 files as a complete set.
        using var harness = new Harness();
        var first = harness.Write("a/es.txt", "Hola.");
        var second = harness.Write("b/es.txt", "Adiós.");

        var exit = await harness.RunAsync("translate", "--fake", "-o", harness.Path_("out"), first, second);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("would both be written to", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdNamesOneOutputSoItTakesOneInput()
    {
        using var harness = new Harness();
        var first = harness.Write("a.txt", "Hola.");
        var second = harness.Write("b.txt", "Adiós.");

        var exit = await harness.RunAsync("translate", "--fake", "--id", "both", first, second);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("--id names one output", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingInputIsNamedRatherThanSkipped()
    {
        using var harness = new Harness();

        var exit = await harness.RunAsync("translate", "--fake", harness.Path_("absent.txt"));

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("File not found", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheModelItSaysWhichModelAndHowToGetIt()
    {
        using var harness = new Harness();
        var input = harness.Write("es.txt", "Hola.");

        var exit = await harness.RunAsync("translate", input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("is not installed", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("opus-mt-tc-bible-big-mul-en-fp32", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Path_("es.en.txt")));
    }

    [Fact]
    public async Task AHalfPresentCheckpointIsRefusedByNameRatherThanFailingInsideOnnxRuntime()
    {
        // The route is nine files and a partial set loads until it does not. Naming the missing
        // ones is the difference between a fixable message and a stack trace from a native library.
        using var harness = new Harness();
        var input = harness.Write("es.txt", "Hola.");
        harness.Write("checkpoint/vocab.json", "{}");

        var exit = await harness.RunAsync("translate", "--model-path", harness.Path_("checkpoint"), input);

        Assert.Equal(ExitCodes.UsageError, exit);

        var error = harness.Error.ToString();
        Assert.Contains("not a complete translation checkpoint", error, StringComparison.Ordinal);
        Assert.Contains("encoder_model.onnx", error, StringComparison.Ordinal);
        Assert.Contains("source.spm", error, StringComparison.Ordinal);

        // The sidecar's list is the authority and it names eight files; until 2026-08-22 the host
        // named seven, without generation_config.json, and a checkpoint missing only that loaded
        // here and was refused there.
        Assert.Contains("generation_config.json", error, StringComparison.Ordinal);

        // The file that IS there is not in the list. Checked against vocab.json rather than
        // config.json, whose name is a substring of tokenizer_config.json — which is missing, so
        // the obvious assertion passes for the wrong reason.
        Assert.DoesNotContain("vocab.json", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryThatIsNotThereSaysItWantsADirectory()
    {
        using var harness = new Harness();
        var input = harness.Write("es.txt", "Hola.");

        var exit = await harness.RunAsync("translate", "--model-path", harness.Path_("nope"), input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("directory not found", harness.Error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ItIsInTheHelpAndSaysWhatItIsFor()
    {
        using var harness = new Harness();

        await harness.RunAsync("--help");
        Assert.Contains("translate", harness.Out.ToString(), StringComparison.Ordinal);

        harness.Out.GetStringBuilder().Clear();
        await harness.RunAsync("translate", "--help");

        var help = harness.Out.ToString();
        Assert.Contains("no audio and no ASR", help, StringComparison.Ordinal);

        // The absence is deliberate and worth asserting: beam width and context are the degrees of
        // freedom that decide what English comes out, every published figure was produced at one
        // setting of them, and a flag would make it easy to produce a number describing nothing.
        Assert.DoesNotContain("--beams", help, StringComparison.Ordinal);
        Assert.DoesNotContain("--context", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextIsReportedAsIgnoredRatherThanSilentlyDoingNothing()
    {
        // --context-segments has shipped since the seam landed, and no translator this product can
        // ship reads it. Saying so is the same shape as the diariser being told a speaker count it
        // cannot use: a lever that silently does nothing is worse than no lever.
        using var harness = new Harness();
        var input = harness.Write("call.txt", "Hola.");

        // Through transcribe, which is where the option lives; the fake engine keeps it audio-free.
        var wav = Path.Combine(harness.Directory, "call.wav");
        Parakeet.Audio.WavWriter.WriteFile(wav, new float[16_000 * 2], 16_000);

        var exit = await harness.RunAsync("transcribe", "--fake", "--translate", "--context-segments", "2", wav);

        Assert.Equal(ExitCodes.Success, exit);

        var error = harness.Error.ToString();
        Assert.Contains("--context-segments 2", error, StringComparison.Ordinal);
        Assert.Contains("is ignored", error, StringComparison.Ordinal);

        Assert.NotNull(input);
    }
}
