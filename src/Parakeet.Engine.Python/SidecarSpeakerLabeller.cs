using System.Text.Json;
using Parakeet.Audio;
using Parakeet.Core.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.Python;

/// <summary>
/// Whether this machine's stack reproduces the diariser the published figures describe.
/// </summary>
/// <remarks>
/// The numbers travel with the verdict rather than only a boolean, because "the check failed" with
/// no magnitude tells a user nothing they can act on. A stack sitting just past the tolerance and
/// one scoring 53% diarisation error are different situations and deserve different reactions.
/// </remarks>
public sealed record ParityResult
{
    public required bool Passed { get; init; }

    /// <summary>
    /// False when the check itself could not be run — the sidecar answered the <c>parity</c>
    /// request with an error rather than a verdict. <see cref="Passed"/> is then false too, and
    /// <see cref="Reason"/> carries the error: the labels are unverified rather than known wrong.
    /// </summary>
    /// <remarks>
    /// The third state, and the one that used to be silent. Until 2026-08-22 a check that crashed
    /// was reported as null — the same null as "not run" on the CPU — and the labels went out
    /// with nothing said. A missing fixture is still null: the sidecar reports that structurally,
    /// before anything runs, and it is not a failure of anything on this machine.
    /// </remarks>
    public bool Ran { get; init; } = true;

    /// <summary>
    /// Why it failed when no magnitude says so: the sidecar's own reason — a shape that does not
    /// match the reference's, probabilities that are not finite — or, when <see cref="Ran"/> is
    /// false, the error the check came back with. Null when the verdict is in the numbers below.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Largest disagreement with the reference, over the fixture's probabilities. NaN when the
    /// verdict carried no magnitude — see <see cref="Reason"/>.
    /// </summary>
    public double MaxAbsoluteDifference { get; init; } = double.NaN;

    /// <summary>
    /// How many frame decisions differ. Zero is common on a passing <i>and</i> a failing stack —
    /// CUDA disagrees at 8.1e-04 while flipping nothing on this fixture — which is exactly why the
    /// verdict is taken on the probabilities and not on this.
    /// </summary>
    public double DecisionFlipPercent { get; init; }

    public double Tolerance { get; init; } = double.NaN;

    /// <summary>
    /// The sentence a run prints about this result, or null when there is nothing to say because
    /// the check passed. One place for the three failing shapes — a magnitude past the tolerance,
    /// a reason given instead of one, a check that could not run — so the command line and the
    /// window cannot come to describe them differently.
    /// </summary>
    public string? Describe() =>
        !Ran ? SpeakerLabelling.DescribeParityNotRun(Reason ?? "the sidecar gave no reason")
        : Passed ? null
        : Reason is { Length: > 0 } reason ? SpeakerLabelling.DescribeParityFailure(reason)
        : SpeakerLabelling.DescribeParityFailure(MaxAbsoluteDifference, Tolerance);
}

/// <summary>How the sidecar's diariser is loaded and driven.</summary>
public sealed record SidecarLabellerOptions
{
    /// <summary>Path to <c>sortformer-default.onnx</c>. Required; there is no default location.</summary>
    public required string ModelPath { get; init; }

    /// <summary>Catalogue id carried into the transcript's provenance beside the ASR model's.</summary>
    public string ModelId { get; init; } = "sortformer-4spk-v2.1";

    /// <summary>
    /// Intra-op threads for the ONNX session, or 0 for the diariser's own default of 12 — the
    /// number every CPU figure in this project was measured with. Not "let ONNX Runtime choose":
    /// that is the translator's 0, and the two differ on purpose, because changing the diariser's
    /// thread count is changing the conditions its 16.33% was produced under.
    /// </summary>
    public int IntraOpThreads { get; init; }

    /// <summary>
    /// Execution provider: <c>auto</c>, <c>cpu</c>, <c>cuda</c>, <c>webgpu</c> or <c>dml</c>.
    /// </summary>
    /// <remarks>
    /// <b>This changes the answer, not only the speed.</b> Measured 2026-08-21 on AMI test: CPU
    /// 16.3324%, WebGPU 16.3319%, CUDA 16.1021%, DirectML at its own default 53.15%. The
    /// provider therefore travels into the transcript's provenance, and a non-CPU provider is
    /// verified against the parity fixture before it is trusted.
    /// </remarks>
    public string Provider { get; init; } = "cpu";

