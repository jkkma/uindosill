using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// The two engine wrappers, driven against a scripted sidecar: what they send, what they make of
/// what comes back, and what they refuse.
/// </summary>
/// <remarks>
/// <para>
/// No weights and no Python. Everything here is the seam between a JSON reply and this project's own
/// contracts, which is where a wrong answer stops being an error and starts being a result — a
/// backend reported as CPU when WebGPU ran, or a source translated when it should have been refused.
/// </para>
/// <para>
/// <b>What is not covered, said rather than implied.</b> Nothing here calls
/// <c>SidecarSpeakerLabeller.LabelAsync</c>, so its turn parsing and validation, the WAV staging and
/// its deletion, and the conversion from the sidecar's chunk counter to the host's audio-duration
/// progress are untested anywhere in this repository. They need an <c>IAudioSource</c> and a reply
/// full of turns, both of which this stand-in could supply; the gap is work not yet done rather than
/// work that cannot be done.
/// </para>
/// </remarks>
public sealed class SidecarEngineTests
{
    private const string DiariserCapabilities =
        """{"id":{id},"type":"result","capabilities":{"engineName":"pyannote-torch-python","modelId":"pyannote-speaker-diarization-community-1","backend":"cpu","segmentationBackend":"torch:cpu","embeddingBackend":"torch:cpu","supportsFixedSpeakerCount":false,"maxSpeakers":null,"reliableUpToSeconds":null,"honoursPostProcessing":false}}""";

    private const string TranslatorCapabilities =
        """{"id":{id},"type":"result","capabilities":{"engineName":"marian-onnx-python","modelId":"opus-mt","backend":"webgpu","maxSourceTokens":512,"beams":6,"maxNewTokens":512,"lengthPenalty":1.0,"earlyStopping":false}}""";

    /// <summary>
    /// A script that answers the handshake, the rules given, and <b>errors on anything else</b>.
    /// </summary>
    /// <remarks>
    /// The default arm is what makes "this op is never sent" a testable claim. Two tests below
    /// assert exactly that — an empty segment must not reach the model, and the parity check must
    /// not run on the CPU — and they assert it by leaving the op out of the script. Without a
    /// default the stand-in reads the request and says nothing, so a regression would not fail the
    /// test: it would hang it, and with no timeout anywhere in this suite it would hang the run.
    /// With one, the request comes back as an error and the test fails in milliseconds.
    /// </remarks>
    private static object Script(params object[] rules) => new
    {
        rules = new object[] { new { op = "hello", emit = new[] { FakeSidecarProcess.Handshake } } }
            .Concat(rules)
            .ToArray(),
        @default = new
        {
            emit = new[] { UnexpectedOp },
        },
    };

    private const string UnexpectedOp =
        """{"id":{id},"type":"error","kind":"request","message":"this op was not supposed to be sent"}""";

    private static SidecarTranslatorOptions TranslatorOptions => new()
    {
        ModelDirectory = Path.GetTempPath(),
        ModelId = "opus-mt",
    };

    // -- the translator ------------------------------------------------------------------------

