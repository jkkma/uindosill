using System.Text.Json;
using Parakeet.Core.Formatting;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;

namespace Parakeet.Cli.Tests;

/// <summary>
/// Drives <c>uindosill retrieve</c> through the real entry point against transcript files on
/// disk. The verb exists so the lab's recall figure measures the product's own index — the same
/// windows, tokenizer and BM25 the Ask panel retrieves evidence with — so what these tests hold
/// is the seam: the panel's construction reachable from a script, hits carrying resolvable
/// citation ids, and empty retrieval arriving as a real answer rather than an error.
/// </summary>
public class RetrieveCommandTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Directory = TestTemp.NewDirectory("uindosill-retrieve");
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

        public string WriteTranscript(string name, TranscriptDocument document)
        {
            var path = Path.Combine(Directory, name);
            File.WriteAllText(path, new JsonTranscriptFormatter().Format(document));
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
                // A straggling handle on Windows is not what any of these tests assert.
            }
        }
    }

    /// <summary>Three one-topic segments a minute apart, so each lands in a window of its own.</summary>
    private static TranscriptDocument ThreeTopics() => new()
    {
        Segments =
        [
            new TranscriptSegment
            {
                Start = TimeSpan.Zero,
                End = TimeSpan.FromSeconds(10),
                Text = "The meeting opened with the quarterly budget review.",
            },
            new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(60),
                End = TimeSpan.FromSeconds(70),
                Text = "Maria presented the axolotl conservation project to the board.",
            },
            new TranscriptSegment
            {
                Start = TimeSpan.FromSeconds(120),
                End = TimeSpan.FromSeconds(130),
                Text = "The team agreed to reconvene on Friday afternoon.",
            },
        ],
        AudioDuration = TimeSpan.FromSeconds(130),
    };

    [Fact]
    public async Task TheTopHitIsTheWindowThatCarriesTheTerm()
    {
        using var h = new Harness();
        var transcript = h.WriteTranscript("talk.json", ThreeTopics());

        var exit = await h.RunAsync("retrieve", transcript, "-q", "what was said about axolotl conservation?", "--json");

        Assert.Equal(ExitCodes.Success, exit);
        using var parsed = JsonDocument.Parse(h.Out.ToString());
        var root = parsed.RootElement;
        Assert.Equal(3, root.GetProperty("segments").GetInt32());
        Assert.Equal(10, root.GetProperty("top").GetInt32());

        var hits = root.GetProperty("results")[0].GetProperty("hits");
        Assert.True(hits.GetArrayLength() > 0);
        var best = hits[0];
        Assert.Equal(1, best.GetProperty("rank").GetInt32());
        Assert.Equal("S2", best.GetProperty("citation").GetString());
        Assert.Equal(2, best.GetProperty("firstSegment").GetInt32());
        Assert.Equal(2, best.GetProperty("lastSegment").GetInt32());
        Assert.Equal(60, best.GetProperty("startSec").GetDouble());
        Assert.Equal(70, best.GetProperty("endSec").GetDouble());
        Assert.True(best.GetProperty("score").GetDouble() > 0);
    }

    [Fact]
    public async Task AQuestionNothingMatchesReturnsAnEmptyListAndSucceeds()
    {
        using var h = new Harness();
        var transcript = h.WriteTranscript("talk.json", ThreeTopics());

        var exit = await h.RunAsync("retrieve", transcript, "-q", "zeppelin cartography", "--json");

        Assert.Equal(ExitCodes.Success, exit);
        using var parsed = JsonDocument.Parse(h.Out.ToString());
        var hits = parsed.RootElement.GetProperty("results")[0].GetProperty("hits");
        Assert.Equal(0, hits.GetArrayLength());
    }

    [Fact]
    public async Task QuestionsComeBackInTheOrderTheyWereAsked()
    {
        using var h = new Harness();
        var transcript = h.WriteTranscript("talk.json", ThreeTopics());

        var exit = await h.RunAsync(
            "retrieve", transcript,
            "-q", "when do they meet again?",
            "-q", "what about the budget?",
            "--json");

        Assert.Equal(ExitCodes.Success, exit);
        using var parsed = JsonDocument.Parse(h.Out.ToString());
        var results = parsed.RootElement.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("when do they meet again?", results[0].GetProperty("question").GetString());
        Assert.Equal("what about the budget?", results[1].GetProperty("question").GetString());
    }

    [Fact]
    public async Task TheWideVariantIsTheRegistersComparisonShape()
    {
        using var h = new Harness();
        var transcript = h.WriteTranscript("talk.json", ThreeTopics());

        var exit = await h.RunAsync("retrieve", transcript, "-q", "budget", "--wide", "--json");

        Assert.Equal(ExitCodes.Success, exit);
        using var parsed = JsonDocument.Parse(h.Out.ToString());
        Assert.Equal(120, parsed.RootElement.GetProperty("windowSeconds").GetDouble());
        Assert.Equal(60, parsed.RootElement.GetProperty("strideSeconds").GetDouble());
    }

    [Fact]
    public async Task TheHumanOutputNamesTheCitation()
    {
        using var h = new Harness();
        var transcript = h.WriteTranscript("talk.json", ThreeTopics());

        var exit = await h.RunAsync("retrieve", transcript, "-q", "axolotl conservation");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("S2", h.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItRefusesToRunWithoutAQuestion()
    {
        using var h = new Harness();
        var transcript = h.WriteTranscript("talk.json", ThreeTopics());

        var exit = await h.RunAsync("retrieve", transcript);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("--question", h.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingTranscriptIsAUsageError()
    {
        using var h = new Harness();

        var exit = await h.RunAsync("retrieve", Path.Combine(h.Directory, "absent.json"), "-q", "anything");

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("not found", h.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedTranscriptIsAUsageErrorThatNamesTheFile()
    {
        using var h = new Harness();
        var path = Path.Combine(h.Directory, "broken.json");
        await File.WriteAllTextAsync(path, "{ this is not a transcript");

        var exit = await h.RunAsync("retrieve", path, "-q", "anything", "--json");

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("broken.json", h.Error.ToString(), StringComparison.Ordinal);
    }
}
