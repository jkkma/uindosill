using System.Text.Json;
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

    /// <summary>
    /// The providers <c>auto</c> tried and passed over before the one that loaded, each with the
    /// reason it did not build — empty when the first candidate built or the provider was named.
    /// </summary>
    /// <remarks>
    /// Read from the <c>fellBackFrom</c> list both engines put in their capabilities. Until
    /// 2026-08-22 the sidecar kept these reasons only for the case where every candidate failed,
    /// so a run that landed on the CPU because WebGPU would not initialise said nothing about why,
    /// and the one fact that explained its speed was discarded at the moment it was known.
    /// </remarks>
    public static IReadOnlyList<string> ReadFellBackFrom(JsonElement capabilities)
    {
        if (!capabilities.TryGetProperty("fellBackFrom", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var reasons = new List<string>();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } reason)
            {
                reasons.Add(reason);
            }
        }

        return reasons;
    }
}
