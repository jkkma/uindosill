namespace Parakeet.Core.Transcription;

/// <summary>What a loaded engine can actually do, so the UI can disable rather than fail.</summary>
public sealed record EngineCapabilities
{
    public required string EngineName { get; init; }

    /// <summary>Identifier of the loaded model, or null when nothing is loaded yet.</summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Quantisation of the loaded weights (<c>f16</c>, <c>q8_0</c>, …), when known.
    /// </summary>
    /// <remarks>
    /// Carried through to the finished transcript. Quantisation quality on this engine is
    /// measured on one corpus only (docs/UNPROVEN.md) and the analogous ONNX INT8 export collapsed
    /// silently, so a transcript that cannot say which weights produced it is not a result anybody
    /// can re-examine later.
    /// </remarks>
    public string? Quantisation { get; init; }

    /// <summary>
    /// Which compute backend the loaded native library is actually using — or null when that is
    /// not known: a library found in a flat directory or on the system search path has no backend
    /// in its path, and recording the one that was requested would put a guess into the
    /// transcript's provenance, which it did until 2026-08-22.
    /// </summary>
    public ComputeBackend? Backend { get; init; } = ComputeBackend.Cpu;

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

    /// <summary>
    /// ONNX Runtime's DirectML provider: any Direct3D 12 GPU, so AMD and Intel as well as NVIDIA.
    /// </summary>
    /// <remarks>
    /// The odd one out here, and deliberately its own member rather than folded into
    /// <see cref="Vulkan"/>. The other three are ggml backends and describe the ASR engine; this one
    /// belongs to the ONNX components and never runs parakeet.cpp. Reporting it as Vulkan would
    /// claim a backend the transcript was not produced on, and reporting it as
    /// <see cref="Cpu"/> would claim a numerical portability it does not have — measured
    /// 2026-08-21, DirectML at its own default settings scores 53.15% DER against the CPU's
    /// 16.33% on AMI test while looking entirely healthy.
    /// </remarks>
    DirectMl = 3,

    /// <summary>
    /// ONNX Runtime's WebGPU provider: any GPU with a Direct3D 12 or Vulkan driver, on one code
    /// path for every vendor.
    /// </summary>
    /// <remarks>
    /// Like <see cref="DirectMl"/> this belongs to the ONNX components and never runs parakeet.cpp.
    /// Measured 2026-08-21 on AMI test it scores <b>16.3319%</b> DER against the CPU's 16.3324% — a
    /// difference of 0.0005 points, smaller than this project's own C#-against-Python port
    /// difference — so unlike the other GPU providers here it does not move the number that is
    /// published.
    /// </remarks>
    WebGpu = 4,
}
