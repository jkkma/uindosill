using System.Runtime.CompilerServices;
using System.Text.Json;
using Parakeet.Core.Transcription;
using Parakeet.Core.Translation;

namespace Parakeet.Engine.Python;

/// <summary>How the sidecar's translator is loaded and driven.</summary>
public sealed record SidecarTranslatorOptions
{
    /// <summary>The exported checkpoint directory: two graphs, two configs, five tokenizer files.</summary>
    public required string ModelDirectory { get; init; }

    /// <summary>Catalogue id, carried into the transcript's provenance beside the ASR model's.</summary>
    public string? ModelId { get; init; }

    /// <summary>Intra-op threads for the ONNX sessions, or 0 to let ONNX Runtime choose.</summary>
    public int IntraOpThreads { get; init; }

    /// <summary>
    /// Execution provider: <c>auto</c>, <c>cpu</c>, <c>cuda</c>, <c>webgpu</c> or <c>dml</c>.
    /// </summary>
    /// <remarks>
    /// <b>This changes the English, not only the speed.</b> Measured 2026-08-21 on 32 FLEURS
    /// es_419 sentences at beam 6: WebGPU returned the CPU's own translations on 32 of 32 at 1.30×
    /// the speed, CUDA on 240 of 240, and DirectML on <b>0</b> of 32 — its decoder falls into a
    /// repetition loop — while running 21.5× slower. <c>auto</c> is resolved inside the sidecar,
    /// because the only thing that knows whether a provider will initialise is the ONNX Runtime
    /// that would have to initialise it.
    /// </remarks>
    public string Provider { get; init; } = "auto";

    /// <summary>ONNX Runtime graph optimisation level, or null for the provider's default.</summary>
    public string? GraphOptimization { get; init; }

    /// <summary>
    /// BCP-47 tags the catalogue says this checkpoint reads. Empty when unconstrained or unknown.
    /// </summary>
    /// <remarks>
    /// Passed in rather than read from the model, because it is a claim about a list and claims
    /// belong in the catalogue where a release engineer can review one. The checkpoint's own card
    /// disclaims its coverage list in as many words.
    /// </remarks>
    public IReadOnlyList<string> SourceLanguages { get; init; } = [];
}

/// <summary>
/// Whether this machine's stack reproduces the translator the published figures describe.
/// </summary>
/// <remarks>
/// <para>
/// A count and the sentences behind it, not a boolean. "Parity failed" tells a user nothing they
/// can judge; the sentence that came back instead tells them at a glance whether they are looking
/// at a word of difference or at a decoder repeating one phrase for 512 tokens.
/// </para>
/// <para>
/// <b>It is a weaker instrument than the diariser's and says so.</b> That one compares
/// probabilities and has three orders of magnitude between a faithful provider and a diverging one.
/// A translation is a string, so this is identical-or-not per sentence with no margin: a provider
/// that goes wrong only on long or unusual inputs passes six short ones. What it catches is the
/// failure that has actually been seen — DirectML's repetition-loop collapse, wrong on all 32
/// sentences measured. What establishes a translator on a machine is the gate corpus, and nothing
/// smaller.
/// </para>
/// </remarks>
public sealed record TranslationParityResult
{
    public required bool Passed { get; init; }

    /// <summary>
    /// False when the check itself could not be run — the sidecar answered the <c>parity</c>
    /// request with an error rather than a verdict; <see cref="Reason"/> carries it, and the
    /// English is unverified rather than known wrong. <see cref="ParityResult.Ran"/> on the
    /// diariser's result says why this is a state of its own and not the null of "not run".
    /// </summary>
    public bool Ran { get; init; } = true;

    /// <summary>
    /// The sidecar's reason when the verdict is not a count — a fixture of the wrong length — or,
    /// when <see cref="Ran"/> is false, the error the check came back with.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>How many of the fixture's sentences came back exactly as the reference has them.</summary>
    public int Identical { get; init; }

    public int Total { get; init; }

    /// <summary>Up to three of the disagreements, each as expected-against-actual.</summary>
    public IReadOnlyList<string> Differing { get; init; } = [];

    /// <summary>
    /// The first sentence a run prints about this result, or null when the check passed. The
    /// callers add what the English is and what to do about it, because those differ between a
    /// command line with a flag to recommend and a window without one.
    /// </summary>
    public string? Describe()
    {
        if (!Ran)
        {
            return "WARNING: the check that compares this machine's translator against the reference could not be " +
                   $"run: {Reason ?? "the sidecar gave no reason"}.";
        }

        if (Passed)
        {
            return null;
        }

        if (Reason is { Length: > 0 } reason)
        {
            return $"WARNING: this machine's translator does not reproduce the reference: {reason}.";
        }

        var examples = Differing.Count > 0 ? " " + string.Join(" ", Differing) : string.Empty;
        return $"WARNING: this machine's translator reproduced {Identical} of {Total} of the reference's " +
               $"translations.{examples}";
    }
}

