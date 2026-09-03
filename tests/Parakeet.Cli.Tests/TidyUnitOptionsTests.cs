using System.Text.Json;
using Parakeet.Audio;
using Parakeet.Core.Models;

namespace Parakeet.Cli.Tests;

/// <summary>
/// The measurement options behind <c>--tidy</c> — the unit a request carries, the shape the tidy
/// runs in, and the trace of what it cost — driven through the real entry point against the
/// canned engine and the canned tidier.
/// </summary>
public class TidyUnitOptionsTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Directory = TestTemp.NewDirectory("uindosill-cli-tidy");
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
    public async Task ThePassShapeOverJoinedRunsWritesTheTidiedVersionAndATrace()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.5, false), (2.5, true), (0.6, false), (2.0, true), (0.5, false));
        var trace = Path.Combine(harness.Directory, "trace", "clip.json");

        var exit = await harness.RunAsync(
            "transcribe", "--fake", "--tidy", "--tidy-unit", "run", "--tidy-shape", "pass", "--tidy-trace", trace,
            "-f", "txt,json", input);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(Path.Combine(harness.Directory, "clip.tidy.txt")), harness.Error.ToString());
        Assert.True(File.Exists(trace), "the trace was asked for");

        using var document = JsonDocument.Parse(File.ReadAllText(trace));
        var root = document.RootElement;
        Assert.Equal("pass", root.GetProperty("shape").GetString());
        Assert.Equal("run", root.GetProperty("unit").GetString());
        Assert.Equal(4, root.GetProperty("concurrency").GetInt32());

        var segments = root.GetProperty("segments").GetInt32();
        var units = root.GetProperty("units").GetInt32();
        Assert.True(segments >= 1);
        Assert.True(units >= 1 && units <= segments, $"{units} requests for {segments} segments");

        var transcriptDone = root.GetProperty("transcriptCompleteSec").GetDouble();
        var tidyDone = root.GetProperty("tidyCompleteSec").GetDouble();
        Assert.True(tidyDone >= transcriptDone, "under the pass shape the tidy starts after the transcript is complete");

        var requests = root.GetProperty("requests").EnumerateArray().ToList();
        Assert.Equal(units, requests.Count);
        Assert.All(requests, r =>
        {
            Assert.True(r.GetProperty("enqueuedSec").GetDouble() >= transcriptDone - 0.001);
            Assert.True(r.GetProperty("landedSec").GetDouble() >= r.GetProperty("startedSec").GetDouble());
            Assert.True(r.GetProperty("accepted").GetBoolean());
        });
        Assert.Equal(segments, requests.Sum(r => r.GetProperty("pieces").GetInt32()));
    }

    [Fact]
    public async Task TheMeasurementOptionsMeanNothingWithoutTheTidyAndRefuseWhatTheyDoNotKnow()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("clip.wav", (0.5, false), (1.5, true), (0.5, false));

        var exit = await harness.RunAsync("transcribe", "--fake", "--tidy-unit", "run", input);
        Assert.NotEqual(ExitCodes.Success, exit);
        Assert.Contains("--tidy-unit only means something with --tidy", harness.Error.ToString(), StringComparison.Ordinal);

        exit = await harness.RunAsync("transcribe", "--fake", "--tidy", "--tidy-unit", "paragraph", input);
        Assert.NotEqual(ExitCodes.Success, exit);
        Assert.Contains("--tidy-unit takes segment, run or sentence", harness.Error.ToString(), StringComparison.Ordinal);

        exit = await harness.RunAsync("transcribe", "--fake", "--tidy", "--tidy-shape", "sideways", input);
        Assert.NotEqual(ExitCodes.Success, exit);
        Assert.Contains("--tidy-shape takes tandem or pass", harness.Error.ToString(), StringComparison.Ordinal);
    }
}
