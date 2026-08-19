using Parakeet.Core.Jobs;
using Parakeet.Core.Licensing;
using Parakeet.Core.Segmentation;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tests;

public class FakeEngineTests
{
    [Fact]
    public async Task ProducesOneSegmentPerUtteranceWithShiftedWordTimings()
    {
        var audio = new ArrayAudioSource(TestAudio.Build((0.5, false), (2, true), (1.2, false), (2, true), (0.5, false)));
        await using var engine = new FakeTranscriptionEngine();

        var document = await TranscriptionRunner.RunAsync(engine, audio, sourceName: "test");

        Assert.Equal(2, document.Segments.Count);
        Assert.All(document.Segments, s => Assert.NotEmpty(s.Words));

        // Word timings must land on the file's timeline, not the segment's.
        var second = document.Segments[1];
        Assert.True(second.Words[0].Start >= second.Start);
        Assert.True(second.Words[^1].End <= second.End + TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task WordTimingsAreMonotonicAcrossTheWholeDocument()
    {
        var audio = new ArrayAudioSource(TestAudio.Build((0.4, false), (3, true), (1, false), (3, true)));
        await using var engine = new FakeTranscriptionEngine();

        var document = await TranscriptionRunner.RunAsync(engine, audio);
        var words = document.Segments.SelectMany(s => s.Words).ToList();

        for (var i = 1; i < words.Count; i++)
        {
            Assert.True(
                words[i].Start >= words[i - 1].Start,
                $"word '{words[i].Text}' starts before the previous word");
        }
    }

    [Fact]
    public async Task LoadIsIdempotent()
    {
        await using var engine = new FakeTranscriptionEngine();

        await engine.LoadAsync();
        await engine.LoadAsync();
        await TranscriptionRunner.RunAsync(engine, new ArrayAudioSource(TestAudio.Build((1, true))));

        Assert.Equal(1, engine.LoadCount);
    }

    [Fact]
    public async Task CancellationStopsTheRun()
    {
        var audio = new ArrayAudioSource(TestAudio.Build((60, true)));
        await using var engine = new FakeTranscriptionEngine(new FakeEngineOptions
        {
            PerSegmentDelay = TimeSpan.FromMilliseconds(50),
        });

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TranscriptionRunner.RunAsync(engine, audio, ct: cancellation.Token));
    }

    [Fact]
    public async Task SilentFileReportsWhySoTheUserIsNotLeftWithAnEmptyFile()
    {
        var audio = new ArrayAudioSource(new float[TestAudio.SampleRate * 3]);
        await using var engine = new FakeTranscriptionEngine();

        var document = await TranscriptionRunner.RunAsync(engine, audio);

        Assert.True(document.IsEmpty);
        Assert.NotNull(engine.LastSegmentationReport);
        Assert.True(engine.LastSegmentationReport!.IsDigitalSilence);
    }

    [Fact]
    public async Task ProgressReachesTheDecodingStage()
    {
        var audio = new ArrayAudioSource(TestAudio.Build((0.5, false), (2, true), (0.5, false)));
        await using var engine = new FakeTranscriptionEngine();

        var reports = new List<TranscriptionProgress>();
        await TranscriptionRunner.RunAsync(
            engine, audio, progress: new InlineProgress(reports.Add));

        Assert.Contains(reports, r => r.Stage == TranscriptionStage.Decoding);
    }

    /// <summary>Progress&lt;T&gt; posts to a scheduler, so reports can arrive after the run
    /// completes; reporting inline means the list is complete when RunAsync returns.</summary>
    private sealed class InlineProgress(Action<TranscriptionProgress> handler) : IProgress<TranscriptionProgress>
    {
        public void Report(TranscriptionProgress value) => handler(value);
    }

    [Fact]
    public async Task EngineReturningTheWrongNumberOfResultsIsRefused()
    {
        var audio = new ArrayAudioSource(TestAudio.Build((0.5, false), (2, true), (0.6, false), (2, true)));
        await using var engine = new LosesResultsEngine();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TranscriptionRunner.RunAsync(engine, audio));

        Assert.Contains("corrupts the timeline", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>An engine that drops a result, which would otherwise shift every later timestamp.</summary>
    private sealed class LosesResultsEngine : SegmentingTranscriptionEngine
    {
        public override EngineCapabilities Capabilities { get; } = new() { EngineName = "broken" };

        protected override int BatchSize => 1;

        public override ValueTask LoadAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        protected override ValueTask<IReadOnlyList<DecodedSegment>> DecodeAsync(
            IReadOnlyList<AudioSegment> batch, TranscriptionOptions options, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<DecodedSegment>>([]);
    }
}

public class BatchRunnerTests
{
    private static TranscriptionJob Job(string name) => new() { InputPath = name, Formats = [] };

    [Fact]
    public async Task OneFailureDoesNotStopTheQueue()
    {
        var runner = new BatchTranscriptionRunner((job, _, _) =>
            job.InputPath == "bad"
                ? throw new InvalidOperationException("corrupt file")
                : Task.FromResult(new JobResult { Job = job, State = JobState.Completed }));

        var results = await runner.RunAsync([Job("a"), Job("bad"), Job("c")]);

        Assert.Equal(3, results.Count);
        Assert.Equal(JobState.Completed, results[0].State);
        Assert.Equal(JobState.Failed, results[1].State);
        Assert.Equal("corrupt file", results[1].Error);
        Assert.Equal(JobState.Completed, results[2].State);
    }

    [Fact]
    public async Task CancelledQueueStillAccountsForEveryFile()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new BatchTranscriptionRunner((job, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new JobResult { Job = job, State = JobState.Completed });
        });

        var results = await runner.RunAsync([Job("a"), Job("b"), Job("c")], ct: cancellation.Token);

        Assert.Equal(3, results.Count);
        Assert.Equal(2, results.Count(r => r.State == JobState.Cancelled));
    }
}

public class TranscriptWriterTests
{
    [Fact]
    public async Task WritesOneFilePerRequestedFormat()
    {
        using var temp = new TempDirectory();
        var job = new TranscriptionJob
        {
            InputPath = Path.Combine(temp.Path, "input.wav"),
            Formats = ["txt", "srt", "json"],
            OutputDirectory = temp.Path,
        };

        var document = new TranscriptDocument
        {
            Segments =
            [
                new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "hello" },
            ],
        };

