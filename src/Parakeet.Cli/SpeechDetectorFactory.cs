using Parakeet.Core.Models;
using Parakeet.Core.Segmentation;
using Parakeet.Engine.SileroVad;

namespace Parakeet.Cli;

/// <summary>
/// Resolves the speech detector a <c>transcribe</c> run cuts with: the Silero graph on ONNX Runtime
/// by default whenever its model is installed, the energy gate when it is not or when
/// <c>--vad energy</c> asks for it, and nothing at all under <c>--no-vad</c>.
/// </summary>
/// <remarks>
/// <para>
/// Shaped like <c>LabellerFactory</c> and for the same reason: there are three ways the neural
/// detector can fail to exist — no entry in the catalogue, an entry that is not installed, a graph
/// that will not load — and each wants the sentence that says which one happened and what to do
/// about it. Resolved before the ASR engine so that "download the model first" arrives before
/// 1.34 GiB of weights load rather than after.
/// </para>
/// <para>
/// The default changed on 2026-08-23, the day the detector shipped: it was the gate, with
/// <c>--vad neural</c> as the opt-in, and became the detector at the maintainer's word — the same
/// default the app's checkbox had taken hours earlier. Three things follow from a default that can
/// resolve to either. Every run says on stderr which detector cut it, because a reader of the
/// transcript cannot tell from the flags any more. A model that is installed but will not load is a
/// refusal rather than a fall-back to the gate: a transcript cut by the gate under a default that
/// promises the detector would carry the wrong provenance in silence, and <c>models verify</c> is
/// the answer to a graph that does not open. And <c>--vad energy</c>, asked for by name, says
/// nothing — the person asking knows what they asked for.
/// </para>
/// </remarks>
internal static class SpeechDetectorFactory
{
    public const string Option = "vad";

    /// <summary>The detector this run cuts with, or null for the energy gate or for fixed windows.</summary>
    public static ISpeechDetector? Create(CliContext context, ParsedCommandLine parsed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parsed);

        var asked = parsed.Value(Option);
        var fixedWindows = parsed.HasFlag("no-vad");

        if (asked is null)
        {
            // Fixed windows decode everything and nothing decides where speech is, so there is no
            // detector to load and nothing to say about one.
            return fixedWindows ? null : CreateDefault(context);
        }

        var mode = asked.Trim().ToLowerInvariant();

        switch (mode)
        {
            case "energy":
                return null;

            case "neural":
                break;

            default:
                throw new CliUsageException(
                    $"--{Option} takes energy or neural, not '{asked}'. energy is the loudness gate; neural is Silero VAD " +
                    "on ONNX Runtime, which hears pauses under music the gate cannot, and is the default whenever its " +
                    "model is installed.");
        }

        // Two flags about detection that contradict each other are refused rather than ranked:
        // --no-vad turns detection off and --vad neural turns a different detector on, and a reader
        // of the transcript's provenance would otherwise have to know which one this build let win.
        if (fixedWindows)
        {
            throw new CliUsageException(
                $"--no-vad and --{Option} neural contradict each other: the first decodes fixed windows with no " +
                "detector at all, the second asks for a detector. Give one or the other.");
        }

        var (path, descriptor) = ResolveModel(context);
        var detector = Load(path, descriptor);

        context.WriteError(
            $"Speech detection: {detector.Name} ({descriptor.Id}). The segment boundaries this run writes are " +
            "this detector's, not the energy gate's, and no figure in docs/UNPROVEN.md measured before 2026-08-23 " +
            "describes them.");

        return detector;
    }

    /// <summary>
    /// The default: the detector when its model is installed and loads, otherwise the gate — and in
    /// every case one line on stderr saying which, because the flags no longer say.
    /// </summary>
    private static ISpeechDetector? CreateDefault(CliContext context)
    {
        var entries = context.Catalog.VoiceActivityModels;

        if (entries.Count != 1)
        {
            // Neither is a usage error here, unlike under --vad neural: nobody asked for the
            // detector by name, so the run goes on with the gate and says why.
            context.WriteError(entries.Count == 0
                ? "Speech detection: energy gate. The model catalogue has no speech-detection entry, so the neural " +
                  "default has nothing to load."
                : $"Speech detection: energy gate. The catalogue has {entries.Count} speech-detection entries and " +
                  "nothing says which is the default; --vad neural refuses for the same reason.");
            return null;
        }

        var descriptor = entries[0];
        var path = context.Store.PathFor(descriptor);

        if (!File.Exists(path))
        {
            context.WriteError(
                $"Speech detection: energy gate. The neural detector is the default, but its model '{descriptor.Id}' " +
                $"is not installed. 'uindosill models download {descriptor.Id}' " +
                $"({ModelsCommand.Bytes(descriptor.TotalSizeBytes ?? 0)}) turns it on; --vad energy asks for the gate " +
                "on purpose and silences this line.");
            return null;
        }

        var detector = Load(path, descriptor);

        context.WriteError(
            $"Speech detection: {detector.Name} ({descriptor.Id}), the default since 2026-08-23 whenever its model is " +
            "installed; --vad energy cuts on the gate instead. No figure in docs/UNPROVEN.md measured before " +
            "2026-08-23 describes this detector's segment boundaries.");

        return detector;
    }

    /// <summary>
    /// Opens the graph, and turns a graph that will not open into the sentence that says what to do
    /// about it — a refusal, under the default as under <c>--vad neural</c>, never a fall-back.
    /// </summary>
    private static SileroSpeechDetector Load(string path, ModelDescriptor descriptor)
    {
        try
        {
            return new SileroSpeechDetector(path);
        }
        catch (SpeechDetectorException exception)
        {
            throw new SpeechDetectorException(
                $"The speech-detection model '{descriptor.Id}' is installed at {path} but will not load: " +
                $"{exception.Message} 'uindosill models verify {descriptor.Id}' checks it against the catalogue and " +
                $"'uindosill models download {descriptor.Id} --force' replaces it; --vad energy cuts on the gate " +
                "without it.",
                exception);
        }
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
