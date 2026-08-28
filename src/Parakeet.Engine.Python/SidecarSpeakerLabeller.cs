using System.Text.Json;
using Parakeet.Audio;
using Parakeet.Core.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Core.Transcription;

namespace Parakeet.Engine.Python;

/// <summary>How the sidecar's diariser is loaded and driven.</summary>
public sealed record SidecarLabellerOptions
{
    /// <summary>
    /// The model directory — which keeps the upstream repository's subdirectory layout rather than
    /// being flattened, because the pipeline resolves its parts through its own <c>config.yaml</c>.
    /// Required; there is no default location.
    /// </summary>
    /// <remarks>
    /// <b>A directory, and only a directory, since 2026-08-27.</b> This field meant a <c>.onnx</c>
    /// file or a directory depending on which of two diarisers was being loaded, and a <c>Kind</c>
    /// beside it told the sidecar which to expect. Sortformer went to <c>attic/sortformer/</c> and
    /// took the ambiguity with it, so neither side has to agree about the meaning of this string
    /// any more — which is one fewer thing that can disagree.
    /// </remarks>
    public required string ModelPath { get; init; }

    /// <summary>Catalogue id carried into the transcript's provenance beside the ASR model's.</summary>
    public string ModelId { get; init; } = "pyannote-speaker-diarization-community-1";

    /// <summary>
    /// Torch intra-op threads, or 0 for the diariser's own default of 12. Not "let the runtime
    /// choose": that is the translator's 0, and the two differ on purpose.
    /// </summary>
    /// <remarks>
    /// <b>The 12 is inherited rather than derived.</b> It was the number every CPU diarisation
    /// figure this project published was measured with, and those figures were the shelved engine's
    /// — so the default now rests on nothing measured about <i>this</i> pipeline. Kept because a
    /// number that has never been shown to be wrong is a better default than one nobody chose, and
    /// recorded as a gap in <c>docs/UNPROVEN.md</c> rather than left looking like evidence.
    /// </remarks>
    public int IntraOpThreads { get; init; }

    /// <summary>
    /// The torch device: <c>auto</c>, <c>cpu</c> or <c>cuda</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A device rather than an execution provider, since 2026-08-27.</b> Both neural stages are
    /// torch on the same device, so <c>webgpu</c> and <c>dml</c> name nothing here and are refused
    /// rather than quietly treated as the CPU. <c>auto</c> is the CPU, because the bundled torch is
    /// the CPU build — resolved rather than negotiated, so there is nothing to fall back from.
    /// </para>
    /// <para>
    /// <b>The measured provider comparison that stood here went with Sortformer.</b> CPU 16.3324%,
    /// WebGPU 16.3319%, CUDA 16.1021% and DirectML 53.15% on AMI test are that graph's numbers, and
    /// no figure of any kind has been produced on this pipeline. The device still travels into the
    /// transcript's provenance, which is worth doing before there is evidence rather than after.
    /// </para>
    /// </remarks>
    public string Provider { get; init; } = "auto";

    /// <summary>
    /// Windows of audio the diariser batches together, or null for the pipeline's own value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is not a number this could have defaulted to.</b> It means "whatever the model
    /// ships", which is what makes running the published artefact's configuration the thing you get
    /// by not choosing. Any default here would be this project picking a number for every machine,
    /// which is what it did until 2026-08-27 and withdrew.
    /// </para>
    /// <para>
    /// <b>Its established effect is on peak memory, not on the labels.</b> Batch 8, 16 and 32 each
    /// returned 225 turns and 5 speakers on <c>two-hosts-three-guests-a</c>; peak working set over
    /// the same three was roughly 3.9, 6.8 and 11.7 GB. A real-time-factor difference was also
    /// published and is <b>withdrawn</b>: the sweep ran the three sizes once each in one process in
    /// ascending order, on a machine that cannot hold the largest arm resident, so batch size and
    /// memory pressure were one condition. Nothing here may be presented to a user as a speed
    /// setting.
    /// </para>
    /// </remarks>
    public int? BatchSize { get; init; }

