using Parakeet.Audio;
using Parakeet.Core.Models;

namespace Parakeet.Cli.Tests;

/// <summary>
/// A format spelled by one of its aliases reaches every guard the canonical spelling reaches, and
/// reaches the writer once.
/// </summary>
/// <remarks>
/// The parser accepts <c>words</c> for <c>vtt-words</c>, <c>.rttm</c> for <c>rttm</c>, <c>text</c>
/// for <c>txt</c>, and the writer resolved them at write time; the two guards on the list compared
/// the spelling as typed. So <c>--translate -f words</c> wrote the word-timed file the refusal is
/// written to prevent, <c>-f .rttm</c> with no <c>--speakers</c> wrote the empty <c>.rttm</c> the
/// other refusal names, and <c>-f vtt,webvtt</c> wrote the same file twice. These hold the list to
/// being canonical before anything reads it.
/// </remarks>
public class FormatAliasTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("uindosill-alias").FullName;
            Error = new StringWriter();
            Context = new CliContext
            {
                Out = new StringWriter(),
                Error = Error,
                Store = new LocalModelStore(Path.Combine(Directory, "models")),
                Catalog = ModelCatalog.Default,
                Interactive = false,
            };
        }

        public string Directory { get; }

        public StringWriter Error { get; }

        public CliContext Context { get; }

        public string WriteWav(string name)
        {
            var path = Path.Combine(Directory, name);
            var rate = 16_000;
            var samples = new float[rate * 4];
            var random = new Random(11);

            for (var i = 0; i < samples.Length; i++)
            {
                var second = i / (double)rate;
                samples[i] = second is > 0.5 and < 3.2
                    ? (float)(0.5 * Math.Sin(2 * Math.PI * 200 * i / rate))
                    : (float)(random.NextDouble() * 0.001 - 0.0005);
            }

            WavWriter.WriteFile(path, samples, rate);
            return path;
        }

        public Task<int> RunAsync(params string[] args) =>
            CliEntryPoint.RunAsync(args, Context, CancellationToken.None);

        public IReadOnlyList<string> Outputs(string stem) =>
            System.IO.Directory.GetFiles(Directory, stem + "*")
                .Select(Path.GetFileName)
                .Where(name => !name!.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList()!;

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

    [Theory]
    [InlineData("words")]
    [InlineData("webvtt-words")]
    [InlineData(".vtt-words")]
    [InlineData("VTT-WORDS")]
    public async Task TheWordTimedRefusalUnderTranslateCatchesEverySpellingOfTheFormat(string spelling)
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav");

        var exit = await harness.RunAsync("transcribe", "--fake", "--translate", "-f", "txt," + spelling, input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("translation does not carry word timings", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(harness.Outputs("call"));
    }

    [Theory]
    [InlineData(".rttm")]
    [InlineData("RTTM")]
    public async Task TheTurnsFormatWithoutSpeakersIsRefusedHoweverItIsSpelled(string spelling)
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav");

        var exit = await harness.RunAsync("transcribe", "--fake", "-f", spelling, input);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains("an empty .rttm would be written", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(harness.Outputs("call"));
    }

    [Fact]
    public async Task TwoSpellingsOfOneFormatWriteItOnce()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav");

        var exit = await harness.RunAsync("transcribe", "--fake", "-f", "vtt,webvtt,txt,text,plain", input);

        Assert.Equal(ExitCodes.Success, exit);

        // Two files, not five, and neither of the `name (2).ext` renames the writer falls back to
        // when a name is already taken by the same run.
        Assert.Equal(["call.txt", "call.vtt"], harness.Outputs("call"));
    }
}
