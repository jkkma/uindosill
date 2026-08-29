using Parakeet.Core.Audio;
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
    public async Task WordTimestampsOffMeansNoWordsFromTheFakeAsFromTheRealEngine()
    {
        // The real engine honours the request — ParakeetCppEngine returns no words when the
        // option is off — and until 2026-08-29 the fake consulted only its own EmitWordTimestamps
        // knob and emitted them anyway. A fake more forgiving than the device is how tests pass
        // over behaviour the real path does not have.
        var audio = new ArrayAudioSource(TestAudio.Build((0.4, false), (2, true), (0.4, false)));
        await using var engine = new FakeTranscriptionEngine();

        var document = await TranscriptionRunner.RunAsync(
            engine, audio, TranscriptionOptions.Default with { WordTimestamps = false });

        Assert.NotEmpty(document.Segments);
        Assert.All(document.Segments, s => Assert.Empty(s.Words));
    }

    [Fact]
    public async Task ProcessingTimeExcludesAColdEnginesModelLoad()
    {
        // ProcessingTime is documented as excluding model load and is what every published
        // real-time factor divides by. A cold engine handed straight to the runner used to pay
        // its load inside the stopwatch — every shipping caller pre-loads, so nothing noticed —
        // and the runner now loads first, which a pre-loading caller cannot feel (LoadAsync is
        // idempotent). Two seconds of load against a decode of milliseconds keeps this exact
        // without racing the clock.
        var audio = new ArrayAudioSource(TestAudio.Build((0.3, false), (1, true), (0.3, false)));
        await using var engine = new FakeTranscriptionEngine(new FakeEngineOptions
        {
            LoadDelay = TimeSpan.FromSeconds(2),
        });

        var document = await TranscriptionRunner.RunAsync(engine, audio);

        Assert.True(document.ProcessingTime < TimeSpan.FromSeconds(1),
            $"ProcessingTime was {document.ProcessingTime}; the 2 s model load leaked into the figure.");
    }

    [Fact]
    public async Task InvalidOptionsAreRefusedBeforeAColdEngineLoads()
    {
        // The other half of the load-before-stopwatch fix, which alone would have moved the load
        // AHEAD of validation: TranscribeAsync's own Validate lives in a lazy iterator that runs
        // only at the first MoveNext, so the runner has to refuse a typo before it pays for a
        // model. A typo must never cost a multi-hundred-megabyte load.
        var audio = new ArrayAudioSource(TestAudio.Build((1, true)));
        await using var engine = new FakeTranscriptionEngine();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TranscriptionRunner.RunAsync(
            engine, audio, TranscriptionOptions.Default with { ThreadCount = 0 }));

        Assert.Equal(0, engine.LoadCount);
    }

    [Fact]
    public async Task TheDecodeTimeIsTheModelsShareAndTheProcessingTimeIsTheWholePass()
    {
        // A source that is slow to read and an engine that decodes in no time: the wall figure
        // carries the read, the decode figure does not. Until 2026-08-22 only the wall figure
        // existed and every document called it "decode time" — on a fast GPU backend the read is
        // most of it.
        var audio = new SlowAudioSource(TestAudio.Build((0.4, false), (2, true), (0.4, false)), delayPerBlock: TimeSpan.FromMilliseconds(60));
        await using var engine = new FakeTranscriptionEngine();

        var document = await TranscriptionRunner.RunAsync(engine, audio, sourceName: "test");

        Assert.NotNull(document.ProcessingTime);
        Assert.NotNull(document.DecodeTime);
        Assert.True(document.DecodeTime <= document.ProcessingTime);
        Assert.True(document.DecodeTime < TimeSpan.FromMilliseconds(200), $"decode {document.DecodeTime}");
        Assert.True(document.ProcessingTime >= TimeSpan.FromMilliseconds(300), $"pass {document.ProcessingTime}");
        Assert.NotNull(document.DecodeRealTimeFactor);
        Assert.True(document.DecodeRealTimeFactor < document.RealTimeFactor);
    }

    [Fact]
    public async Task ASlowDecodeIsMostOfTheProcessingTime()
    {
        // The other way round: an engine that takes its time and a source that does not, so the
        // decode figure accounts for most of the pass.
        var audio = new ArrayAudioSource(TestAudio.Build((0.4, false), (2, true), (0.4, false)));
        await using var engine = new FakeTranscriptionEngine(new FakeEngineOptions { SimulatedRealTimeFactor = 0.25 });

        var document = await TranscriptionRunner.RunAsync(engine, audio, sourceName: "test");

        Assert.True(document.DecodeTime >= TimeSpan.FromMilliseconds(400), $"decode {document.DecodeTime}");
        Assert.True(document.DecodeTime >= document.ProcessingTime * 0.7, $"decode {document.DecodeTime} of pass {document.ProcessingTime}");
    }

    /// <summary>An in-memory source that pauses between blocks, standing in for a container decode.</summary>
    private sealed class SlowAudioSource : IAudioSource
    {
        private readonly float[] _samples;
        private readonly TimeSpan _delayPerBlock;

        public SlowAudioSource(float[] samples, TimeSpan delayPerBlock)
        {
            _samples = samples;
            _delayPerBlock = delayPerBlock;
        }

        public int SampleRate => TestAudio.SampleRate;

        public TimeSpan? Duration => TimeSpan.FromSeconds(_samples.Length / (double)SampleRate);

        public async IAsyncEnumerable<ReadOnlyMemory<float>> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            const int Block = 8_000;
            for (var offset = 0; offset < _samples.Length; offset += Block)
            {
                await Task.Delay(_delayPerBlock, ct);
                yield return _samples.AsMemory(offset, Math.Min(Block, _samples.Length - offset));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
    public async Task AWriteLeavesNoStagingFileBehind()
    {
        // The content goes to a staging name and is moved into place, so a write that stops leaves
        // nothing under the final name; a write that finishes leaves nothing under the staging one.
        using var temp = new TempDirectory();
        var job = new TranscriptionJob
        {
            InputPath = Path.Combine(temp.Path, "input.wav"),
            Formats = ["txt"],
            OutputDirectory = temp.Path,
        };
        var document = new TranscriptDocument
        {
            Segments = [new TranscriptSegment { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1), Text = "hello" }],
        };

        var written = await TranscriptWriter.WriteAsync(document, job);

        Assert.Contains("hello", await File.ReadAllTextAsync(Assert.Single(written)), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void TwoInputsThatWouldWriteOneFileAreFoundBeforeAnythingIsWritten()
    {
        // The same name in two folders under one output directory, and a.wav beside a.mp3 with no
        // output directory at all: both write one stem to one place, and until 2026-08-22 the
        // second silently replaced the first under --overwrite.
        using var temp = new TempDirectory();
        var shared = Path.Combine(temp.Path, "out");
        var underOut = new[]
        {
            new TranscriptionJob { InputPath = Path.Combine(temp.Path, "a", "call.wav"), OutputDirectory = shared },
            new TranscriptionJob { InputPath = Path.Combine(temp.Path, "b", "call.wav"), OutputDirectory = shared },
            new TranscriptionJob { InputPath = Path.Combine(temp.Path, "b", "other.wav"), OutputDirectory = shared },
        };
        var beside = new[]
        {
            new TranscriptionJob { InputPath = Path.Combine(temp.Path, "c", "a.wav") },
            new TranscriptionJob { InputPath = Path.Combine(temp.Path, "c", "a.mp3") },
        };
        var distinct = new[]
        {
            new TranscriptionJob { InputPath = Path.Combine(temp.Path, "a", "call.wav") },
            new TranscriptionJob { InputPath = Path.Combine(temp.Path, "b", "call.wav") },
        };

        var collision = Assert.Single(TranscriptWriter.FindOutputCollisions(underOut));
        Assert.Equal(2, collision.Count);
        Assert.All(collision, job => Assert.EndsWith("call.wav", job.InputPath, StringComparison.Ordinal));

        Assert.Single(TranscriptWriter.FindOutputCollisions(beside));
        Assert.Empty(TranscriptWriter.FindOutputCollisions(distinct));

        // The translated run's infix is part of the stem, so a plain and a translated job of one
        // file do not collide — that is what the infix is for.
        var plainAndTranslated = new[]
        {
            new TranscriptionJob { InputPath = Path.Combine(temp.Path, "a", "call.wav") },
            new TranscriptionJob { InputPath = Path.Combine(temp.Path, "a", "call.wav"), StemSuffix = ".en" },
        };
        Assert.Empty(TranscriptWriter.FindOutputCollisions(plainAndTranslated));
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
    public void TheTranslatorNoticeCarriesAllFourApacheSection4Conditions()
    {
        // §4 attaches four conditions to redistribution, and (c) and (d) are the two that cannot be
        // written from the licence text alone: they depend on what the upstream tree actually
        // carries. That was read at the pinned revision on 2026-08-20 — no NOTICE file, no
        // copyright, patent or trademark notice anywhere, and four attribution notices on the card.
        // Uploading to Hugging Face is redistribution, so this asserts on the rendered notice that
        // all four conditions reach a reader rather than only the two a licence text supplies.
        var text = Attributions.Get(Attributions.OpusMtBibleBigMulEn).ToPlainText();

        // (a) a copy of the License, not a link — so the path is named and the link is beside it.
        Assert.Contains(Attributions.ApacheLicencePath, text, StringComparison.Ordinal);
        Assert.Contains("apache.org/licenses/LICENSE-2.0", text, StringComparison.Ordinal);

        // (b) prominent notices that the files were changed, and what the change was.
        Assert.Contains("Modified:", text, StringComparison.Ordinal);
        Assert.Contains("exported to ONNX", text, StringComparison.Ordinal);

        // (c) the attribution notices found in the source form, retained rather than summarised.
        Assert.Contains("University of Helsinki", text, StringComparison.Ordinal);
        Assert.Contains("opusTCv20230926max50+bt+jhubc_transformer-big_2024-08-18.zip", text, StringComparison.Ordinal);
        Assert.Contains("Democratizing neural machine translation", text, StringComparison.Ordinal);
        Assert.Contains("grant agreement No 101070350", text, StringComparison.Ordinal);

        // (d) is discharged by a finding rather than by a reproduction, and the finding is the part
        // worth asserting: a notice that silently omits a NOTICE file and one that records there is
        // none read identically to anyone downstream, and only the second says the check was done.
        Assert.Contains("no NOTICE file", text, StringComparison.Ordinal);
        Assert.Contains("bb1ef830d5", text, StringComparison.Ordinal);

        // And §7, which the other two notice shapes also carry.
        Assert.Contains("AS IS", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTranslatorNoticeInventsNoCopyrightLine()
    {
        // The upstream repository publishes no copyright line, and the failure this guards against
        // is the tempting one: filling the gap with a plausible "Copyright (c) Helsinki-NLP" that
        // nobody upstream ever wrote. That is a false notice in front of a user, which is the same
        // failure models.json's comment about the deferred entries refuses. The word may appear
        // only in the finding that says there is none.
        var text = Attributions.Get(Attributions.OpusMtBibleBigMulEn).ToPlainText();

        Assert.DoesNotContain("Copyright ©", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Copyright (c)", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("All rights reserved", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no copyright, patent or trademark notice", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDiarisationConstraintOnUseIsListed()
    {
        var joined = string.Join(" ", Attributions.WeightUsageRestrictions);

        // The one restriction here that is about what the product does rather than what it prints,
        // and it must survive a change of engine. It arrived as NVIDIA Open Model License §2.3,
        // whose Trustworthy AI terms name biometric processing; those weights were retired on
        // 2026-08-27 and `community-1`'s CC BY 4.0 raises the subject nowhere. **The caution is
        // asserted anyway**, because it was never really about the licence: separating people by
        // their voices is voice biometrics whichever model does it. This test is what stops the
        // sentence being swept out with the paperwork that introduced it.
        Assert.Contains("biometric", joined, StringComparison.Ordinal);
        Assert.Contains("consent", joined, StringComparison.Ordinal);

        // And the caution itself must not still be phrased as somebody's licence term, which would
        // be this product citing an agreement no model it ships is under. The licence is named once
        // more in this list — in the patent-bargain entry, saying that its bargain has left — and
        // that is a statement about the past rather than a condition on the user, so the assertion
        // is over the sentence that carries the caution rather than over the whole list.
        var caution = Attributions.WeightUsageRestrictions.Single(
            r => r.Contains("biometric", StringComparison.Ordinal));
        Assert.DoesNotContain("NVIDIA", caution, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInferenceRuntimeIsListedWithItsBundledNotices()
    {
        // ONNX Runtime is MIT, but it statically links 69 components that are not, and the shipped
        // ThirdPartyNotices.txt is what covers them. Listing the package as plain MIT and stopping
        // there is the omission this asserts against.
        var runtime = Assert.Single(
            Attributions.Components,
            c => c.Component.Contains("ONNX Runtime", StringComparison.Ordinal));

        Assert.Equal("MIT", runtime.License);
        Assert.Contains("Microsoft Corporation", runtime.Notes ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("ThirdPartyNotices", runtime.Notes ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("github.com/microsoft/onnxruntime", runtime.Uri.ToString(), StringComparison.Ordinal);
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
        // and the About window render, so an omission here reaches the shipped product. Asserted
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
    public void TheAskEngineIsListedWithItsCopyrightAndTheTravellingText()
    {
        // llama.cpp's release archives ship no licence file, so the notice discipline is the
        // vendoring script fetching the MIT text to travel beside the binaries — and this list
        // is what the CLI and the About window render, so the component has to be in it.
        var llama = Assert.Single(
            Attributions.Components,
            c => c.Component.Contains("llama.cpp", StringComparison.Ordinal));

        Assert.Equal("MIT", llama.License);
        Assert.Contains("The ggml authors", llama.Notes ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("travels beside the binaries", llama.Notes ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("github.com/ggml-org/llama.cpp", llama.Uri.ToString(), StringComparison.Ordinal);
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
    public void ASegmentCapUnderTheSplitSearchWindowIsHonouredNotRefused()
    {
        // A three-second cap with the default four-second forced-split window used to pass this
        // validation and then throw from inside the decode iterator, after the model had loaded,
        // naming ForcedSplitSearchWindow — a knob the caller never set and one the segmenter
        // clamps to fit regardless. The derivation now shrinks the window under the cap, so the
        // cap means what it says.
        var options = TranscriptionOptions.Default with { MaxSegmentLength = TimeSpan.FromSeconds(3) };
        options.Validate();

        var derived = options.SegmentationOptions();
        Assert.Equal(TimeSpan.FromSeconds(3), derived.MaxSegmentLength);
        Assert.True(derived.ForcedSplitSearchWindow < derived.MaxSegmentLength);
        derived.Validate();
    }

    [Fact]
    public void ASegmentCapTooShortForFourFramesIsRefusedHereAndNamedAsTheCap()
    {
        // The one derivable failure that remains: a cap that cannot hold four detector frames.
        // It fails at Validate(), before any model loads, attributed to the setting that was set.
        var options = TranscriptionOptions.Default with { MaxSegmentLength = TimeSpan.FromMilliseconds(50) };
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        Assert.Equal(nameof(TranscriptionOptions.MaxSegmentLength), refusal.ParamName);
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
