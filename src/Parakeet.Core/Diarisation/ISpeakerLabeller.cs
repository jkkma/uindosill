using Parakeet.Core.Audio;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Diarisation;

public sealed record SpeakerLabellingOptions
{
    public static SpeakerLabellingOptions Default { get; } = new();

    /// <summary>
    /// The number of speakers, when the caller knows it — two hosts, no guests. Null lets the
    /// labeller estimate it. A labeller that cannot honour a fixed count says so through its
    /// capabilities rather than silently ignoring this.
    /// </summary>
    public int? SpeakerCount { get; init; }

    /// <summary>
    /// A word that no turn overlaps at all is still attributed to the nearest turn if the gap
    /// between the word's edge and the turn is at most this. Turn boundaries and word boundaries
    /// jitter against each other by tens of milliseconds; without a tolerance the first word of
    /// every turn would go unattributed.
    /// </summary>
    public TimeSpan AttributionTolerance { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The display name given to the n-th distinct voice, numbered by first appearance. Null keeps
    /// the labeller's own labels — a diariser's cluster ids, which is what a scorer wants to see.
    /// </summary>
    public string? DisplayNameFormat { get; init; } = "Speaker {0}";

    public void Validate()
    {
        if (SpeakerCount is { } count && count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SpeakerCount), count, "Speaker count must be at least one.");
        }

        if (AttributionTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AttributionTolerance), AttributionTolerance, "Tolerance cannot be negative.");
        }

        if (DisplayNameFormat is { } format && string.IsNullOrWhiteSpace(format))
        {
            throw new ArgumentException("The display-name format cannot be blank; use null to keep raw labels.", nameof(DisplayNameFormat));
        }
    }
}

/// <summary>
/// What is knowable about a labeller <i>before</i> it is loaded: the two limits and whose they are.
/// </summary>
/// <remarks>
/// <para>
/// A smaller type than <see cref="SpeakerLabellerCapabilities"/> and smaller on purpose. The window
/// has to answer "how many voices can this tell apart" and "how long a recording are its labels
/// established on" while the queue is being built and the weights are still on disk, because both
/// drive warnings that are worth reading before a batch and worthless after it. Those questions have
/// answers that do not depend on anything being loaded.
/// </para>
/// <para>
/// <b><see cref="SpeakerLabellerCapabilities.Backend"/> is deliberately absent.</b> It is the one
/// field only a loaded engine can answer — since the diariser moved out of process the provider is
/// chosen inside the sidecar — and a declared guess at it is how provenance becomes fiction. A
/// caller wanting the backend has to hold a labeller that has loaded, which is the point.
/// </para>
/// </remarks>
public sealed record SpeakerLabellerLimits
{
    /// <summary>
    /// What to call the labeller in a sentence: its model id where there is one, its engine name
    /// otherwise. Collapsed here rather than carried as two fields, because every reader of it made
    /// the same choice and one of them would eventually make it differently.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>True when <see cref="SpeakerLabellingOptions.SpeakerCount"/> reaches the model.</summary>
    public bool SupportsFixedSpeakerCount { get; init; }

    /// <summary>The most distinct voices the model can keep apart, when it has such a limit.</summary>
    public int? MaxSpeakers { get; init; }

    /// <summary>
    /// How long a recording this labeller's output has actually been established on, when that is
    /// known. Null means no such bound has been measured — not "any length".
    /// </summary>
    public TimeSpan? ReliableUpTo { get; init; }
}

/// <summary>What a loaded speaker labeller can do and where its labels come from.</summary>
public sealed record SpeakerLabellerCapabilities
{
    public required string EngineName { get; init; }

    /// <summary>
    /// Identifier of the loaded diarisation model, carried into the transcript's provenance
    /// beside the ASR model's id — a transcript that cannot say which model named its speakers is
    /// not a result anybody can re-examine later.
    /// </summary>
    public string? ModelId { get; init; }

    public ComputeBackend Backend { get; init; } = ComputeBackend.Cpu;

    /// <summary>True when <see cref="SpeakerLabellingOptions.SpeakerCount"/> reaches the model.</summary>
    public bool SupportsFixedSpeakerCount { get; init; }

    /// <summary>
    /// The most distinct voices the model can keep apart, when it has such a limit. A file with more
    /// speakers than this is labelled with the fifth voice merged into one of the others, and the
    /// caller must say so rather than present the labels as complete.
    /// </summary>
    public int? MaxSpeakers { get; init; }

    /// <summary>
    /// How long a recording this labeller's output has actually been established on, when that is
    /// known. Null means no such bound has been measured — which is not the same as "any length".
    /// </summary>
    /// <remarks>
    /// A separate limit from <see cref="MaxSpeakers"/> and a differently-shaped one. The cap is
    /// architectural: it is in the model's geometry, it is the same on every file, and it is
    /// knowable without running anything. This is empirical: it is where the evidence stops, and
    /// past it the labels are not known to be wrong so much as not known to be right. Both belong
    /// on the capability rather than in a caller, because a caller that has to remember either is a
    /// caller that will one day forget.
    /// </remarks>
    public TimeSpan? ReliableUpTo { get; init; }

    /// <summary>
    /// The part of this that a caller can also learn without loading anything.
    /// </summary>
    /// <remarks>
    /// A projection rather than a component so that no construction site changes, and so that the
    /// sentences drawn before a run and the sentences drawn after it come out of one body of code —
    /// see <see cref="SpeakerLabelling.DescribeUnreachableCount(SpeakerLabellerLimits, int?)"/>. A
    /// hint beside a field that disagrees with the warning that stops the batch is worse than
    /// either alone.
    /// </remarks>
    public SpeakerLabellerLimits Limits => new()
    {
        Name = ModelId ?? EngineName,
        SupportsFixedSpeakerCount = SupportsFixedSpeakerCount,
        MaxSpeakers = MaxSpeakers,
        ReliableUpTo = ReliableUpTo,
    };
}

/// <summary>
/// Says who is speaking when. The one abstraction the rest of the application knows about for
/// diarisation: no detail of sherpa-onnx, ONNX Runtime or anything else may leak through it, for
/// the same reason <see cref="ITranscriptionEngine"/> hides parakeet.cpp — the engine behind it is
/// still being chosen by measurement, and the choice must stay one project's business.
/// </summary>
/// <remarks>
/// A labeller reads the audio itself and returns turns on the file's timeline. It does not see the
/// transcript: attributing words to turns is a pure post-step (<see cref="SpeakerAssignment"/>),
/// so the ASR engine and the labeller stay independently testable and independently replaceable.
/// Both audio sources this product has are single-read, so a caller opens the file a second time
/// for the labeller — a second decode, and a cost only the opt-in pays.
/// </remarks>
public interface ISpeakerLabeller : IAsyncDisposable
{
    SpeakerLabellerCapabilities Capabilities { get; }

    /// <summary>Loads the model. Idempotent, expensive, never on a UI thread.</summary>
    ValueTask LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Labels a whole source. Returns every turn found, in time order, with the labeller's own
    /// speaker labels; overlapping turns of different speakers are expected and welcome.
    /// </summary>
    Task<IReadOnlyList<SpeakerTurn>> LabelAsync(
        IAudioSource audio,
        SpeakerLabellingOptions options,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken ct = default);
}
