namespace Parakeet.Core.Transcription;

/// <summary>What a loaded engine can actually do, so the UI can disable rather than fail.</summary>
public sealed record EngineCapabilities
{
    public required string EngineName { get; init; }

    /// <summary>Identifier of the loaded model, or null when nothing is loaded yet.</summary>
    public string? ModelId { get; init; }

    /// <summary>Which compute backend the loaded native library is actually using.</summary>
    public ComputeBackend Backend { get; init; } = ComputeBackend.Cpu;

    /// <summary>Native ABI version reported by the engine, when it has one.</summary>
    public int? NativeAbiVersion { get; init; }

    public bool SupportsWordTimestamps { get; init; }

    public bool SupportsBatchDecode { get; init; }

    /// <summary>True when the engine accepts a target language / locale hint.</summary>
    public bool SupportsLanguageSelection { get; init; }

    /// <summary>
    /// True when <see cref="TranscriptionOptions.ThreadCount"/> actually reaches the decoder.
    /// </summary>
    /// <remarks>
    /// False is a real answer, not a placeholder. The parakeet.cpp C ABI (v6) takes no thread
    /// count on any entry point and its C++ surface exposes none either, so the decode runs on
    /// whatever ggml chooses. A UI that offers a thread slider which changes nothing is worse
    /// than one that has no slider, so callers must check this before showing the control.
    /// </remarks>
    public bool SupportsThreadCount { get; init; }

    /// <summary>
    /// True when a decode in flight can genuinely be stopped. False means cancellation can
    /// only stop scheduling further work; the current segment runs to completion and its
    /// result is discarded.
    /// </summary>
    public bool SupportsDecodeCancellation { get; init; }

    /// <summary>
    /// Longest audio the engine should be handed in one call. Parakeet degrades past
    /// roughly 24 minutes single-pass and glues text across chunk boundaries well before
    /// that, so callers segment instead of trusting a long single pass.
    /// </summary>
    public TimeSpan MaxSingleDecodeLength { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Languages the model claims to handle, empty when unknown or unconstrained.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];
}

/// <summary>Which native compute path is in use.</summary>
public enum ComputeBackend
{
    /// <summary>Portable CPU build. Always available, always the fallback.</summary>
    Cpu = 0,

    /// <summary>ggml-Vulkan: NVIDIA, AMD and Intel with only a normal graphics driver.</summary>
    Vulkan = 1,

    /// <summary>ggml-CUDA. Ships its own cudart, so no CUDA Toolkit on the user's machine.</summary>
    Cuda = 2,
}