        var written = await TranscriptWriter.WriteAsync(document, job);

        Assert.Equal(3, written.Count);
        Assert.All(written, path => Assert.True(File.Exists(path)));
        Assert.Contains(written, p => p.EndsWith("input.srt", StringComparison.Ordinal));
    }

    [Fact]
    public void RenamePolicyFindsAFreeName()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "existing");

        var path = TranscriptWriter.ResolvePath(temp.Path, "a", ".txt", OverwritePolicy.Rename);

        Assert.Equal(Path.Combine(temp.Path, "a (2).txt"), path);
    }

    [Fact]
    public void SkipPolicyReturnsNothing()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "a.txt"), "existing");

        Assert.Null(TranscriptWriter.ResolvePath(temp.Path, "a", ".txt", OverwritePolicy.Skip));
    }

    [Fact]
    public void OverwritePolicyReusesThePath()
    {
        using var temp = new TempDirectory();
        var existing = Path.Combine(temp.Path, "a.txt");
        File.WriteAllText(existing, "existing");

        Assert.Equal(existing, TranscriptWriter.ResolvePath(temp.Path, "a", ".txt", OverwritePolicy.Overwrite));
    }
}

public class DecodeThreadPlannerTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    [InlineData(8, 6)]
    [InlineData(16, 8)]
    [InlineData(128, 8)]
    public void DefaultsLeaveHeadroomAndNeverExceedTheCeiling(int processors, int expected) =>
        Assert.Equal(expected, DecodeThreadPlanner.Recommended(processorCount: processors));

    [Fact]
    public void ExplicitRequestsAreHonouredButFlagged()
    {
        Assert.Equal(16, DecodeThreadPlanner.Recommended(16, processorCount: 4));
        Assert.True(DecodeThreadPlanner.IsAboveRecommended(16));
        Assert.False(DecodeThreadPlanner.IsAboveRecommended(8));
    }
}