    [Fact]
    public async Task TheLoadedCapabilitiesAreTheSidecarsRatherThanThisSidesExpectationOfThem()
    {
        using var fake = FakeSidecarProcess.Scripted(Script(
            new { op = "load", emit = new[] { TranslatorCapabilities } },
            new { op = "parity", emit = new[] { """{"id":{id},"type":"result","available":true,"passed":true,"identical":6,"total":6,"differing":[]}""" } }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        await translator.LoadAsync();

        Assert.Equal(ComputeBackend.WebGpu, translator.Capabilities.Backend);
        Assert.Equal("marian-onnx-python", translator.Capabilities.EngineName);
        Assert.Equal(512, translator.Capabilities.MaxSourceTokens);
        Assert.Equal("beam 6, at most 512 new tokens, length penalty 1, early stopping off",
            translator.DecodeDescription);
    }

    [Fact]
    public async Task TheTranslatorDeclaresWhatItCannotDoBeforeItHasLoadedAnything()
    {
        // Read by TranscriptTranslation before the pass starts, so these have to be answers rather
        // than an exception. Every one of them is a property of translation itself and not of a
        // checkpoint, which is why declaring them costs nothing.
        using var fake = FakeSidecarProcess.Scripted(Script());
        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        Assert.False(translator.Capabilities.RequiresSourceLanguage);
        Assert.False(translator.Capabilities.PreservesWordTimings);
        Assert.False(translator.Capabilities.HonoursContext);
        Assert.Equal(">>eng<<", translator.Capabilities.TargetToken);
    }

    [Fact]
    public async Task ADecodeInAnotherProcessCannotBeStoppedAndTheCapabilitySaysSo()
    {
        // The one behaviour the port took away. The in-process search polled between beam steps;
        // this cannot, so cancelling stops the next segment being sent and lets the one in flight
        // finish — which is exactly what false means here.
        using var fake = FakeSidecarProcess.Scripted(Script());
        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        Assert.False(translator.Capabilities.SupportsCancellation);
    }

    [Fact]
    public async Task ASourcePastTheLimitIsRefusedRatherThanTranslated()
    {
        using var fake = FakeSidecarProcess.Scripted(Script(
            new { op = "load", emit = new[] { TranslatorCapabilities } },
            new { op = "parity", emit = new[] { """{"id":{id},"type":"result","available":true,"passed":true,"identical":6,"total":6,"differing":[]}""" } },
            new { op = "translate", emit = new[] { """{"id":{id},"type":"result","tokens":900,"refused":true}""" } }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        var failure = await Assert.ThrowsAsync<SegmentTooLongException>(async () =>
        {
            await foreach (var _ in translator.TranslateAsync([Segment("una frase muy larga")], TranslationOptions.Default))
            {
            }
        });

        Assert.Equal(900, failure.Tokens);
        Assert.Equal(512, failure.Limit);
        Assert.Equal(0, failure.SegmentIndex);
    }

    [Fact]
    public async Task AnEmptySegmentIsYieldedEmptyRatherThanHandedToTheModel()
    {
        // A bare target token is a valid input, and the checkpoint given one confidently writes a
        // sentence nobody said. The script below has no translate arm, so a segment that reached the
        // sidecar would come back as an error from Script's default and fail this test.
        using var fake = FakeSidecarProcess.Scripted(Script(
            new { op = "load", emit = new[] { TranslatorCapabilities } },
            new { op = "parity", emit = new[] { """{"id":{id},"type":"result","available":true,"passed":true,"identical":6,"total":6,"differing":[]}""" } }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        var translated = new List<TranscriptSegment>();
        await foreach (var segment in translator.TranslateAsync([Segment("   ")], TranslationOptions.Default))
        {
            translated.Add(segment);
        }

        Assert.Equal(string.Empty, Assert.Single(translated).Text);
    }

    [Fact]
    public async Task ATranslatedSegmentKeepsItsTimesAndItsSpeakerAndLosesItsWords()
    {
        using var fake = FakeSidecarProcess.Scripted(Script(
            new { op = "load", emit = new[] { TranslatorCapabilities } },
            new { op = "parity", emit = new[] { """{"id":{id},"type":"result","available":true,"passed":true,"identical":6,"total":6,"differing":[]}""" } },
            new { op = "translate", emit = new[] { """{"id":{id},"type":"result","tokens":9,"text":"Good morning.","refused":false}""" } }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        var source = Segment("Buenos días.") with
        {
            Speaker = "Speaker 1",
            Words = [new TranscriptWord { Text = "Buenos", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(1) }],
        };

        var translated = new List<TranscriptSegment>();
        await foreach (var segment in translator.TranslateAsync([source], TranslationOptions.Default))
        {
            translated.Add(segment);
        }

        var only = Assert.Single(translated);
        Assert.Equal("Good morning.", only.Text);
        Assert.Equal(source.Start, only.Start);
        Assert.Equal(source.End, only.End);
        Assert.Equal("Speaker 1", only.Speaker);

        // The clause of the contract that costs something. Word timings from before a translation,
        // attached to the text after it, are a lie with timestamps on it.
        Assert.Empty(only.Words);
    }

    [Fact]
    public async Task ATranslationWithNeitherTextNorARefusalIsNotPassedOffAsAnEmptyOne()
    {
        using var fake = FakeSidecarProcess.Scripted(Script(
            new { op = "load", emit = new[] { TranslatorCapabilities } },
            new { op = "parity", emit = new[] { """{"id":{id},"type":"result","available":true,"passed":true,"identical":6,"total":6,"differing":[]}""" } },
            new { op = "translate", emit = new[] { """{"id":{id},"type":"result","tokens":9}""" } }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        await Assert.ThrowsAsync<PythonSidecarException>(async () =>
        {
            await foreach (var _ in translator.TranslateAsync([Segment("hola")], TranslationOptions.Default))
            {
            }
        });
    }

    [Fact]
    public async Task AFailedTranslationParityCheckIsReportedWithWhatDiffered()
    {
        using var fake = FakeSidecarProcess.Scripted(Script(
            new { op = "load", emit = new[] { TranslatorCapabilities } },
            new
            {
                op = "parity",
                emit = new[]
                {
                    """{"id":{id},"type":"result","available":true,"passed":false,"identical":0,"total":6,"differing":[{"source":">>eng<< hola","expected":"hello","actual":"hello hello hello"}]}""",
                },
            }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        await translator.LoadAsync();

        Assert.NotNull(translator.Parity);
        Assert.False(translator.Parity!.Passed);
        Assert.Equal(0, translator.Parity.Identical);
        Assert.Equal(6, translator.Parity.Total);
        Assert.Contains("hello hello hello", Assert.Single(translator.Parity.Differing), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheParityCheckIsSkippedOnTheCpuBecauseTheCpuIsWhatItComparesAgainst()
    {
        // No parity rule below, so asking for one comes back as an error from Script's default and
        // fails this test. Running the reference against itself would cost six decodes to prove
        // nothing.
        using var fake = FakeSidecarProcess.Scripted(Script(
            new
            {
                op = "load",
                emit = new[]
                {
                    """{"id":{id},"type":"result","capabilities":{"engineName":"marian-onnx-python","modelId":"opus-mt","backend":"cpu","maxSourceTokens":512}}""",
                },
            }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        await translator.LoadAsync();

        Assert.Equal(ComputeBackend.Cpu, translator.Capabilities.Backend);
        Assert.Null(translator.Parity);
    }

    [Fact]
    public async Task AMissingParityFixtureIsSaidToBeMissingRatherThanFailed()
    {
        // A stack with no fixture is not a stack that failed the check. Reporting it as a failure
        // would put a warning in front of people whose only problem is an incomplete install.
        using var fake = FakeSidecarProcess.Scripted(Script(
            new { op = "load", emit = new[] { TranslatorCapabilities } },
            new { op = "parity", emit = new[] { """{"id":{id},"type":"result","available":false,"reason":"no reference committed"}""" } }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        await translator.LoadAsync();

        Assert.Null(translator.Parity);
    }

    // -- the diariser --------------------------------------------------------------------------

    [Fact]
    public async Task TheDiariserIsNeverParityChecked()
    {
        // **Parity compares two paths to one answer, and this arm has one.** pyannote's pipeline is
        // torch on both stages with no ONNX route, so the only comparison available would be a
        // tensor against itself — and the sidecar refuses the `parity` op for this kind by name, so
        // asking anyway would surface as a request error on a load that was otherwise fine.
        //
        // **Asserted on a non-CPU backend, because that is where the rule could go wrong.** The
        // Sortformer arm checks everything but the CPU, so a kind-blind implementation would send a
        // parity request here the moment somebody ran this diariser on CUDA. No `parity` reply is
        // scripted below: if the check were attempted, the fake sidecar would have nothing to
        // answer with and the load would not complete.
        //
        // The arm this replaced had the opposite rule and its own test — DiariZen's ONNX speaker
        // embedder was checked even when it reported the CPU, because its reference was torch. Both
        // the fixture and that test left with the engine they measured, on 2026-08-27.
        using var fake = FakeSidecarProcess.Scripted(Script(
            new
            {
                op = "load",
                emit = new[]
                {
                    """{"id":{id},"type":"result","capabilities":{"engineName":"pyannote-torch-python","modelId":"pyannote","backend":"cuda","segmentationBackend":"torch:cuda","embeddingBackend":"torch:cuda","supportsFixedSpeakerCount":false,"maxSpeakers":null,"reliableUpToSeconds":null,"honoursPostProcessing":false,"fellBackFrom":[]}}""",
                },
            }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var labeller = new SidecarSpeakerLabeller(
            new SidecarLabellerOptions
            {
                ModelPath = "unread-directory",
                Provider = "cuda",
            },
            sidecar);

        await labeller.LoadAsync();

        Assert.Equal(ComputeBackend.Cuda, labeller.Capabilities.Backend);

        // Asserted rather than merely scripted: a rename of the JSON key would otherwise ship in
        // silence. Both stages report the same runtime, so it also stands for segmentation.
        Assert.Equal("torch:cuda", labeller.Capabilities.EmbeddingBackend);

        // **The assertion is that the load completed at all.** No `parity` reply is scripted, so if
        // this labeller had sent one the fake sidecar would have had nothing to answer with and
        // LoadAsync would not have returned. That is what makes this a statement about the rule —
        // no diariser is parity-checked, on any backend — rather than about a null.
    }


    [Fact]
    public async Task TheDiarisersDeclaredLimitsAreTheOnesItReportsAfterLoading()
    {
        using var fake = FakeSidecarProcess.Scripted(Script(
            new { op = "load", emit = new[] { DiariserCapabilities } },
            new { op = "parity", emit = new[] { """{"id":{id},"type":"result","available":true,"passed":true,"maxAbsDiff":1.0e-06,"decisionFlipPercent":0,"tolerance":1.0e-04}""" } }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var labeller = new SidecarSpeakerLabeller(
            new SidecarLabellerOptions { ModelPath = "unread-directory", Provider = "auto" }, sidecar);

        await labeller.LoadAsync();

        Assert.Equal(SidecarSpeakerLabeller.DeclaredLimits.MaxSpeakers, labeller.Capabilities.MaxSpeakers);
        Assert.Equal(SidecarSpeakerLabeller.DeclaredLimits.ReliableUpTo, labeller.Capabilities.ReliableUpTo);
        Assert.Equal(ComputeBackend.Cpu, labeller.Capabilities.Backend);
    }

    [Fact]
    public async Task ASidecarWhoseLimitsDisagreeWithThisBuildsIsRefusedRatherThanBelieved()
    {
        // The check that keeps the declared copy honest. The window warns about a four-speaker cap
        // and a fifty-minute bound before a run; a bundled Python that means different numbers turns
        // those warnings into misstatements, and nothing in the output would show it.
        using var fake = FakeSidecarProcess.Scripted(Script(
            new
            {
                op = "load",
                emit = new[]
                {
                    """{"id":{id},"type":"result","capabilities":{"engineName":"pyannote-torch-python","backend":"cpu","supportsFixedSpeakerCount":false,"maxSpeakers":8,"reliableUpToSeconds":3000}}""",
                },
            }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var labeller = new SidecarSpeakerLabeller(
            new SidecarLabellerOptions { ModelPath = "unread-directory" }, sidecar);

        var failure = await Assert.ThrowsAsync<PythonSidecarException>(async () => await labeller.LoadAsync());

        Assert.Contains("8 speakers", failure.Message, StringComparison.Ordinal);
        Assert.Contains("out of step", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDiarisersCapabilitiesAreNotAvailableBeforeItHasLoaded()
    {
        using var fake = FakeSidecarProcess.Scripted(Script());
        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var labeller = new SidecarSpeakerLabeller(
            new SidecarLabellerOptions { ModelPath = "unread-directory" }, sidecar);

        // The limits are; the backend and the model id are not, and a guess at those is how
        // provenance becomes fiction. The declared cap is null rather than 4 since the four-slot
        // graph was retired — still an answer available before a load, and still the point of the
        // split: null is "no cap", not "ask later".
        Assert.Null(SidecarSpeakerLabeller.DeclaredLimits.MaxSpeakers);
        Assert.Throws<InvalidOperationException>(() => labeller.Capabilities);
    }

    [Fact]
    public async Task ATranslatorParityCheckThatCrashesIsAResultThatSaysItDidNotRunRatherThanNothing()
    {
        using var fake = FakeSidecarProcess.Scripted(Script(
            new { op = "load", emit = new[] { TranslatorCapabilities } },
            new { op = "parity", emit = new[] { """{"id":{id},"type":"error","kind":"internal","message":"boom"}""" } }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        await translator.LoadAsync();

        var parity = Assert.IsType<TranslationParityResult>(translator.Parity);
        Assert.False(parity.Ran);
        Assert.False(parity.Passed);
        Assert.Contains("could not be run", parity.Describe(), StringComparison.Ordinal);
        Assert.Contains("boom", parity.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATranslatorThatFellBackSaysFromWhat()
    {
        using var fake = FakeSidecarProcess.Scripted(Script(
            new
            {
                op = "load",
                emit = new[]
                {
                    """{"id":{id},"type":"result","capabilities":{"engineName":"marian-onnx-python","modelId":"opus-mt","backend":"cpu","maxSourceTokens":512,"beams":6,"maxNewTokens":512,"lengthPenalty":1.0,"earlyStopping":false,"fellBackFrom":["webgpu: no adapter"]}}""",
                },
            }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        await translator.LoadAsync();

        Assert.Equal(ComputeBackend.Cpu, translator.Capabilities.Backend);
        Assert.Equal(["webgpu: no adapter"], translator.FellBackFrom);
    }

    [Fact]
    public async Task TheDecodeDescriptionIsInvariantOnACommaDecimalMachine()
    {
        // "length penalty 0,65" is what this read under es-PY until 2026-08-22: the sentence goes
        // into run reports, and CA1305 cannot see an interpolated string.
        using var fake = FakeSidecarProcess.Scripted(Script(
            new
            {
                op = "load",
                emit = new[]
                {
                    """{"id":{id},"type":"result","capabilities":{"engineName":"marian-onnx-python","modelId":"opus-mt","backend":"cpu","maxSourceTokens":512,"beams":6,"maxNewTokens":512,"lengthPenalty":0.65,"earlyStopping":true}}""",
                },
            }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var translator = new SidecarTranscriptTranslator(TranslatorOptions, sidecar);

        await translator.LoadAsync();

        Assert.Equal("beam 6, at most 512 new tokens, length penalty 0.65, early stopping on", translator.DecodeDescription);
    }

    [Fact]
    public void StaleStagedFilesAreSweptAndFreshOnesAreLeftAlone()
    {
        // A host killed mid-label never reaches the finally that deletes its staged WAV; until
        // 2026-08-22 nothing swept such a file, ever. The sweep takes only files too old to belong
        // to a live run, so a concurrent instance's staging is not touched.
        var directory = TestTemp.NewDirectory("uindosill-stale");
        try
        {
            var stale = Path.Combine(directory, "uindosill-diarise-old.wav");
            var fresh = Path.Combine(directory, "uindosill-diarise-new.wav");
            var other = Path.Combine(directory, "somebody-elses.wav");
            File.WriteAllBytes(stale, [0]);
            File.WriteAllBytes(fresh, [0]);
            File.WriteAllBytes(other, [0]);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(3));
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow - TimeSpan.FromHours(3));

            var swept = SidecarSpeakerLabeller.SweepStaleStagedFiles(TimeSpan.FromHours(1), directory);

            Assert.Equal(1, swept);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(other));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TranscriptSegment Segment(string text) => new()
    {
        Text = text,
        Start = TimeSpan.FromSeconds(1),
        End = TimeSpan.FromSeconds(3),
        SourceSegmentIndex = 0,
    };
}
