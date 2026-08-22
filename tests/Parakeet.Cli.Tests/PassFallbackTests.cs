using Parakeet.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Jobs;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;

namespace Parakeet.Cli.Tests;

/// <summary>
/// What one file comes to when an opt-in pass fails after its transcript is finished, and what
/// the batch's exit code says about it.
/// </summary>
/// <remarks>
/// Driven through <c>RunOneAsync</c> directly rather than the command line, because the canned
/// labeller and translator the <c>--fake</c> flag builds cannot be made to fail from a flag, and a
/// flag whose only purpose is to make a fake fail would be a flag in the product for the tests'
/// sake. Everything else is real: the canned engine through the real reader and segmenter, the
/// real writer, the real report.
/// </remarks>
public class PassFallbackTests
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("uindosill-pass").FullName;
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

        public async Task<JobResult> RunAsync(
            TranscriptionJob job,
            ISpeakerLabeller? labeller,
            ITranscriptTranslator? translator)
        {
            await using var engine = new FakeTranscriptionEngine(FakeEngineOptions.Default);
            return await TranscribeCommand.RunOneAsync(
                Context,
                engine,
                engine.LoadAsync,
                labeller,
                translator,
                job,
                new TranscriptionOptions(),
                labeller is null ? null : SpeakerLabellingOptions.Default,
                translator is null ? null : TranslationOptions.Default,
                null,
                quiet: true,
                CancellationToken.None);
        }

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
    public async Task ALabellerThatFailsOnAFileLeavesTheTranscriptWrittenWithoutSpeakersAndSaysSo()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav");

        var job = new TranscriptionJob { InputPath = input, Formats = ["txt", "rttm"] };
        await using var labeller = new FakeSpeakerLabeller(new FakeSpeakerLabellerOptions { FailOnLabel = true });

        var result = await harness.RunAsync(job, labeller, translator: null);

        // Completed, because the words are; incomplete, because the speakers are not — and the
        // transcript is on disk rather than recorded as a failed job over a finished decode.
        Assert.Equal(JobState.Completed, result.State);
        Assert.Single(result.FailedPasses);
        Assert.Equal(OptInPass.Speakers, result.FailedPasses[0].Pass);

        var txt = Path.ChangeExtension(input, ".txt");
        Assert.Contains(txt, result.OutputFiles);
        Assert.NotEmpty(await File.ReadAllTextAsync(txt));
        Assert.DoesNotContain("Speaker", await File.ReadAllTextAsync(txt), StringComparison.Ordinal);

        // No turns, no .rttm: the zero-byte file the command refuses to write when the opt-in is
        // off is not written when the opt-in failed either.
        Assert.False(File.Exists(Path.ChangeExtension(input, ".rttm")));

        // Said when it happened, on stderr, naming the file and the reason.
        Assert.Contains("written without speaker labels", harness.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("configured to fail on every file", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATranslatorThatFailsOnAFileLeavesTheSpokenTranscriptUnderThePlainName()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav");

        // The job carries the .en infix the way `transcribe --translate` builds it. A transcript
        // the translation failed on is the spoken one, and goes under the name that does not
        // promise English.
        var job = new TranscriptionJob { InputPath = input, Formats = ["txt"], StemSuffix = ".en" };
        await using var translator = new FakeTranscriptTranslator(new FakeTranslatorOptions { FailOnTranslate = true });

        var result = await harness.RunAsync(job, labeller: null, translator);

        Assert.Equal(JobState.Completed, result.State);
        Assert.Equal(OptInPass.Translation, Assert.Single(result.FailedPasses).Pass);
        Assert.Null(result.Document?.TranslatedTo);

        Assert.True(File.Exists(Path.ChangeExtension(input, ".txt")));
        Assert.False(File.Exists(Path.Combine(harness.Directory, "call.en.txt")));
        Assert.Contains("written without the English version", harness.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APassThatSucceedsLeavesNothingInTheFailedList()
    {
        using var harness = new Harness();
        var input = harness.WriteWav("call.wav");
        var job = new TranscriptionJob { InputPath = input, Formats = ["txt", "rttm"] };
        await using var labeller = new FakeSpeakerLabeller();

        var result = await harness.RunAsync(job, labeller, translator: null);

        Assert.Equal(JobState.Completed, result.State);
        Assert.Empty(result.FailedPasses);
        Assert.True(File.Exists(Path.ChangeExtension(input, ".rttm")));
    }

    [Fact]
    public void AFileWrittenWithoutAPassItAskedForIsAPartialFailureInTheExitCode()
    {
        // A script that checks the exit code has to be able to tell "every file got everything"
        // from "the files are there but three have no speakers"; success would say they are the
        // same thing.
        using var harness = new Harness();
        var job = new TranscriptionJob { InputPath = "a.wav" };

        var complete = new JobResult { Job = job, State = JobState.Completed };
        var incomplete = complete with { FailedPasses = [new PassFailure(OptInPass.Speakers, "died")] };
        var failed = new JobResult { Job = job, State = JobState.Failed, Error = "no" };

        Assert.Equal(ExitCodes.Success, TranscribeCommand.Report(harness.Context, [complete, complete], quiet: true));
        Assert.Equal(ExitCodes.PartialFailure, TranscribeCommand.Report(harness.Context, [complete, incomplete], quiet: true));
        Assert.Equal(ExitCodes.PartialFailure, TranscribeCommand.Report(harness.Context, [incomplete], quiet: true));
        Assert.Equal(ExitCodes.PartialFailure, TranscribeCommand.Report(harness.Context, [complete, failed], quiet: true));
        Assert.Equal(ExitCodes.RuntimeError, TranscribeCommand.Report(harness.Context, [failed, failed], quiet: true));
    }
}
