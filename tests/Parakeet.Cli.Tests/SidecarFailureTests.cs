using Parakeet.Audio;
using Parakeet.Core.Models;
using Parakeet.Engine.Python;

namespace Parakeet.Cli.Tests;

/// <summary>
/// What a user sees when the bundled Python is not there.
/// </summary>
/// <remarks>
/// <para>
/// Two of this product's three models run out of process, so "there is no interpreter" is a failure
/// an ordinary user can meet — an incomplete install, an antivirus quarantine, a copied application
/// directory. <see cref="PythonSidecarException"/> exists to carry a message that says which half is
/// missing and what to do about it, and the entry point has to be on the list that prints such
/// messages rather than letting them out as an unhandled exception.
/// </para>
/// <para>
/// It was not, and this is the test that says so. Before 2026-08-21 the same failure produced a
/// stack trace naming <c>PythonRuntime.cs</c> and a line number: the message was written, built and
/// thrown away, and the user was shown the one thing in the exception that could not help them.
/// </para>
/// </remarks>
[Collection("environment")]
public class SidecarFailureTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Directory = TestTemp.NewDirectory("uindosill-sidecar");
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

        /// <summary>A checkpoint directory that is complete enough to get past the file checks.</summary>
        public string StageCheckpoint()
        {
            var path = Path.Combine(Directory, "checkpoint");
            System.IO.Directory.CreateDirectory(path);
            foreach (var name in new[]
            {
                "encoder_model.onnx", "decoder_model_merged.onnx", "config.json", "generation_config.json",
                "source.spm", "target.spm", "vocab.json", "tokenizer_config.json",
            })
            {
                File.WriteAllText(Path.Combine(path, name), "not a real one");
            }

            return path;
        }

        /// <summary>A real WAV, so the command reaches the labeller rather than failing on the file.</summary>
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

    /// <summary>
    /// Points the interpreter override at nothing for the duration of one test.
    /// </summary>
    /// <remarks>
    /// The variable is process-wide, which is why this class is in a collection of its own that
    /// disables parallelisation. Restored on dispose, because a
    /// variable left set would make every later test in this process one about a missing Python. The
    /// collection at the bottom of this file is what keeps another collection from running while it
    /// is set.
    /// </remarks>
    private sealed class NoInterpreter : IDisposable
    {

        private readonly string? _previous =
            Environment.GetEnvironmentVariable(PythonRuntime.InterpreterVariable);

        public NoInterpreter() => Environment.SetEnvironmentVariable(
            PythonRuntime.InterpreterVariable,
            Path.Combine(Path.GetTempPath(), "uindosill-no-such-interpreter", "python.exe"));

        public void Dispose() =>
            Environment.SetEnvironmentVariable(PythonRuntime.InterpreterVariable, _previous);
    }

    [Fact]
    public async Task TranslatingWithNoInterpreterSaysSoRatherThanThrowingAStackTrace()
    {
        using var harness = new Harness();
        using var _ = new NoInterpreter();

        var input = Path.Combine(harness.Directory, "es.txt");
        await File.WriteAllTextAsync(input, "Hola.\n");

        var exit = await harness.RunAsync("translate", "--model-path", harness.StageCheckpoint(), input);

        Assert.Equal(ExitCodes.RuntimeError, exit);
        Assert.Contains(PythonRuntime.InterpreterVariable, harness.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LabellingSpeakersWithNoInterpreterSaysSoTheSameWay()
    {
        // The diariser reaches the same failure by a different route — LabellerFactory rather than
        // TranslatorFactory — so it is asserted separately rather than assumed to follow.
        using var harness = new Harness();
        using var _ = new NoInterpreter();

        var model = Path.Combine(harness.Directory, "sortformer.onnx");
        await File.WriteAllTextAsync(model, "not a real graph");

        var exit = await harness.RunAsync("diarise", "--model-path", model, harness.WriteWav("a.wav", 1));

        Assert.Equal(ExitCodes.RuntimeError, exit);
        Assert.Contains(PythonRuntime.InterpreterVariable, harness.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskingForWebgpuWithoutTheExportedGraphsNamesTheMissingFilesAndStartsNothing()
    {
        // The ONNX route is a derived artefact: nothing installs the graphs, so the common case is
        // asking for a provider whose graphs are not there. That has to fail as a usage error
        // naming the files — not as a runtime failure inside a sidecar, and not by quietly running
        // on the CPU, which would report a GPU run that never happened.
        using var harness = new Harness();
        using var _ = new NoInterpreter();

        var model = Path.Combine(harness.Directory, "pyannote");
        System.IO.Directory.CreateDirectory(model);

        var exit = await harness.RunAsync(
            "diarise", "--model-path", model, "--backend", "webgpu", harness.WriteWav("a.wav", 1));

        Assert.Equal(ExitCodes.UsageError, exit);

        var error = harness.Error.ToString();
        Assert.Contains("segmentation.onnx", error, StringComparison.Ordinal);
        Assert.Contains("embedding.onnx", error, StringComparison.Ordinal);
        Assert.Contains("export-diariser-onnx.py", error, StringComparison.Ordinal);

        // The interpreter is missing here too, and the point is that it never mattered: the refusal
        // happens before a subprocess is started, which is what makes it cost nothing.
        Assert.DoesNotContain(PythonRuntime.InterpreterVariable, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebgpuIsNoLongerRefusedOutrightOnceTheGraphsAreBesideTheModel()
    {
        // The other half, and the one that would rot silently: until 2026-08-28 this backend was
        // refused whatever was on disk, so a test that only asserted the refusal would still pass
        // with the route removed.
        using var harness = new Harness();
        using var _ = new NoInterpreter();

        var model = Path.Combine(harness.Directory, "pyannote");
        var graphs = Path.Combine(model, "onnx");
        System.IO.Directory.CreateDirectory(graphs);
        await File.WriteAllTextAsync(Path.Combine(graphs, "segmentation.onnx"), "not a real graph");
        await File.WriteAllTextAsync(Path.Combine(graphs, "embedding.onnx"), "not a real graph");

        var exit = await harness.RunAsync(
            "diarise", "--model-path", model, "--backend", "webgpu", harness.WriteWav("a.wav", 1));

        // It gets past the guard and reaches the sidecar, where this machine has no interpreter.
        // Whether the graphs load is the sidecar's business and is not asserted here — these are
        // two text files.
        Assert.Equal(ExitCodes.RuntimeError, exit);
        Assert.Contains(PythonRuntime.InterpreterVariable, harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCannedTranslatorNeedsNoInterpreterAtAll()
    {
        // The other half of the claim: --fake is what makes the whole pipeline exercisable without
        // weights, and it would stop being that if it went anywhere near the sidecar.
        using var harness = new Harness();
        using var _ = new NoInterpreter();

        var input = Path.Combine(harness.Directory, "es.txt");
        await File.WriteAllTextAsync(input, "Hola.\n");

        var exit = await harness.RunAsync("translate", "--fake", "-o", harness.Directory, input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(Path.Combine(harness.Directory, "es.en.txt")));
    }
}

/// <summary>
/// The one collection in this assembly that is not free of shared state.
/// </summary>
/// <remarks>
/// <see cref="SidecarFailureTests"/> sets <c>UINDOSILL_PYTHON</c>, which is process-wide: without
/// this, another test in another collection could resolve the interpreter while it is pointed at
/// nothing, and would fail for a reason that has nothing to do with it. Disabling parallelisation
/// keeps this collection from running alongside any other, which is the only thing that makes a
/// process-wide variable safe to touch — and touching it is the only way to test that it is read.
/// </remarks>
[CollectionDefinition("environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;
