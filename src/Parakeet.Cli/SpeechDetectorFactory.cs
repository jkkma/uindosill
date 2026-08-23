using Parakeet.Core.Models;
using Parakeet.Core.Segmentation;
using Parakeet.Engine.SileroVad;

namespace Parakeet.Cli;

/// <summary>
/// Resolves the speech detector <c>--vad</c> asks for: the energy gate by default, or the Silero
/// graph on ONNX Runtime for <c>neural</c>.
/// </summary>
/// <remarks>
/// Shaped like <c>LabellerFactory</c> and for the same reason: there are three ways the neural
/// detector can fail to exist — no entry in the catalogue, an entry that is not installed, a graph
/// that will not load — and each wants the sentence that says which one happened and what to do
/// about it. Resolved before the ASR engine so that "download the model first" arrives before
/// 1.34 GiB of weights load rather than after.
/// </remarks>
internal static class SpeechDetectorFactory
{
    public const string Option = "vad";

    /// <summary>The detector the flags ask for, or null for the energy gate.</summary>
    public static ISpeechDetector? Create(CliContext context, ParsedCommandLine parsed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parsed);

        var asked = parsed.Value(Option);
        var mode = (asked ?? "energy").Trim().ToLowerInvariant();

        switch (mode)
        {
            case "energy":
                return null;

            case "neural":
                break;

            default:
                throw new CliUsageException(
                    $"--{Option} takes energy or neural, not '{asked}'. energy is the gate that has always cut the " +
                    "audio; neural is Silero VAD on ONNX Runtime, which hears pauses under music the gate cannot.");
        }

        // Two flags about detection that contradict each other are refused rather than ranked:
        // --no-vad turns detection off and --vad neural turns a different detector on, and a reader
        // of the transcript's provenance would otherwise have to know which one this build let win.
        if (parsed.HasFlag("no-vad"))
        {
            throw new CliUsageException(
                $"--no-vad and --{Option} neural contradict each other: the first decodes fixed windows with no " +
                "detector at all, the second asks for a detector. Give one or the other.");
        }

        var (path, descriptor) = ResolveModel(context);
        var detector = new SileroSpeechDetector(path);

        context.WriteError(
            $"Speech detection: {detector.Name} ({descriptor.Id}). The segment boundaries this run writes are " +
            "this detector's, not the energy gate's, and no figure in docs/UNPROVEN.md measured before 2026-08-23 " +
            "describes them.");

        return detector;
    }

    /// <summary>The catalogue's speech-detection entry and where it lives, or a usage error saying what is missing.</summary>
    public static (string Path, ModelDescriptor Descriptor) ResolveModel(CliContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var descriptor = context.Catalog.VoiceActivityModels.Count switch
        {
            0 => throw new CliUsageException("The model catalogue has no speech-detection model."),
            1 => context.Catalog.VoiceActivityModels[0],
            _ => throw new CliUsageException(
                "The catalogue has more than one speech-detection model; this build takes the first it is " +
                "given and nothing tells it which. Candidates: " +
                string.Join(", ", context.Catalog.VoiceActivityModels.Select(m => m.Id))),
        };

        var path = context.Store.PathFor(descriptor);
        if (!File.Exists(path))
        {
            throw new CliUsageException(
                $"The speech-detection model '{descriptor.Id}' is not installed. Run " +
                $"'uindosill models download {descriptor.Id}' first (it would be at {path}); it is " +
                $"{ModelsCommand.Bytes(descriptor.TotalSizeBytes ?? 0)}.");
        }

        return (path, descriptor);
    }
}