public class AttributionTests
{
    [Fact]
    public void RenderedNoticeContainsAllSevenRequiredElements()
    {
        var attribution = Attributions.Get(Attributions.ParakeetTdt06BV3);
        var text = attribution.ToPlainText();

        Assert.Contains("NVIDIA Corporation", text, StringComparison.Ordinal);                 // creator
        Assert.Contains("Copyright", text, StringComparison.Ordinal);                          // copyright notice
        Assert.Contains("Creative Commons Attribution 4.0", text, StringComparison.Ordinal);   // licence notice
        Assert.Contains("without warranties", text, StringComparison.Ordinal);                 // warranty disclaimer
        Assert.Contains("huggingface.co/nvidia/parakeet-tdt-0.6b-v3", text, StringComparison.Ordinal); // URI
        Assert.Contains("Modified:", text, StringComparison.Ordinal);                          // modification notice
        Assert.Contains("creativecommons.org/licenses/by/4.0", text, StringComparison.Ordinal); // licence link
    }

    [Fact]
    public void RestrictionsCoverTheDrmAndEndorsementClauses()
    {
        var joined = string.Join(" ", Attributions.WeightUsageRestrictions);

        Assert.Contains("technological measures", joined, StringComparison.Ordinal);
        Assert.Contains("endorsement", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProprietaryCudaRuntimeIsListedAndNotDescribedAsMit()
    {
        // The CUDA drop is three NVIDIA proprietary DLLs and the component list is what the CLI
        // and the Licences tab render, so an omission here reaches the shipped product. Asserted
        // rather than trusted because the failure is silent: five MIT rows look complete.
        var cuda = Assert.Single(
            Attributions.Components,
            c => c.Component.Contains("CUDA", StringComparison.Ordinal));

        Assert.Contains("cudart64_12.dll", cuda.Component, StringComparison.Ordinal);
        Assert.Contains("cublasLt64_12.dll", cuda.Component, StringComparison.Ordinal);
        Assert.Contains("EULA", cuda.License, StringComparison.Ordinal);
        Assert.DoesNotContain("MIT", cuda.License.Replace("not MIT", string.Empty, StringComparison.Ordinal));
        Assert.Contains("docs.nvidia.com/cuda/eula", cuda.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheUpdateFrameworkIsListedWithItsCopyrightLine()
    {
        // MIT requires the copyright notice to travel with the binary, and the installer and the
        // update check are both Velopack code inside the shipped application. The notice surfaces
        // render Notes, so the copyright line goes there rather than being left in a source comment.
        var velopack = Assert.Single(
            Attributions.Components,
            c => c.Component.Contains("Velopack", StringComparison.Ordinal));

        Assert.Equal("MIT", velopack.License);
        Assert.Contains("Velopack Ltd", velopack.Notes ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("github.com/velopack/velopack", velopack.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageClaimsDoNotIncludeScriptsTheModelCannotHandle()
    {
        var joined = string.Join(" ", Attributions.WeightUsageRestrictions);
        Assert.Contains("Chinese, Japanese, Korean", joined, StringComparison.Ordinal);

        foreach (var model in Parakeet.Core.Models.ModelCatalog.Default.Models)
        {
            Assert.DoesNotContain("zh", model.Languages);
            Assert.DoesNotContain("ja", model.Languages);
            Assert.DoesNotContain("ko", model.Languages);
            Assert.DoesNotContain("ar", model.Languages);
            Assert.DoesNotContain("hi", model.Languages);
            Assert.DoesNotContain("th", model.Languages);
        }
    }
}

public class OptionsValidationTests
{
    [Fact]
    public void SegmentCapMustBePositive()
    {
        var options = TranscriptionOptions.Default with { MaxSegmentLength = TimeSpan.Zero };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void SegmentCapBeyondFiveMinutesIsRefused()
    {
        var options = TranscriptionOptions.Default with { MaxSegmentLength = TimeSpan.FromMinutes(30) };
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void BeamSearchNBestMustFitTheBeam()
    {
        var options = TranscriptionOptions.Default with
        {
            BeamSearch = new BeamSearchOptions { BeamSize = 2, NBest = 5 },
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void BeamSearchIsOffByDefaultBecauseItIsAMeasuredRegression() =>
        Assert.Null(TranscriptionOptions.Default.BeamSearch);
}