    /// <summary>
    /// The post-processing thresholds, <b>which the current diariser ignores</b>.
    /// </summary>
    /// <remarks>
    /// Sent and dropped rather than removed. This pipeline binarizes internally at the parameters
    /// its own published figures describe, and reports <c>honoursPostProcessing: false</c> — which
    /// nothing on this side reads, so it is a capabilities-dump field rather than a signal; the
    /// host knows; the field survives because the protocol still carries it and because these
    /// values are the shelved engine's tuned set, which is a record worth keeping where the type
    /// that would use it lives. See <c>attic/sortformer/</c>.
    /// </remarks>
    public DiariserPostProcessing PostProcessing { get; init; } = DiariserPostProcessing.Default;
}

/// <summary>
/// The knobs NeMo's <c>ts_vad_post_processing</c> turned, tuned on the 18 AMI development meetings
/// and applied unchanged to the 16 test meetings.
/// </summary>
/// <remarks>
/// <b>Nothing reads these any more.</b> They were the shelved ONNX diariser's, and the 16.33% AMI
/// figure they produced is that parameter set's number and no other's — which is exactly why they
/// are recorded rather than reset to something tidier. The pipeline that ships now binarizes
/// internally and says so through <c>honoursPostProcessing</c>.
/// </remarks>
public sealed record DiariserPostProcessing
{
    public static DiariserPostProcessing Default { get; } = new();

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
/// Speaker labelling by <c>pyannote/speaker-diarization-community-1</c>, run in the bundled Python.
/// </summary>
/// <remarks>
/// <para>
/// The engine moved out of process on 2026-08-21. What it buys is that the numerical core is
/// upstream's own pipeline, imported and called rather than reimplemented. What it costs is a
/// process boundary, which is this class.
/// </para>
/// <para>
/// <b>The audio crosses as a file, not as PCM down the pipe.</b> The host decodes and resamples,
/// exactly as it did when the engine was in-process, and hands over a 16 kHz mono WAV that it
/// deletes afterwards. A pipe carrying both a protocol and a megabyte of samples is a pipe with two
/// failure modes, and the decode belongs to the side that already owns Media Foundation.
/// </para>
/// <para>
/// <b>No speaker cap and no established length.</b> This pipeline clusters rather than tracking, so
/// there is no architectural ceiling on how many voices it can separate — and nothing about it has
/// been measured, so there is no bound on how long a recording its labels hold up over either. Both
/// facts arrive as nulls through <see cref="Capabilities"/>, which the window renders as "no limit"
/// and "no bound established" so that a caller cannot mistake either for an unasked question.
/// </para>
/// <para>
/// <b>This class labelled with NVIDIA's Streaming Sortformer 4spk v2.1 until 2026-08-27</b>, and
/// that engine carried every diarisation figure this project has published — 16.33% DER on AMI
/// test, four speaker slots, labels established to fifty minutes. It is in <c>attic/sortformer/</c>
/// and none of its numbers describe what runs here.
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
    /// a warning, it is an epitaph — so they cannot wait for the pipeline to load to answer them.
    /// <b>Both are null here</b>, which is itself an answer and not an absence of one: the window
    /// says "no limit" and "no bound established" rather than staying silent.
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
        Name = "pyannote-torch-python",

        // The pipeline clusters rather than tracking, and the clustering is never given a count.
        SupportsFixedSpeakerCount = false,

        // **Null because there is no cap, not because nobody looked.** Upstream's VBxClustering
        // never given a count by this build: `label` calls the pipeline with no `num_speakers`.
        //
        // **That is a statement about this build rather than about upstream, and it was written the
        // other way round until 2026-08-27.** `VBxClustering.expects_num_clusters = false` means a
        // count is not *required*, not that it is ignored — 4.0.7 clamps to min/max and re-clusters
        // with KMeans when a requested count disagrees with what VBx derived. The sidecar's own
        // docstring had this right while this comment had it wrong.
        MaxSpeakers = null,

        // **Null because nothing has been measured**, which the window renders as "no bound
        // established" rather than as "any length". The fifty minutes that stood here was a figure
        // this project produced on Sortformer, and it went to attic/sortformer/ with the graph;
        // inventing a replacement from upstream's benchmark table would be quoting somebody else's
        // corpus as this project's evidence. Upstream publishes DER figures for community-1 on
        // several corpora and not one of them was produced on this project's material through this
        // project's audio path.
        ReliableUpTo = null,
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