/// <summary>
/// Translation into English by Marian, run in the bundled Python.
/// </summary>
/// <remarks>
/// <para>
/// The engine moved out of process on 2026-08-21, and what it buys is that the beam search and the
/// tokenizer are HuggingFace's own rather than this project's: <c>transformers.generate</c> over
/// <c>optimum</c>'s ONNX Runtime sessions, in place of ~2,760 lines of C# that reimplemented a
/// SentencePiece processor, a Marian tokenizer, a beam search and a decoder loop. Every one of
/// those was a second place for a decode to drift from the one that was scored.
/// </para>
/// <para>
/// <b>The policy did not move with it.</b> The <c>&gt;&gt;eng&lt;&lt;</c> target token is applied
/// here by <see cref="TranslationRequest.Build"/>, the limit a source is refused against is
/// enforced here, and refusing the word-timed subtitle format is the caller's. The sidecar does the
/// two things only the model can do — count a string's tokens and translate it — and is told
/// nothing about what either means. That is the same division <see cref="SidecarSpeakerLabeller"/>
/// draws, kept deliberately, so that moving an engine across a process boundary does not also move
/// the decisions.
/// </para>
/// <para>
/// <b>One request per segment.</b> A decode is about half a second and a protocol line is
/// microseconds, so batching buys nothing and costs the streaming: each translated segment is
/// yielded as it arrives, which is what lets a long transcript render while it is still being
/// written.
/// </para>
/// </remarks>
public sealed class SidecarTranscriptTranslator : ITranscriptTranslator
{
    /// <summary>
    /// The token every source must carry. One vocabulary entry, id 693, not three punctuation marks
    /// and a word.
    /// </summary>
    /// <remarks>
    /// Declared on this side rather than reported by the sidecar because it is policy: measured
    /// 2026-08-19, the recommended checkpoint handed Spanish without it returned fluent German —
    /// its first declared target — rather than an error, so the marking belongs at the seam where
    /// forgetting it is not an option a translator has.
    /// </remarks>
    public const string EnglishTargetToken = ">>eng<<";

    /// <summary>
    /// What the tokenizer will say, used until it has said it.
    /// </summary>
    /// <remarks>
    /// The checkpoint's <c>tokenizer_config.json</c> declares <c>model_max_length</c> 512 and the
    /// sidecar reads it at load. This is what that read will return, carried so that a caller
    /// asking before the weights are in can be told a number rather than nothing — the same thing
    /// the in-process translator did, and for the same reason.
    /// </remarks>
    private const int DeclaredMaxSourceTokens = 512;

    private readonly SidecarTranslatorOptions _options;
    private readonly PythonSidecar _sidecar;
    private readonly bool _ownsSidecar;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private bool _loaded;
    private bool _disposed;

    public SidecarTranscriptTranslator(SidecarTranslatorOptions options, PythonSidecar? sidecar = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelDirectory);

        _options = options;
        _ownsSidecar = sidecar is null;
        _sidecar = sidecar ?? new PythonSidecar(PythonRuntime.Resolve());