    /// <summary>ONNX Runtime graph optimisation level, or null for the provider's safe default.</summary>
    public string? GraphOptimization { get; init; }

    /// <summary>The tuned post-processing. Changing it invalidates the measured DER.</summary>
    public SortformerPostProcessing PostProcessing { get; init; } = SortformerPostProcessing.Default;
}

/// <summary>
/// The knobs NeMo's <c>ts_vad_post_processing</c> turns. Tuned on the 18 AMI development meetings
/// and applied unchanged to the 16 test meetings; changing a default here invalidates the measured
/// DER, which is that parameter set's number and no other's.
/// </summary>
public sealed record SortformerPostProcessing
{
    public static SortformerPostProcessing Default { get; } = new();

    public double Onset { get; init; } = 0.5;

    public double Offset { get; init; } = 0.5;

    public TimeSpan PadOnset { get; init; } = TimeSpan.FromMilliseconds(50);

    public TimeSpan PadOffset { get; init; } = TimeSpan.Zero;

    /// <summary>Segments shorter than this are deleted. Zero in the tuned set.</summary>
    public TimeSpan MinimumSpeechDuration { get; init; } = TimeSpan.Zero;

    /// <summary>Gaps shorter than this between two segments of one speaker are filled.</summary>
    public TimeSpan MinimumSilenceDuration { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Speaker labelling by NVIDIA's Streaming Sortformer 4spk v2.1, run in the bundled Python.
/// </summary>
/// <remarks>
/// <para>
/// The engine moved out of process on 2026-08-21. What it buys is that the numerical core is
/// NVIDIA's own <c>SortformerModules</c> — the arrival-order speaker cache imported and called
/// rather than reimplemented — and that the mel featurizer is the one validated bit-exact against
/// NeMo's <c>FilterbankFeatures</c>. What it costs is a process boundary, which is this class.
/// </para>
/// <para>
/// <b>The audio crosses as a file, not as PCM down the pipe.</b> The host decodes and resamples,
/// exactly as it did when the engine was in-process, and hands over a 16 kHz mono WAV that it
/// deletes afterwards. A pipe carrying both a protocol and a megabyte of samples is a pipe with two
/// failure modes, and the decode belongs to the side that already owns Media Foundation.
/// </para>
/// <para>
/// <b>Four speakers, and no more.</b> The cap is architectural: above it a fifth voice is merged
/// into one of the four rather than reported. Its labels are established only to fifty minutes.
/// Both facts arrive through <see cref="Capabilities"/> so a caller cannot forget them.
/// </para>
/// </remarks>
public sealed class SidecarSpeakerLabeller : ISpeakerLabeller
{
    private const int TargetSampleRate = 16000;

    private readonly SidecarLabellerOptions _options;
    private readonly PythonSidecar _sidecar;
    private readonly bool _ownsSidecar;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private SpeakerLabellerCapabilities? _capabilities;

    public SidecarSpeakerLabeller(SidecarLabellerOptions options, PythonSidecar? sidecar = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _ownsSidecar = sidecar is null;
        _sidecar = sidecar ?? new PythonSidecar(PythonRuntime.Resolve());
    }

    /// <summary>
    /// Available only after <see cref="LoadAsync"/>: the backend and the model id are the sidecar's
    /// to report, and reporting a guess before it has answered is how provenance becomes fiction.
    /// </summary>
    public SpeakerLabellerCapabilities Capabilities =>
        _capabilities ?? throw new InvalidOperationException(
            "The diariser's capabilities are not known until it has been loaded.");

    /// <summary>
    /// The two limits of this model that a caller needs <i>before</i> anything is loaded, and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the questions the window has to answer while the queue is being built and the
    /// weights are still on disk: how many voices can be told apart, and how long a recording the
    /// labels have been established on. Both drive warnings that are worth reading before a batch
    /// starts and worthless after it — "seven speakers was never reachable" said afterwards is not
    /// a warning, it is an epitaph — so they cannot wait for a 453 MiB load to answer them.
    /// </para>
    /// <para>
    /// <b>This is a second copy of two constants that live in the sidecar, and it is checked
    /// against them.</b> <c>MAX_SPEAKERS</c> and <c>RELIABLE_UP_TO_SECONDS</c> belong to the engine
    /// and are reported by it; a copy here that quietly disagreed would put a different number in
    /// front of a user than the one the run honours. <see cref="LoadAsync"/> therefore refuses a
    /// sidecar whose answer differs from this, which is the only thing that keeps the duplicate
    /// from going stale — the check fires on the machine where the two halves are actually
    /// together.
    /// </para>
    /// <para>
    /// <b>The backend is not here, and its absence is the point.</b> It is the one thing only the
    /// sidecar can answer, and this type has no field for it. Read <see cref="Capabilities"/> for
    /// what ran and this for what the model is.
    /// </para>
    /// </remarks>
    public static SpeakerLabellerLimits DeclaredLimits { get; } = new()
    {
        Name = "sortformer-onnx-python",

        // The model estimates the count and cannot be told one.
        SupportsFixedSpeakerCount = false,

        // Architectural: the graph has four speaker slots, and a fifth voice is merged into one of
        // the four rather than reported.
        MaxSpeakers = 4,

        // Where the evidence stops, not where the model does. Measured 2026-08-20 by growing a
        // window from a fixed onset: right at 10, 30, 40 and 50 minutes across two episodes, then
        // wrong past an hour.
        ReliableUpTo = TimeSpan.FromMinutes(50),
    };

    public async ValueTask LoadAsync(CancellationToken ct = default)
    {
        if (_capabilities is not null)
        {
            // Loaded — but the child may have died since, and this is where that is found out:
            // before the caller decodes and stages a whole file for it, rather than by the write
            // that would follow. The sidecar is not restarted; a failure of the sidecar is every
            // remaining file, by design, and what this buys is that each of them learns it at once.
            _sidecar.ThrowIfFaulted();
            return;
        }

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_capabilities is not null)
            {
                _sidecar.ThrowIfFaulted();
                return;
            }

            await _sidecar.StartAsync(ct).ConfigureAwait(false);

            var reply = await _sidecar.SendAsync("load", writer =>
            {
                writer.WriteString("engine", "diariser");
                writer.WriteString("path", _options.ModelPath);
                writer.WriteString("modelId", _options.ModelId);
                writer.WriteNumber("threads", _options.IntraOpThreads);
                writer.WriteString("provider", _options.Provider);
                if (_options.GraphOptimization is { Length: > 0 } level)
                {
                    writer.WriteString("graphOptimization", level);
                }
            }, null, ct).ConfigureAwait(false);

            // Read into a local and published to the field only once every check has passed.
            // `_capabilities` is also the "already loaded" short-circuit at the top of this method,
            // so assigning it first would mean a throw from either check below left this labeller
            // marked loaded — and the next call would return immediately, having skipped both. In a
            // batch, where one labeller serves every file and a per-file failure does not stop the
            // run, that turns a refusal into a warning that fires once and is then never seen again.
            var capabilities = ReadCapabilities(reply.GetProperty("capabilities"));
            CheckDeclaredLimits(capabilities);
            FellBackFrom = ExecutionProviders.ReadFellBackFrom(reply.GetProperty("capabilities"));

            // Every backend but the CPU is checked against the committed reference before it is
            // used, because the failure this catches is silent: measured 2026-08-21, DirectML at
            // ONNX Runtime's default settings scores 53.15% diarisation error against the CPU's
            // 16.33% while emitting speaker turns that read as perfectly ordinary. Two chunks of
            // synthetic mel is all it costs, and it is the only thing standing between a user and
            // a transcript that is wrong in a way nothing in it reveals.
            if (capabilities.Backend != ComputeBackend.Cpu)
            {
                Parity = await CheckParityAsync(ct).ConfigureAwait(false);
            }

            _capabilities = capabilities;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// The parity check's result, or null when it was not run — which is the CPU case, since the
    /// CPU is what everything else is compared against.
    /// </summary>
    public ParityResult? Parity { get; private set; }

    /// <summary>
    /// The providers <c>auto</c> tried and passed over before the one that loaded, each with the
    /// reason it did not build. Empty when the first candidate built, or when the provider was
    /// named — a named provider is never fallen back from.
    /// </summary>
    public IReadOnlyList<string> FellBackFrom { get; private set; } = [];

    private async Task<ParityResult?> CheckParityAsync(CancellationToken ct)
    {
        JsonElement reply;
        try
        {
            reply = await _sidecar.SendAsync("parity", _ => { }, null, ct).ConfigureAwait(false);
        }
        catch (PythonEngineException exception)
        {
            // The check was asked for and could not answer. That is not the CPU's "not run" and
            // not a fixture that is missing — the sidecar reports a missing fixture structurally,
            // below, before anything runs — it is a check that crashed, and until 2026-08-22 it was
            // reported as the same null as the other two, so the labels went out unverified with
            // nothing said. It is a result now, one that says it did not run and why.
            return new ParityResult { Passed = false, Ran = false, Reason = exception.Message };
        }

        if (!reply.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True)
        {
            // No fixture committed: nothing was compared and nothing failed, and the null says so.
            return null;
        }

        return new ParityResult
        {
            Passed = reply.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True,
            Reason = reply.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null,
            MaxAbsoluteDifference = Number(reply, "maxAbsDiff") ?? double.NaN,
            DecisionFlipPercent = Number(reply, "decisionFlipPercent") ?? 0,
            Tolerance = Number(reply, "tolerance") ?? double.NaN,
        };
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public async Task<IReadOnlyList<SpeakerTurn>> LabelAsync(
        IAudioSource audio,
        SpeakerLabellingOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        await LoadAsync(ct).ConfigureAwait(false);

        var wav = await WriteResampledWavAsync(audio, ct).ConfigureAwait(false);
        try
        {
            var total = audio.Duration;
            var relay = progress is null ? null : new Progress<(int Completed, int Total)>(step =>
            {
                // The sidecar counts chunks; the host reports audio. Converting here keeps the
                // protocol's unit its own and the UI's unit the UI's.
                var fraction = step.Total > 0 ? step.Completed / (double)step.Total : 0d;
                progress.Report(new TranscriptionProgress
                {
                    Stage = TranscriptionStage.LabellingSpeakers,
                    Processed = total is { } known ? known * Math.Clamp(fraction, 0d, 1d) : TimeSpan.Zero,
                    Total = total,
                });
            });

            var reply = await _sidecar.SendAsync("label", writer =>
            {
                writer.WriteString("wav", wav);
                writer.WriteStartObject("postProcessing");
                writer.WriteNumber("onset", _options.PostProcessing.Onset);
                writer.WriteNumber("offset", _options.PostProcessing.Offset);
                writer.WriteNumber("padOnset", _options.PostProcessing.PadOnset.TotalSeconds);
                writer.WriteNumber("padOffset", _options.PostProcessing.PadOffset.TotalSeconds);
                writer.WriteNumber("minimumSpeechSeconds", _options.PostProcessing.MinimumSpeechDuration.TotalSeconds);
                writer.WriteNumber("minimumSilenceSeconds", _options.PostProcessing.MinimumSilenceDuration.TotalSeconds);
                writer.WriteEndObject();
            }, relay, ct).ConfigureAwait(false);

            return ReadTurns(reply);
        }
        finally
        {
            TryDelete(wav);
        }
    }

    /// <summary>
    /// Drains the source, resamples to 16 kHz and writes a temporary mono WAV — as 32-bit float, so
    /// the sidecar reads exactly the samples the host produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole file lands in memory before it is written, which is what the in-process engine did
    /// too — the diariser is not a streaming consumer, and its speaker cache needs the recording in
    /// order anyway. Three hours of 16 kHz mono float32 is about 690 MB, which is the ceiling this
    /// accepts; on disk it is the same figure, since the file is the buffer written out.
    /// </para>
    /// <para>
    /// Float rather than 16-bit PCM because the sidecar's output is held to the Python reference's,
    /// and PCM16 moved it. Measured 2026-08-22 on the CPU (<c>docs/UNPROVEN.md</c>): on a 48 kHz
    /// MP3 decoded and resampled here, the reference diarising the PCM16 file against the same floats
    /// written exact scored 2.50% DER at collar 0.25 and flipped 1.10% of frame-speaker cells, while
    /// the speaker count stood still — ten times the CUDA gap that keeps CUDA out of <c>auto</c>. On
    /// 16 kHz 16-bit input, which is AMI and therefore every published figure, the round trip moves
    /// 0.25% of samples by one LSB and the DER by nothing, which is why it went unseen. A float file
    /// is twice the size and costs the reference nothing to read: soundfile hands back the bytes.
    /// </para>
    /// </remarks>
    internal static async Task<string> WriteResampledWavAsync(IAudioSource audio, CancellationToken ct)
    {
        var resampler = new Resampler(audio.SampleRate, TargetSampleRate);
        var samples = new List<float>();

        await foreach (var block in audio.ReadAsync(ct).ConfigureAwait(false))
        {
            resampler.Process(block.Span, samples);
        }

        resampler.Complete(samples);

        var path = Path.Combine(Path.GetTempPath(), $"uindosill-diarise-{Guid.NewGuid():N}.wav");
        try
        {
            WavWriter.WriteFloat32File(path, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples), TargetSampleRate);
        }
        catch (Exception exc)
        {
            TryDelete(path);
            throw new PythonSidecarException(
                $"Could not stage the audio for the diariser at {path}: {exc.Message}", exc);
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A temp file that outlives the run is untidy, not a failure worth surfacing.
        }
    }

    /// <summary>
    /// Refuses a sidecar whose limits are not the ones this build has been telling people about.
    /// </summary>
    /// <remarks>
    /// The whole justification for <see cref="DeclaredLimits"/> existing is this check. A window
    /// that has already warned about a fifty-minute bound and a four-speaker cap has made a promise
    /// on the engine's behalf, and a bundled Python that disagrees turns that promise into a
    /// misstatement nothing else would catch — the labels would come back looking ordinary. It is
    /// the same failure the protocol-version check guards against and it gets the same treatment:
    /// the two halves are out of step, so say so rather than run.
    /// </remarks>
    private static void CheckDeclaredLimits(SpeakerLabellerCapabilities reported)
    {
        // Name is not compared: it is the catalogue's id where there is one, and the two sides get
        // it from different places by design. The numbers are the claim.
        if (reported.MaxSpeakers != DeclaredLimits.MaxSpeakers
            || reported.ReliableUpTo != DeclaredLimits.ReliableUpTo
            || reported.SupportsFixedSpeakerCount != DeclaredLimits.SupportsFixedSpeakerCount)
        {
            throw new PythonSidecarException(
                $"The bundled Python's diariser reports a cap of {Describe(reported.MaxSpeakers)} speakers and " +
                $"labels established to {Describe(reported.ReliableUpTo)}, and this build has been saying " +
                $"{Describe(DeclaredLimits.MaxSpeakers)} and {Describe(DeclaredLimits.ReliableUpTo)}. Those " +
                "numbers are what the warnings before a run are written from, so the two halves are out of step — " +
                "reinstall rather than mixing them.");
        }
    }

    private static string Describe(int? value) => value?.ToString() ?? "no limit on the";

    private static string Describe(TimeSpan? value) =>
        value is { } bound ? $"{bound.TotalMinutes:F0} minutes" : "no measured length";

    private static SpeakerLabellerCapabilities ReadCapabilities(JsonElement element) => new()
    {
        EngineName = element.TryGetProperty("engineName", out var name)
            ? name.GetString() ?? "sortformer-onnx-python"
            : "sortformer-onnx-python",
        ModelId = element.TryGetProperty("modelId", out var id) ? id.GetString() : null,
        Backend = element.TryGetProperty("backend", out var backend)
            ? ExecutionProviders.Parse(backend.GetString())
            : ComputeBackend.Cpu,
        SupportsFixedSpeakerCount = element.TryGetProperty("supportsFixedSpeakerCount", out var fixedCount)
                                    && fixedCount.ValueKind == JsonValueKind.True,
        MaxSpeakers = element.TryGetProperty("maxSpeakers", out var max) && max.ValueKind == JsonValueKind.Number
            ? max.GetInt32()
            : null,
        ReliableUpTo = element.TryGetProperty("reliableUpToSeconds", out var reliable)
                       && reliable.ValueKind == JsonValueKind.Number
            ? TimeSpan.FromSeconds(reliable.GetDouble())
            : null,
    };

    private static IReadOnlyList<SpeakerTurn> ReadTurns(JsonElement reply)
    {
        if (!reply.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            throw new PythonSidecarException("The diariser returned no turns array.");
        }

        var result = new List<SpeakerTurn>(turns.GetArrayLength());
        foreach (var turn in turns.EnumerateArray())
        {
            var built = new SpeakerTurn
            {
                // Rounded to the tick rather than truncated, as the RTTM reader already did; a
                // turn end that arithmetic left a hair under its decimal is otherwise a tick short.
                Start = AudioMath.SecondsToTime(turn.GetProperty("start").GetDouble()),
                End = AudioMath.SecondsToTime(turn.GetProperty("end").GetDouble()),
                Speaker = turn.GetProperty("speaker").GetString() ?? "spk?",
            };

            built.Validate();
            result.Add(built);
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        _loadGate.Dispose();
        if (_ownsSidecar)
        {
            await _sidecar.DisposeAsync().ConfigureAwait(false);
        }
    }
}
