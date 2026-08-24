using Parakeet.Core.Transcription;

namespace Parakeet.Engine.LlamaServer;

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
    /// with room to spare; the whole-transcript path needs 53,248 — the measured three-hour
    /// transcript is 51,712 tokens under the working candidate's template
    /// (docs/V2-ASK-THE-TRANSCRIPT.md, decision 2's correction block) — and pays for it in KV.
    /// </summary>
    public int ContextSize { get; init; } = 16_384;

    /// <summary>Layers to offload; the default asks for all of them.</summary>
    public int GpuLayers { get; init; } = 999;

    /// <summary>`-fa on|off|auto`, or null for the server's own default. #26609 is why this
    /// exists: CUDA plus expert offload plus flash attention is a live crash until it closes.</summary>
    public string? FlashAttention { get; init; }

    /// <summary>
    /// Extra environment for the child. On Vulkan, <c>GGML_VK_DISABLE_BFLOAT16=1</c> is set by
    /// default unless this dictionary overrides it: the laptop's driver hangs at load without it
    /// (docs/UNPROVEN.md, *Upstream llama.cpp on the second machine*), a hang is strictly worse
    /// than the bf16 path being unavailable, and the child's environment costing one line is why
    /// the child-process design was chosen over in-process knobs.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>How long a load may take before it is a failure. A ~9 GB file is tens of seconds
    /// from a cold disk.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Cap on generated tokens per answer.</summary>
    public int MaxAnswerTokens { get; init; } = 1_024;

    /// <summary>
    /// Constrain decoding to the citation grammar. On by default: measured on a 0.6B model, the
    /// grammar — not `--reasoning-budget 0` — is what kept reasoning out of the answer channel
    /// and made the output terminate (docs/UNPROVEN.md).
    /// </summary>
    public bool UseGrammar { get; init; } = true;

    /// <summary>
    /// Admit the <c>NOT_IN_TRANSCRIPT</c> production. A measured dial, not a formality: with it
    /// a small model abstained on everything, without it it invented citations on the
    /// adversarial question. Per-model measurement decides; the honest default admits it.
    /// </summary>
    public bool AllowAbstain { get; init; } = true;

    /// <summary>Require a verbatim «quote» per bullet, for the validator's substring check.</summary>
    public bool RequireQuote { get; init; } = true;
}
