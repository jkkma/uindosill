using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer;

/// <summary>
/// Where a mixture-of-experts model's expert weights are placed on the Vulkan backend.
/// </summary>
/// <remarks>
/// A dial with one measured end and one unmeasured one, which is why it is a setting rather than
/// a constant. <see cref="SystemMemory"/> is what the second machine needs — a UMA driver splits
/// its memory into two ~7.8 GiB heaps and a 26B-class mixture cannot load at all without it
/// (measured 2026-08-24, docs/UNPROVEN.md). <see cref="Device"/> is what a card with memory of
/// its own should want, and no machine here has measured it. On a dense model the override
/// matches no tensors and the choice costs nothing either way.
/// </remarks>
public enum MoeExpertPlacement
{
    /// <summary>From <see cref="VulkanDeviceProbe.Classify"/>: on a card, in system memory
    /// otherwise. What ships.</summary>
    Automatic = 0,

    /// <summary>On the GPU, whatever the loader reports.</summary>
    Device = 1,

    /// <summary>In system memory, whatever the loader reports.</summary>
    SystemMemory = 2,
}

/// <summary>How the child is started. Everything here becomes an argument or an environment.</summary>
public sealed record LlamaServerOptions
{
    /// <summary>The GGUF to load. The engine does not download models; the catalogue does.</summary>
    public required string ModelPath { get; init; }

    /// <summary>Which vendored drop to run, or null to take the best present per
    /// <see cref="LlamaServerLocator.ProbeOrder"/>.</summary>
    public ComputeBackend? Backend { get; init; }

    /// <summary>
    /// Where the server drops live, when not under the application's own directory — the lab
    /// scripts and the gated tests point this at a checkout's <c>native/win-x64/llm</c>.
    /// </summary>
    public string? ServerRoot { get; init; }

    /// <summary>
    /// The context window to allocate, in tokens. The default covers retrieval-mode evidence
    /// with room to spare; the whole-transcript path pays for what it reads in KV — the measured
    /// three-hour transcript is 51,712 tokens under the working candidate's template
    /// (docs/V2-ASK-THE-TRANSCRIPT.md, decision 2's correction block) — so the application sizes
    /// this to the open recording rather than to the largest transcript anyone might open.
    /// </summary>
    public int ContextSize { get; init; } = 16_384;

    /// <summary>Layers to offload; the default asks for all of them.</summary>
    public int GpuLayers { get; init; } = 999;

    /// <summary>
    /// The logical and physical prefill batches — <c>-b</c> and <c>-ub</c>. Larger than the
    /// server's own 2048 and 512 because prefill is where an answer's time goes.
    /// </summary>
    /// <remarks>
    /// <b>Measured 2026-08-27/28 on the second machine</b>, the 26B-A4B at UD-IQ4_XS with its
    /// drafting head. On retrieval-shaped questions the physical batch at 1,024 took prefill
    /// from ~89 to 112 tok/s and the median answer from 39.8 s to 28.0 s — more than cutting
    /// the evidence from eight windows to six bought, and unlike the evidence it changes no
    /// token the model sees. On the survey tier's larger prompt, 2,048 took the first answer
    /// about a three-hour recording from 135.9 s to 120.8 s.
    /// </remarks>
    /// <remarks>
    /// <b>It is not a memory setting on the evidence available.</b> Free system RAM after
    /// load was 0.24 GiB at 1,024 and 0.27 GiB at 2,048 with the same model — the server's
    /// footprint dominates and the batch is lost in it. That is one machine and one model;
    /// a machine that cannot afford the compute buffer sets these lower.
    /// </remarks>
    public int BatchSize { get; init; } = 4_096;

    /// <inheritdoc cref="BatchSize"/>
    public int PhysicalBatchSize { get; init; } = 2_048;

    /// <summary>`-fa on|off|auto`, or null for the server's own default. #26609 is why this
    /// exists: CUDA plus expert offload plus flash attention is a live crash until it closes.</summary>
    public string? FlashAttention { get; init; }

    /// <summary>
    /// A multi-token-prediction head to draft with, or null for ordinary one-token-at-a-time
    /// decoding. The file ships beside the weights it belongs to — `mtp-&lt;model&gt;.gguf` in the
    /// models folder — and <see cref="DraftModelLocator"/> is what pairs the two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured 2026-08-27 on the second machine</b>, the 26B-A4B at UD-IQ4_XS over thirteen
    /// retrieval questions: decode 7.66 -&gt; 10.11 tok/s and median wall 42.3 -&gt; 37.8 s, at
    /// <b>71.7% draft acceptance</b>, with every citation resolving either way and three of three
    /// adversarial questions abstained from — and not with the same answer: the head arm verified
    /// 16 quotes against 17 and cited 47 spans against 52, in a session whose own noise floor says
    /// a changed quote count is a real change in what the model wrote. Greedy decoding is not
    /// byte-identical under drafting or slot batching (docs/GOTCHAS.md, 41); what a head is
    /// measured to keep is the checks, never the bytes. Unsloth claims 1.4–2.2x for the same head;
    /// 1.32x is what this laptop gets, where decode is bounded by reading the experts out of system
    /// memory rather than by arithmetic.
    /// </para>
    /// <para>
    /// <b>Prompt-lookup drafting was measured and rejected in the same session</b>, which is why
    /// this is a model path and not a `--spec-type` string: `ngram-simple`, `ngram-map-k` and
    /// `ngram-mod` accepted 11.5%, 15.3% and 3.0% of their drafts and bought nothing
    /// (7.32, 7.87, 7.78 tok/s against 7.66 for no drafting at all). An answer that cites a
    /// transcript is mostly the model's own prose around a short quote, so there is little
    /// verbatim span for a lookup to find — the trained head predicts the model instead.
    /// </para>
    /// <para>
    /// <b>It costs memory.</b> The head is ~0.5 GB resident, and on the second machine it took
    /// free system RAM from ~1.4 to ~0.89 GiB with the 26B loaded — still the fastest arm
    /// measured there, but the margin above the paging cliff is what it spends.
    /// </para>
    /// </remarks>
    public string? DraftModelPath { get; init; }

