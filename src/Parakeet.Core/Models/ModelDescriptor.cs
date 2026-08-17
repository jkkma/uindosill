namespace Parakeet.Core.Models;

/// <summary>
/// What a catalogue entry is for. The discriminator exists so that a diarisation model can be
/// installed through the same catalogue, installer and digest checks as the ASR weights without
/// ever surfacing as a selectable ASR model: <see cref="ModelCatalog.Recommended"/> and every
/// engine-selecting code path look only at <see cref="Transcription"/> entries.
/// </summary>
public enum ModelTask
{
    /// <summary>Speech to text: what <c>transcribe</c> loads.</summary>
    Transcription = 0,

    /// <summary>Who spoke when: what the speaker-labelling opt-in loads. Never an ASR model.</summary>
    Diarisation = 1,
}

/// <summary>One downloadable set of weights.</summary>
public sealed record ModelDescriptor
{
    /// <summary>Stable identifier used on the command line and in settings.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// What the weights do. Read from the manifest's <c>"task"</c>; absent means
    /// <see cref="ModelTask.Transcription"/>, so every entry that predates the field keeps meaning
    /// what it always meant. A build older than the field would still list a diarisation entry as
    /// an ASR model, which is why no such entry is added to the manifest until the model behind
    /// it exists — the discriminator has to ship first.
    /// </summary>
    public ModelTask Task { get; init; } = ModelTask.Transcription;

    /// <summary>Upstream checkpoint this was converted from, shared across quantisations.</summary>
    public required string Family { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>GGUF quantisation, e.g. <c>f16</c>, <c>q8_0</c>, <c>q4_k</c>.</summary>
    public required string Quantisation { get; init; }

    /// <summary>File name on disk, inside the model store.</summary>
    public required string FileName { get; init; }

    public required Uri Url { get; init; }

    /// <summary>Expected size in bytes, or null when it has not been pinned.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// Lowercase hex SHA-256 of the file. Null means nobody has pinned a digest yet, and
    /// <see cref="ModelInstaller"/> refuses to install such a model unless the caller
    /// explicitly opts in. A 670 MB blob fetched over the network with no integrity check is
    /// not something to install quietly into a user's profile.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// False when the URL, size and digest in the manifest have not been checked against the
    /// live repository. Surfaced in the UI and the CLI: guessing is allowed, pretending is not.
    /// </summary>
    public bool Verified { get; init; }

    /// <summary>SPDX-style licence identifier of the weights.</summary>
    public required string License { get; init; }

    /// <summary>Key into <see cref="Licensing.Attributions"/> for the required notice.</summary>
    public required string AttributionId { get; init; }

    /// <summary>BCP-47 language tags the model claims. Empty when unconstrained or unknown.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    public bool Recommended { get; init; }

    /// <summary>Anything a user should know before choosing this file.</summary>
    public string? Notes { get; init; }

    public override string ToString() => Id;
}

/// <summary>A model as found on disk.</summary>
public sealed record InstalledModel
{
    public required string Id { get; init; }

    public required string Path { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>Null when the file is on disk but not in the catalogue (sideloaded).</summary>
    public ModelDescriptor? Descriptor { get; init; }

    public bool IsSideloaded => Descriptor is null;
}