        // Once, before the first file: whatever an earlier run left staged and never deleted —
        // a host that died mid-label, until 2026-08-22 the only way a staged file outlived its
        // run — is swept here, and only files old enough that no live run can still own them.
        SweepStaleStagedFiles();
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
                // Omitted rather than sent as null when nobody has chosen, so that "the model's
                // own" is the absence of the field rather than a second shape the sidecar has to
                // agree about — the same rule the settings file follows for the backend.
                if (_options.BatchSize is { } batchSize)
                {
                    writer.WriteNumber("batchSize", batchSize);
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

            // **No parity check, and no backend is exempt from one — there is none to run.**
            // Parity compares two paths to one answer, and this pipeline has one: torch on both
            // stages, no ONNX route. The check that stood here belonged to the ONNX diariser and
            // went to attic/sortformer/ with its fixture; the sidecar now refuses the `parity` op
            // for the diariser by name, so asking anyway would surface as a request error on an
            // otherwise good load. What that check caught was real — DirectML scoring 53.15% DER
            // against the CPU's 16.33% while emitting speaker turns that read as perfectly ordinary
            // — and it is worth knowing that this build has no equivalent guard, which is why
            // docs/UNPROVEN.md carries it rather than this comment implying the risk left too.
            _capabilities = capabilities;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task<IReadOnlyList<SpeakerTurn>> LabelAsync(
        IAudioSource audio,
        SpeakerLabellingOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(options);

        await LoadAsync(ct).ConfigureAwait(false);

        var wav = await WriteResampledWavAsync(audio, ct, progress).ConfigureAwait(false);
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
    /// the speaker count stood still — ten times the CUDA gap that kept CUDA out of <c>auto</c> on the
    /// diariser retired 2026-08-27, which is the scale this was judged against. (<c>auto</c> is the
    /// CPU here for an unrelated reason: the bundled torch is the CPU build.) On
    /// 16 kHz 16-bit input, which is AMI and therefore every published figure, the round trip moves
    /// 0.25% of samples by one LSB and the DER by nothing, which is why it went unseen. A float file
    /// is twice the size and costs the reference nothing to read: soundfile hands back the bytes.
    /// </para>
    /// </remarks>
    /// <summary>
    /// <paramref name="progress"/> is reported against the recording's own length, under
    /// <see cref="TranscriptionStage.LabellingSpeakers"/> and named as the reading half.
    /// </summary>
    /// <remarks>
    /// This half used to report nothing. On a three-hour file it is minutes of decoding and
    /// resampling before the sidecar is sent anything, and the caller's row kept whatever the
    /// transcription pass had left on it, so a working labeller was indistinguishable from a stuck
    /// one. The two halves report separately rather than as one number because there is no measured
    /// ratio between them to combine them with, and inventing one would be a bar that lies about
    /// how far along it is instead of a bar that says nothing.
    /// </remarks>
    /// <summary>
    /// Blocks in flight between the decoder and the resampler. Eight, which at the reader's 16,384
    /// samples a block is half a megabyte — enough that neither side waits on a jitter in the other,
    /// small enough that the bound is not worth thinking about beside the 577 MB the output holds.
    /// </summary>
    private const int StagingQueueDepth = 8;

    /// <summary>One decoded block on its way to the resampler, and the pooled array holding it.</summary>
    /// <remarks>
    /// The array is rented rather than allocated because there are 25,433 of them on a
    /// two-and-a-half-hour file, and it is copied rather than passed because the reader hands out
    /// <em>the same array every time</em> — <c>MediaFoundationAudioSource</c> fills one buffer and
    /// yields a window onto it, which is correct for a consumer that finishes before it asks for
    /// the next one and fatal for one that does not. The copy is 1.6 GB of memcpy across the whole
    /// file, a fraction of a second, against the thirteen it buys back.
    /// </remarks>
    private readonly record struct StagedBlock(float[] Buffer, int Length);

    internal static async Task<string> WriteResampledWavAsync(
        IAudioSource audio,
        CancellationToken ct,
        IProgress<TranscriptionProgress>? progress = null)
    {
        var resampler = new Resampler(audio.SampleRate, TargetSampleRate);

        // Sized from the duration when there is one, so the list does not double its way up to the
        // file's length and end holding a buffer half again as large as the audio. The writer below
        // streams the bytes, so this is the one whole-file copy the staging holds — the "690 MB for
        // three hours" figure is this buffer and not, as it was until 2026-08-22, this buffer and
        // a second one of its bytes.
        var expected = audio.Duration is { } duration
            ? (int)Math.Min(int.MaxValue - 64, (duration.TotalSeconds * TargetSampleRate) + 1024)
            : 0;
        var samples = new List<float>(expected);

        // ── Decoding and resampling run at the same time, and that is the whole of this change ──
        //
        // Measured 2026-08-23 on the desktop over the 157-minute podcast: decoding it takes 19.76 s
        // and resampling it 13.09 s, and run one after the other inside a single loop — a block
        // read, then that block resampled, then the next block read — the stage took 32.85 s of one
        // core. Neither half waits on the other's hardware and neither needs the other's result, so
        // the second is simply the first's cost paid twice over. Overlapped, the stage is whichever
        // half is slower rather than their sum, and on this machine that is the decode.
        //
        // A producer and a consumer over a bounded channel rather than a task per block: the
        // resampler is a filter carrying history across block boundaries, so its blocks have to
        // arrive in order and be processed by one thread. What is parallelised here is the *pair of
        // stages*, not the work inside either.
        var blocks = System.Threading.Channels.Channel.CreateBounded<StagedBlock>(
            new System.Threading.Channels.BoundedChannelOptions(StagingQueueDepth)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });

        // Cancelled by the resampling side if it throws, so that a producer parked on a full queue
        // is released rather than waiting for a consumer that is never coming back. Without it the
        // failure mode of a fault in the resampler is a hang, which is worse than the fault.
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var resampling = Task.Run(
            async () =>
            {
                // One report per whole percent, like the sidecar's own: the reader hands over a
                // block every few milliseconds, and a report each time is thousands of cross-thread
                // posts saying what the last one said. Reported from this side rather than the
                // reading side because it is this side that knows how much audio exists so far —
                // and `samples` belongs to this thread alone until the task is awaited.
                var lastPercent = -1;

                try
                {
                    await foreach (var block in blocks.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        try
                        {
                            resampler.Process(block.Buffer.AsSpan(0, block.Length), samples);
                        }
                        finally
                        {
                            System.Buffers.ArrayPool<float>.Shared.Return(block.Buffer);
                        }

                        if (progress is null || audio.Duration is not { Ticks: > 0 } whole)
                        {
                            continue;
                        }

                        var read = TimeSpan.FromSeconds(samples.Count / (double)TargetSampleRate);
                        var percent = (int)(100 * Math.Clamp(read.Ticks / (double)whole.Ticks, 0d, 1d));
                        if (percent == lastPercent)
                        {
                            continue;
                        }

                        lastPercent = percent;
                        progress.Report(new TranscriptionProgress
                        {
                            Stage = TranscriptionStage.LabellingSpeakers,
                            Detail = "Labelling speakers — reading the audio again",
                            Processed = read,
                            Total = whole,
                        });
                    }
                }
                catch
                {
                    stopped.Cancel();
                    throw;
                }
            },
            ct);

        try
        {
            await foreach (var block in audio.ReadAsync(ct).ConfigureAwait(false))
            {
                var buffer = System.Buffers.ArrayPool<float>.Shared.Rent(block.Length);
                block.Span.CopyTo(buffer);
                await blocks.Writer.WriteAsync(new StagedBlock(buffer, block.Length), stopped.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The resampler failed and cancelled us. Its exception is the one worth reporting, and
            // awaiting the task below is what raises it; swallowing this one only avoids replacing
            // a real fault with the cancellation it caused.
        }
        finally
        {
            blocks.Writer.Complete();
        }

        // Both halves are joined here, which is also what makes `samples` safe to read below: every
        // write to it happened on the resampling task, and awaiting it is the fence.
        await resampling.ConfigureAwait(false);

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

    /// <summary>The age past which a staged file cannot belong to a live run and is swept.</summary>
    internal static readonly TimeSpan StaleStagedFileAge = TimeSpan.FromHours(1);

    private static int _staleSweepDone;

    /// <summary>
    /// Deletes staged WAVs left behind by a run that never reached its <c>finally</c> — a host
    /// killed mid-label — once per process. Only files older than <see cref="StaleStagedFileAge"/>
    /// go: a concurrent instance may own a younger one, and a staged file is written and read
    /// within minutes.
    /// </summary>
    internal static int SweepStaleStagedFiles(TimeSpan? olderThan = null, string? directory = null)
    {
        if (directory is null && Interlocked.Exchange(ref _staleSweepDone, 1) == 1)
        {
            return 0;
        }

        var age = olderThan ?? StaleStagedFileAge;
        var cutoff = DateTime.UtcNow - age;
        var swept = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory ?? Path.GetTempPath(), "uindosill-diarise-*.wav"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                        swept++;
                    }
                }
                catch (IOException)
                {
                    // In use, or gone already: either way not this run's to remove.
                }
                catch (UnauthorizedAccessException)
                {
                    // As above.
                }
            }
        }
        catch (IOException)
        {
            // The temp directory itself is unreadable; nothing to sweep.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }

        return swept;
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
    /// <summary>
    /// Holds the sidecar's reported limits against the ones this build has been warning from.
    /// </summary>
    /// <remarks>
    /// <b>One set of limits, since there is one diariser.</b> This took a kind and chose between
    /// two sets while Sortformer was loadable, because comparing a pyannote load against its
    /// four-speaker cap would have failed every load of a model whose whole point is not having
    /// one. Both nulls below are claims, and the sidecar is held to them the same way a number
    /// would be.
    /// </remarks>
    private static void CheckDeclaredLimits(SpeakerLabellerCapabilities reported)
    {
        var declared = DeclaredLimits;

        // Name is not compared: it is the catalogue's id where there is one, and the two sides get
        // it from different places by design. The numbers are the claim.
        if (reported.MaxSpeakers != declared.MaxSpeakers
            || reported.ReliableUpTo != declared.ReliableUpTo
            || reported.SupportsFixedSpeakerCount != declared.SupportsFixedSpeakerCount)
        {
            throw new PythonSidecarException(
                $"The bundled Python's diariser reports a cap of {Describe(reported.MaxSpeakers)} speakers and " +
                $"labels established to {Describe(reported.ReliableUpTo)}, and this build has been saying " +
                $"{DescribeAlone(declared.MaxSpeakers)} and {Describe(declared.ReliableUpTo)}. Those " +
                "numbers are what the warnings before a run are written from, so the two halves are out of step — " +
                "reinstall rather than mixing them.");
        }
    }

    // Two call sites and two grammars: one is followed by the word "speakers" and the other is not,
    // which only showed once both declared limits became null and the sentence read "has been saying
    // no limit on the and no measured length".
    private static string Describe(int? value) => value?.ToString() ?? "no limit on the";

    private static string DescribeAlone(int? value) => value?.ToString() ?? "no cap";

    private static string Describe(TimeSpan? value) =>
        value is { } bound ? $"{bound.TotalMinutes:F0} minutes" : "no measured length";

    private static SpeakerLabellerCapabilities ReadCapabilities(JsonElement element) => new()
    {
        EngineName = element.TryGetProperty("engineName", out var name)
            ? name.GetString() ?? "pyannote-torch-python"
            : "pyannote-torch-python",
        ModelId = element.TryGetProperty("modelId", out var id) ? id.GetString() : null,
        Backend = element.TryGetProperty("backend", out var backend)
            ? ExecutionProviders.Parse(backend.GetString())
            : ComputeBackend.Cpu,
        EmbeddingBackend = element.TryGetProperty("embeddingBackend", out var embedding)
                           && embedding.ValueKind == JsonValueKind.String
            ? embedding.GetString() ?? ""
            : "",
        BatchSize = element.TryGetProperty("batchSize", out var batch) && batch.ValueKind == JsonValueKind.Number
            ? batch.GetInt32()
            : null,
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