        Capabilities = new TranslatorCapabilities
        {
            EngineName = "marian-onnx-python",
            ModelId = options.ModelId,

            // The provider that was *asked for*, which is not always the one that will run: "auto"
            // is resolved inside the sidecar and lands here at load. Reported rather than left at a
            // flat Cpu because for every value but auto it is already known and already true, and
            // nothing writes a translator backend into a transcript's provenance before the load
            // that corrects it — see LoadAsync.
            Backend = ExecutionProviders.Parse(options.Provider),
            TargetToken = EnglishTargetToken,

            // Many-to-one: it is told the target and never the source, which is what makes it
            // drivable by a pipeline that cannot detect what it just transcribed.
            RequiresSourceLanguage = false,
            PreservesWordTimings = false,

            // False, and the change from the in-process translator that this is the one to notice.
            // The decode happens in another process with no way in: cancelling stops the host
            // sending the next segment, and the one already being decoded runs to completion —
            // which is exactly what this flag being false means. Half a second, at beam 6 on the
            // measured checkpoint, and there is no partial result to salvage in any case: a
            // truncated translation is the failure the whole contract exists to avoid.
            SupportsCancellation = false,
            HonoursContext = false,

            MaxSourceTokens = DeclaredMaxSourceTokens,
            SourceLanguages = options.SourceLanguages,
            TargetLanguages = [TranslationTarget.LanguageTag],
        };
    }

    public TranslatorCapabilities Capabilities { get; private set; }

    /// <summary>
    /// The decode the sidecar reported using, or null before load.
    /// </summary>
    /// <remarks>
    /// Carried because the graphs are pinned and the search over them is not: six beams rather than
    /// the config file's four, 512 new tokens, length penalty 1.0, early stopping off. Every one of
    /// those changes the English, and a run that cannot say which search produced it is one nobody
    /// can reproduce. The values live in the sidecar, which is what performs the search; this is
    /// how they reach a report.
    /// </remarks>
    public string? DecodeDescription { get; private set; }

    public async ValueTask LoadAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            // Loaded — but the child may have died since, on the labeller's terms: said here, at
            // once, rather than by the first segment's write. Not restarted.
            _sidecar.ThrowIfFaulted();
            return;
        }

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                _sidecar.ThrowIfFaulted();
                return;
            }

            await _sidecar.StartAsync(ct).ConfigureAwait(false);

            var reply = await _sidecar.SendAsync("load", writer =>
            {
                writer.WriteString("engine", "translator");
                writer.WriteString("path", _options.ModelDirectory);
                writer.WriteString("modelId", _options.ModelId ?? string.Empty);
                writer.WriteNumber("threads", _options.IntraOpThreads);
                writer.WriteString("provider", _options.Provider);
                if (_options.GraphOptimization is { Length: > 0 } level)
                {
                    writer.WriteString("graphOptimization", level);
                }
            }, null, ct).ConfigureAwait(false);

            Apply(reply.GetProperty("capabilities"));

            // Every backend but the CPU is checked against the committed reference before it is
            // used, on the diariser's terms and for the same reason: `auto` selects WebGPU, WebGPU
            // was measured faithful on one card with one driver, and DirectML's diarisation defect
            // turned out to be driver-mediated — so "faithful where it was measured" does not
            // transfer, and a wrong translator produces English rather than an error. Six short
            // sentences, about two seconds, once per load.
            if (Capabilities.Backend != ComputeBackend.Cpu)
            {
                Parity = await CheckParityAsync(ct).ConfigureAwait(false);
            }

            _loaded = true;
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
    public TranslationParityResult? Parity { get; private set; }

    /// <summary>
    /// The providers <c>auto</c> tried and passed over before the one that loaded, each with the
    /// reason it did not build. Empty when the first candidate built, or when the provider was
    /// named — a named provider is never fallen back from.
    /// </summary>
    public IReadOnlyList<string> FellBackFrom { get; private set; } = [];

    private async Task<TranslationParityResult?> CheckParityAsync(CancellationToken ct)
    {
        JsonElement reply;
        try
        {
            reply = await _sidecar.SendAsync(
                "parity", writer => writer.WriteString("engine", "translator"), null, ct).ConfigureAwait(false);
        }
        catch (PythonEngineException exception)
        {
            // The check crashed, which is neither the CPU's "not run" nor a fixture that is missing
            // — the sidecar reports that structurally, below. Until 2026-08-22 all three were the
            // same null, and English went out unverified with nothing said; this is now a result
            // that says the check did not run and why.
            return new TranslationParityResult { Passed = false, Ran = false, Reason = exception.Message };
        }

        if (!reply.TryGetProperty("available", out var available) || available.ValueKind != JsonValueKind.True)
        {
            // No fixture committed: nothing was compared and nothing failed, and the null says so.
            return null;
        }

        var differing = new List<string>();
        if (reply.TryGetProperty("differing", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var expected = row.TryGetProperty("expected", out var e) ? e.GetString() : null;
                var actual = row.TryGetProperty("actual", out var a) ? a.GetString() : null;
                differing.Add($"expected \"{expected}\", got \"{actual}\"");
            }
        }

        return new TranslationParityResult
        {
            Passed = reply.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True,
            Reason = reply.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null,
            Identical = reply.TryGetProperty("identical", out var same) && same.ValueKind == JsonValueKind.Number
                ? same.GetInt32()
                : 0,
            Total = reply.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Number
                ? total.GetInt32()
                : 0,
            Differing = differing,
        };
    }

    /// <summary>Takes the sidecar's word for what loaded, over this side's expectation of it.</summary>
    private void Apply(JsonElement capabilities)
    {
        Capabilities = Capabilities with
        {
            EngineName = capabilities.TryGetProperty("engineName", out var name)
                ? name.GetString() ?? Capabilities.EngineName
                : Capabilities.EngineName,

            ModelId = capabilities.TryGetProperty("modelId", out var id) && id.GetString() is { Length: > 0 } reported
                ? reported
                : Capabilities.ModelId,

            Backend = capabilities.TryGetProperty("backend", out var backend)
                ? ExecutionProviders.Parse(backend.GetString())
                : Capabilities.Backend,

            // The tokenizer's own declared limit rather than this side's guess at it. They agree on
            // this checkpoint; a checkpoint where they did not is one to hear about, which is why
            // the sidecar's answer wins rather than being checked against a constant and dropped.
            MaxSourceTokens = capabilities.TryGetProperty("maxSourceTokens", out var max)
                              && max.ValueKind == JsonValueKind.Number
                ? max.GetInt32()
                : Capabilities.MaxSourceTokens,
        };

        DecodeDescription = Describe(capabilities);
        FellBackFrom = ExecutionProviders.ReadFellBackFrom(capabilities);
    }

    private static string? Describe(JsonElement capabilities)
    {
        if (!capabilities.TryGetProperty("beams", out var beams) || beams.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var maxNew = capabilities.TryGetProperty("maxNewTokens", out var m) && m.ValueKind == JsonValueKind.Number
            ? m.GetInt32()
            : 0;
        var penalty = capabilities.TryGetProperty("lengthPenalty", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetDouble()
            : 1d;
        var early = capabilities.TryGetProperty("earlyStopping", out var e) && e.ValueKind == JsonValueKind.True;

        return $"beam {beams.GetInt32()}, at most {maxNew} new tokens, length penalty {penalty:0.##}, " +
               $"early stopping {(early ? "on" : "off")}";
    }

    public async IAsyncEnumerable<TranscriptSegment> TranslateAsync(
        IReadOnlyList<TranscriptSegment> segments,
        TranslationOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        await LoadAsync(ct).ConfigureAwait(false);

        // Built here rather than inline so that no translator can forget the target token: a source
        // without it comes back as fluent German rather than as an error.
        var requests = TranslationRequest.Build(segments, options, Capabilities.TargetToken);
        var total = segments.Count > 0 ? segments[^1].End : TimeSpan.Zero;

        for (var i = 0; i < requests.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var segment = segments[i];
            var request = requests[i];

            string text;
            if (segment.Text.Trim().Length == 0)
            {
                // Nothing to translate. Yielded empty rather than dropped, and rather than handed
                // to the model, which given a bare target token would confidently write a sentence
                // nobody said.
                text = string.Empty;
            }
            else
            {
                text = await TranslateOneAsync(request, ct).ConfigureAwait(false);
            }

            yield return segment with
            {
                Text = text,

                // Not the source's words under new text. Every clause of the contract that costs
                // something costs it here.
                Words = [],
            };

            progress?.Report(new TranscriptionProgress
            {
                Stage = TranscriptionStage.Translating,
                Processed = segment.End,
                Total = total,
                SegmentsCompleted = i + 1,
                SegmentsTotal = segments.Count,
            });
        }
    }

    /// <summary>
    /// Translates one marked source, or refuses it for being longer than the model will read.
    /// </summary>
    /// <remarks>
    /// The limit travels with the request so that a source about to be refused is not decoded
    /// first — half a second saved, and no chance of the sidecar's own truncation happening before
    /// this side gets to object. The decision is still this side's: the count comes back either
    /// way and the refusal is thrown from here.
    /// </remarks>
    private async Task<string> TranslateOneAsync(TranslationRequest request, CancellationToken ct)
    {
        var limit = Capabilities.MaxSourceTokens;

        var reply = await _sidecar.SendAsync("translate", writer =>
        {
            writer.WriteString("source", request.Source.Trim());
            if (limit is { } cap)
            {
                writer.WriteNumber("maxTokens", cap);
            }
        }, null, ct).ConfigureAwait(false);

        var tokens = reply.TryGetProperty("tokens", out var counted) && counted.ValueKind == JsonValueKind.Number
            ? counted.GetInt32()
            : 0;

        if (limit is { } bound && tokens > bound)
        {
            throw new SegmentTooLongException(request.SegmentIndex, tokens, bound);
        }

        if (!reply.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
        {
            throw new PythonSidecarException(
                $"The translator returned no text for segment {request.SegmentIndex}, and no limit it was over. " +
                "A segment that comes back with neither is one this side cannot account for, so it is not passed " +
                "off as an empty translation.");
        }

        return text.GetString() ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadGate.Dispose();

        if (_ownsSidecar)
        {
            await _sidecar.DisposeAsync().ConfigureAwait(false);
        }
    }
}
