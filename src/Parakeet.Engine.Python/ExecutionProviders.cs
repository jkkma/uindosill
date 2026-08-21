using Parakeet.Core.Transcription;

namespace Parakeet.Engine.Python;

/// <summary>
/// The protocol's names for ONNX Runtime's execution providers, and what they are on this side.
/// </summary>
/// <remarks>
/// <para>
/// One place, because two engines report a backend over the same protocol and a mapping written
/// twice is a mapping that will one day disagree with itself. The names are the sidecar's — they
/// are what <c>engine.py</c>'s <c>PROVIDERS</c> is keyed by, what the command line accepts, and
/// what comes back in a <c>capabilities</c> reply.
/// </para>
/// <para>
/// <b>The unknown case is CPU on purpose.</b> A provider this build has never heard of is one whose
/// numerical behaviour it cannot describe, and the CPU is the only backend every published figure
/// in this project was produced on — so an unrecognised name reads as "no claim of acceleration"
/// rather than inventing an enum member for it. The sidecar refuses to load a provider it does not
/// know, so a name arriving here that is not below means the two sides have gone out of step, which
/// the protocol version is what guards.
/// </para>
/// </remarks>
internal static class ExecutionProviders
{
    /// <summary>Turns a provider name from the protocol into the backend it is.</summary>
    public static ComputeBackend Parse(string? value) => value switch
    {
        "cuda" => ComputeBackend.Cuda,
        "dml" => ComputeBackend.DirectMl,
        "webgpu" => ComputeBackend.WebGpu,
        _ => ComputeBackend.Cpu,
    };
}