    /// <summary>
    /// Extra environment for the child. On Vulkan, <c>GGML_VK_DISABLE_BFLOAT16=1</c> is set by
    /// default unless this dictionary overrides it: the laptop's driver hangs at load without it
    /// (docs/UNPROVEN.md, *Upstream llama.cpp on the second machine*), a hang is strictly worse
    /// than the bf16 path being unavailable, and the child's environment costing one line is why
    /// the child-process design was chosen over in-process knobs.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Where a mixture's experts go on Vulkan. <see cref="MoeExpertPlacement.Automatic"/> asks
    /// the loader; <see cref="Environment"/> still overrides whatever this resolves to, because
    /// an explicit environment is the caller saying they know better than both.
    /// </summary>
    public MoeExpertPlacement ExpertPlacement { get; init; } = MoeExpertPlacement.Automatic;

    /// <summary>How long a load may take before it is a failure. A ~9 GB file is tens of seconds
    /// from a cold disk.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Cap on generated tokens per answer.</summary>
    public int MaxAnswerTokens { get; init; } = 1_024;

    /// <summary>
    /// Let the model think before answering, and — since 2026-08-25 — actually decide it: the
    /// child is started with <c>--reasoning on|off</c> from this flag. On, the child's reasoning
    /// parser keeps the thinking in <c>reasoning_content</c> where the answer stream never sees
    /// it, and the engine sends no grammar: an eager grammar was measured shaping the think
    /// block itself and filing the whole answer as reasoning (2026-08-16, re-measured
    /// 2026-08-24), so in this mode citation trust is the parser's and validator's, post-hoc.
    /// </summary>
    /// <remarks>
    /// Off used to mean only "file the thinking elsewhere", which is not the same thing and was
    /// a shipped defect: <c>--reasoning</c> defaults to <c>auto</c>, so a thinking model's
    /// template thought anyway, the default parse filed it under <c>reasoning_content</c>, the
    /// engine dropped it, and <see cref="MaxAnswerTokens"/> could be spent before one content
    /// token existed — the 26B-A4B answered a twelve-segment overview with nothing at all in
    /// 79.4 s, and with the same prompt under <c>off</c> produced a lead and four cited bullets
    /// in 45.5 s (docs/UNPROVEN.md).
    /// </remarks>
    /// <remarks>
    /// Off by default — the maintainer's decision, 2026-08-24, taken on the measured cost:
    /// thinking runs at decode speed, and on the second machine the 26B-A4B spent ~6 minutes
    /// thinking before a two-bullet answer (415.6 s wall) whose grammar-mode sibling was
    /// ~20–40 s — with the grammar-mode answer measured no worse. The dial exists for the
    /// desktop tier, where decode is an order of magnitude faster.
    /// </remarks>
    public bool ThinkBeforeAnswer { get; init; }

    /// <summary>
    /// Extra generation budget for the thinking, on top of <see cref="MaxAnswerTokens"/>, when
    /// <see cref="ThinkBeforeAnswer"/> is on. The 2,048 default is a dial set from one measured
    /// point — the 9B closed a toy question's think block at ~550 tokens (2026-08-16) — not a
    /// measured optimum.
    /// </summary>
    public int ThinkingBudgetTokens { get; init; } = 2_048;

    /// <summary>
    /// Constrain decoding to the citation grammar, applied only when
    /// <see cref="ThinkBeforeAnswer"/> is off. **Off by default — the maintainer's decision,
    /// 2026-08-25**, on the first real app sessions: under greedy decoding the grammar's
    /// free-text spans trap the model in repetition loops it cannot leave (docs/UNPROVEN.md),
    /// while the template-only configuration ran clean in every same-day measurement. Citation
    /// trust without the grammar is the parser's and validator's, post-hoc — an invented id
    /// renders unresolved instead of being unsamplable. The grammar remains the measured tool
    /// for models whose output does not terminate without it.
    /// </summary>
    public bool UseGrammar { get; init; }

    /// <summary>
    /// Admit the <c>NOT_IN_TRANSCRIPT</c> production. A measured dial, not a formality: with it
    /// a small model abstained on everything, without it it invented citations on the
    /// adversarial question. Per-model measurement decides; the honest default admits it.
    /// </summary>
    public bool AllowAbstain { get; init; } = true;

    /// <summary>Require a verbatim «quote» per bullet, for the validator's substring check.</summary>
    public bool RequireQuote { get; init; } = true;
}
