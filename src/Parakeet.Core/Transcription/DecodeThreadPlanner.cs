namespace Parakeet.Core.Transcription;

/// <summary>
/// Chooses how many threads to give a decode.
/// </summary>
/// <remarks>
/// Handing the engine <see cref="Environment.ProcessorCount"/> is the obvious thing and the
/// wrong one: past roughly eight threads the throughput curve flattens while the machine
/// stops feeling responsive, which on a desktop app costs more than the milliseconds it saves.
/// Leaving headroom also keeps the UI thread schedulable while a long file decodes.
/// </remarks>
public static class DecodeThreadPlanner
{
    /// <summary>The point past which more threads stop paying for themselves.</summary>
    public const int MaxRecommended = 8;

    public static int Recommended(int? requested = null, int? processorCount = null)
    {
        var processors = Math.Max(1, processorCount ?? Environment.ProcessorCount);

        if (requested is { } explicitCount)
        {
            // An explicit request is honoured — someone benchmarking needs to be able to ask
            // for sixteen — but it is still clamped to something that can run.
            return Math.Max(1, explicitCount);
        }

        // Leave a core for the UI and one for the OS on anything but a very small machine.
        var headroom = processors <= 2 ? 0 : processors <= 4 ? 1 : 2;
        return Math.Clamp(processors - headroom, 1, MaxRecommended);
    }

    /// <summary>True when a caller asked for more threads than the recommended ceiling.</summary>
    public static bool IsAboveRecommended(int threads) => threads > MaxRecommended;
}
