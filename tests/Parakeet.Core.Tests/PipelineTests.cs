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
    public void TheDiariserNoticeCarriesTheStringItsLicenceMandatesVerbatim()
    {
        // NVIDIA Open Model License §3.1 does not ask for a package of elements the way CC BY does.
        // It asks for one exact sentence in a Notice file, and a copy of the Agreement. A notice
        // that paraphrases the sentence has not met the condition, so the string is asserted
        // character for character and asserted on its own line — prefixing it, as the renderer
        // prefixes every other field, would stop it being the required string.
        var text = Attributions.Get(Attributions.SortformerDiarisation4Spk).ToPlainText();

        Assert.Contains(
            "\nLicensed by NVIDIA Corporation under the NVIDIA Open Model License\n",
            "\n" + text,
            StringComparison.Ordinal);

        Assert.Contains("soniqo", text, StringComparison.Ordinal);          // the export is a third party's
        Assert.Contains("Model Derivative", text, StringComparison.Ordinal); // and what that makes it
        Assert.Contains("AS IS", text, StringComparison.Ordinal);            // §6
        Assert.Contains(Attributions.OpenModelLicencePath, text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgreementTheDiariserNoticePointsAtActuallyShips()
    {
        // §3.1 wants a copy, not a link. A notice naming a file that is not there is worse than no
        // notice, so the path in the attribution is resolved rather than trusted, and the sentence
        // the Agreement mandates is checked to be inside it.
        var repository = AppContext.BaseDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "Uindosill.slnx")))
        {
            repository = Path.GetDirectoryName(repository);
        }

        Assert.NotNull(repository);
        var agreement = Path.Combine(repository, Attributions.OpenModelLicencePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(agreement), $"the NVIDIA Open Model License copy is missing from {agreement}");

        var text = File.ReadAllText(agreement);
        Assert.Contains("Licensed by NVIDIA Corporation under the NVIDIA Open Model License", text, StringComparison.Ordinal);
        Assert.Contains("You may reproduce and distribute copies of the Model", text, StringComparison.Ordinal);
        Assert.Contains("An output is not a Derivative Model", text, StringComparison.Ordinal);
        Assert.Contains("Last Modified: October 24, 2025", text, StringComparison.Ordinal);

        // The sections this product's own notices cite have to be findable in the copy it ships.
        // The Agreement is a nested list on NVIDIA's page, and a tag-stripping extraction flattens
        // it to unnumbered paragraphs — leaving "section 6 of the Agreement" in the rendered notice
        // pointing at a heading that carries no number, and the Agreement's own cross-references
        // ("revocable (as stated in Section 2.1)") unresolvable inside the file that contains them.
        Assert.Contains("3.1. If you distribute the Model", text, StringComparison.Ordinal);
        Assert.Contains("2.3. AI Ethics.", text, StringComparison.Ordinal);
        Assert.Contains("6. Disclaimer of Warranty.", text, StringComparison.Ordinal);
        Assert.Contains("revocable (as stated in Section 2.1)", text, StringComparison.Ordinal);

        // Eleven top-level sections, Definitions through Trade and Compliance.
        Assert.Contains("11. Trade and Compliance.", text, StringComparison.Ordinal);

        // And nothing from the page around it.
        Assert.DoesNotContain("Popular Links", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Privacy Policy", text, StringComparison.OrdinalIgnoreCase);
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
    public void TheDiarisationLicencesConstraintsOnUseAreListed()
    {
        var joined = string.Join(" ", Attributions.WeightUsageRestrictions);

        // The one restriction here that is about what the product does rather than what it prints:
        // §2.3 incorporates the Trustworthy AI terms, whose biometric clause is squarely on point
        // for a feature that separates people by their voices.
        Assert.Contains("biometric", joined, StringComparison.Ordinal);

        // And the difference from CC BY that a reader would otherwise assume away.
        Assert.Contains("revocable", joined, StringComparison.Ordinal);
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
