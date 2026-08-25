using System.Text.Json;
using Parakeet.Core.Models;

namespace Parakeet.Cli.Tests;

/// <summary>
/// Drives `uindosill wer` through the real entry point against files on disk. No audio and no
/// engine: a hypothesis is a transcript JSON or .txt exactly as the tool writes them, and a
/// reference is a text or .nlp file, all written here by hand so every count can be checked.
/// </summary>
public class WerCommandTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Directory = TestTemp.NewDirectory("uindosill-wer");
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

        public string Write(string name, string content)
        {
            var path = Path.Combine(Directory, name);
            var parent = Path.GetDirectoryName(path)!;
            System.IO.Directory.CreateDirectory(parent);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>A transcript JSON of the shape JsonTranscriptFormatter writes, reduced to what wer reads.</summary>
        public string WriteHypothesisJson(string name, string text) =>
            Write(name, JsonSerializer.Serialize(new { backend = "fake", text, segments = Array.Empty<object>() }));

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
    public async Task APerfectHypothesisScoresZeroAndTheNormalisationIsNamed()
    {
        using var harness = new Harness();
        var reference = harness.Write("ref.txt", "Good morning, and welcome to the call.\n");
        var hypothesis = harness.WriteHypothesisJson("call.json", "Good morning and welcome to the call");

        var exit = await harness.RunAsync("wer", "--reference", reference, hypothesis);
        var output = harness.Out.ToString();

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("0.00%", output, StringComparison.Ordinal);
        Assert.Contains("not the leaderboard normaliser", output, StringComparison.Ordinal);
        Assert.Contains("fillers (uh, um, hmm, mm, mhm, mmm) dropped", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountsAreReportedAgainstTheReferenceLength()
    {
        using var harness = new Harness();
        // 9 reference words after normalisation. Hypothesis: "morning" -> "evening" (S), "the"
        // dropped (D), "very" added (I): 3 errors, 33.33%, and no other alignment costs three.
        var reference = harness.Write("ref.txt", "Good morning, and welcome to the call. Thank you.");
        var hypothesis = harness.WriteHypothesisJson("call.json", "Good evening and welcome to call. Thank you very");

        var exit = await harness.RunAsync("wer", "--reference", reference, "--json", hypothesis);

        Assert.Equal(ExitCodes.Success, exit);
        using var document = JsonDocument.Parse(harness.Out.ToString());
        var scored = document.RootElement.GetProperty("hypotheses")[0].GetProperty("normalised");
        Assert.Equal(9, scored.GetProperty("referenceWords").GetInt32());
        Assert.Equal(9, scored.GetProperty("hypothesisWords").GetInt32());
        Assert.Equal(1, scored.GetProperty("substitutions").GetInt32());
        Assert.Equal(1, scored.GetProperty("deletions").GetInt32());
        Assert.Equal(1, scored.GetProperty("insertions").GetInt32());
        Assert.Equal(3, scored.GetProperty("errors").GetInt32());
        Assert.Equal(1.0 / 3.0, scored.GetProperty("rate").GetDouble(), 6);

        // Raw tokens keep case and punctuation, so "morning," and "you." differ from their
        // hypothesis counterparts as written and the raw figure is higher.
        var raw = document.RootElement.GetProperty("hypotheses")[0].GetProperty("raw");
        Assert.Equal(9, raw.GetProperty("referenceWords").GetInt32());
        Assert.True(raw.GetProperty("errors").GetInt32() > 3);
    }

    [Fact]
    public async Task TheTxtOutputIsReadWithItsTimestampsStripped()
    {
        using var harness = new Harness();
        var reference = harness.Write("ref.txt", "one two three four");
        var hypothesis = harness.Write("clip.txt", "[00:00:00] one two\n[00:00:01] three four\n");

        var exit = await harness.RunAsync("wer", "--reference", reference, "--json", hypothesis);

        Assert.Equal(ExitCodes.Success, exit);
        using var document = JsonDocument.Parse(harness.Out.ToString());
        var scored = document.RootElement.GetProperty("hypotheses")[0];
        Assert.Equal(0, scored.GetProperty("normalised").GetProperty("errors").GetInt32());
        // The raw figure sees the timestamps stripped too: four whitespace tokens, not six.
        Assert.Equal(4, scored.GetProperty("raw").GetProperty("hypothesisWords").GetInt32());
        Assert.Equal(0, scored.GetProperty("raw").GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task AnEarnings22NlpReferenceIsReadTokenByTokenWithItsPunctuation()
    {
        using var harness = new Harness();
        var nlp = string.Join('\n',
            "token|speaker|ts|endTs|punctuation|case|tags|wer_tags",
            "Good|0||||UC|[]|[]",
            "morning|0|||,|LC|['5:TIME']|['5']",
            "and|0||||LC|[]|[]",
            "welcome|0||||LC|[]|[]",
            "to|0||||LC|[]|[]",
            "Q1|0||||CA|[]|[]",
            "2020|0|||.|CA|['0:YEAR']|['0']",
            "");
        var reference = harness.Write("call.nlp", nlp);
        var hypothesis = harness.WriteHypothesisJson("call.json", "Good morning, and welcome to Q1 2020.");

        var exit = await harness.RunAsync("wer", "--reference", reference, "--json", hypothesis);

        Assert.Equal(ExitCodes.Success, exit);
        using var document = JsonDocument.Parse(harness.Out.ToString());
        var scored = document.RootElement.GetProperty("hypotheses")[0];
        Assert.Equal(7, scored.GetProperty("normalised").GetProperty("referenceWords").GetInt32());
        Assert.Equal(0, scored.GetProperty("normalised").GetProperty("errors").GetInt32());
        // Raw: the punctuation column was appended, so "morning," and "2020." match the hypothesis as written.
        Assert.Equal(0, scored.GetProperty("raw").GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task ANlpFileWithoutItsHeaderIsRefusedWithTheWayOut()
    {
        using var harness = new Harness();
        var reference = harness.Write("call.nlp", "just some words\n");
        var hypothesis = harness.WriteHypothesisJson("call.json", "just some words");

        var exit = await harness.RunAsync("wer", "--reference", reference, hypothesis);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("--reference-format text", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReferenceDirectoryMatchesByStemAndSumsAcrossFiles()
    {
        using var harness = new Harness();
        harness.Write("refs/a.txt", "one two three four");          // 4 words
        harness.Write("refs/b.nlp", "token|speaker|ts|endTs|punctuation|case|tags|wer_tags\nfive|0||||LC|[]|[]\nsix|0||||LC|[]|[]\n"); // 2 words
        var a = harness.WriteHypothesisJson("hyps/a.json", "one two three");   // 1 deletion
        var b = harness.WriteHypothesisJson("hyps/b.json", "five six seven"); // 1 insertion

        var exit = await harness.RunAsync("wer", "--reference-dir", Path.Combine(harness.Directory, "refs"), "--json", a, b);

        Assert.Equal(ExitCodes.Success, exit);
        using var document = JsonDocument.Parse(harness.Out.ToString());
        var summed = document.RootElement.GetProperty("summed").GetProperty("normalised");
        Assert.Equal(6, summed.GetProperty("referenceWords").GetInt32());
        Assert.Equal(1, summed.GetProperty("deletions").GetInt32());
        Assert.Equal(1, summed.GetProperty("insertions").GetInt32());
        // 2 / 6, summed counts — not the mean of 25% and 50%.
        Assert.Equal(2.0 / 6.0, summed.GetProperty("rate").GetDouble(), 6);

        var hypotheses = document.RootElement.GetProperty("hypotheses");
        Assert.EndsWith("a.txt", hypotheses[0].GetProperty("reference").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("b.nlp", hypotheses[1].GetProperty("reference").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingReferenceInTheDirectoryIsNamed()
    {
        using var harness = new Harness();
        harness.Write("refs/a.txt", "one");
        var orphan = harness.WriteHypothesisJson("hyps/orphan.json", "one");

        var exit = await harness.RunAsync("wer", "--reference-dir", Path.Combine(harness.Directory, "refs"), orphan);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("orphan.txt or .nlp", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactlyOneReferenceModeIsRequired()
    {
        using var harness = new Harness();
        var reference = harness.Write("ref.txt", "one");
        var hypothesis = harness.WriteHypothesisJson("a.json", "one");

        Assert.Equal(ExitCodes.UsageError, await harness.RunAsync("wer", hypothesis));
        Assert.Equal(ExitCodes.UsageError, await harness.RunAsync("wer", "--reference", reference, "--reference-dir", harness.Directory, hypothesis));
        Assert.Contains("exactly one of", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FillersAreDroppedUnlessKept()
    {
        using var harness = new Harness();
        var reference = harness.Write("ref.txt", "we think yes");
        var hypothesis = harness.WriteHypothesisJson("a.json", "um we think, uh, yes");

        await harness.RunAsync("wer", "--reference", reference, "--json", hypothesis);
        using (var dropped = JsonDocument.Parse(harness.Out.ToString()))
        {
            Assert.Equal(0, dropped.RootElement.GetProperty("hypotheses")[0].GetProperty("normalised").GetProperty("errors").GetInt32());
        }

        harness.Out.GetStringBuilder().Clear();
        await harness.RunAsync("wer", "--reference", reference, "--json", "--keep-fillers", hypothesis);
        using (var kept = JsonDocument.Parse(harness.Out.ToString()))
        {
            Assert.Equal(2, kept.RootElement.GetProperty("hypotheses")[0].GetProperty("normalised").GetProperty("insertions").GetInt32());
        }
    }

    [Fact]
    public async Task ShowPrintsTheErrorSitesWithContext()
    {
        using var harness = new Harness();
        var reference = harness.Write("ref.txt", "the quarter was strong across every region we serve");
        var hypothesis = harness.WriteHypothesisJson("a.json", "the quarter were strong across every region we serve");

        var exit = await harness.RunAsync("wer", "--reference", reference, "--show", "1", hypothesis);
        var output = harness.Out.ToString();

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("ref: the quarter WAS  strong across every", output, StringComparison.Ordinal);
        Assert.Contains("hyp: the quarter WERE strong across every", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCommandIsListedInUsageAndHasHelp()
    {
        using var harness = new Harness();

        await harness.RunAsync("--help");
        Assert.Contains("wer", harness.Out.ToString(), StringComparison.Ordinal);

        harness.Out.GetStringBuilder().Clear();
        var exit = await harness.RunAsync("wer", "--help");
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("--reference-dir", harness.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("spells numbers out", harness.Out.ToString(), StringComparison.Ordinal);
    }
}
