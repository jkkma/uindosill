namespace Parakeet.Core.Models;

/// <summary>
/// What a catalogue entry is for. The discriminator exists so that a model which is not the ASR
/// weights can be installed through the same catalogue, installer and digest checks without ever
/// surfacing as a selectable ASR model: <see cref="ModelCatalog.Recommended"/> and every
/// engine-selecting code path look only at <see cref="Transcription"/> entries.
/// </summary>
/// <remarks>
/// Adding a member here compiles clean. Nothing switches on this enum exhaustively — every site
/// asks whether an entry <i>is</i> the task it wants and refuses everything else — so the compiler
/// will not point at the places a new member has to be considered. They are enumerated deliberately
/// instead: <c>ModelTests</c> holds the catalogue half — a new task reaches no ASR list and no
/// other task's list — and the CLI tests hold the refusals each command owes when it is handed the
/// wrong kind of entry. What the two miss is anything that treated "not transcription" as a synonym
/// for one particular other task, which is what the badge and the window's install subscription
/// both did until translation arrived.
/// </remarks>
public enum ModelTask
{
    /// <summary>Speech to text: what <c>transcribe</c> loads.</summary>
    Transcription = 0,

    /// <summary>Who spoke when: what the speaker-labelling opt-in loads. Never an ASR model.</summary>
    Diarisation = 1,

    /// <summary>
    /// Transcript into English: what the translation opt-in loads. Never an ASR model and never a
    /// diariser — it reads text and returns text, and has no idea what audio is.
    /// </summary>
    Translation = 2,

    /// <summary>
    /// Where speech is: what the neural speech-detection opt-in loads in place of the energy gate.
    /// Never an ASR model — it writes no words — and never a diariser: it says when somebody is
    /// speaking and nothing about who. Added 2026-08-23 with the Silero VAD entry.
    /// </summary>
    VoiceActivity = 3,

    /// <summary>
    /// Questions about a finished transcript: what the Ask tab loads. Never an ASR model — it
    /// hears nothing — and never a diariser. Added 2026-08-27, when the answering model stopped
    /// being a file people were told to find for themselves and became a catalogue entry.
    /// </summary>
    /// <remarks>
    /// It is the first entry whose size can exceed what the machine reading this catalogue can
    /// run, which is why <see cref="ModelFit"/> exists: every other task's weights are between
    /// 2 MiB and 1.34 GiB and fit anywhere the application does.
    /// </remarks>
    Answering = 4,
}

/// <summary>
/// One file of a catalogue entry: what to fetch, and what it must weigh and hash when it arrives.
/// </summary>
/// <remarks>
/// Every entry has at least one of these, and until 2026-08-20 every entry had exactly one, which
/// is why the pin used to live directly on <see cref="ModelDescriptor"/>. The ONNX translation route
/// is nine files — two graphs, two configs and a five-file tokenizer — and nine files is not one
/// file with a longer name: each needs its own URL, its own byte count and its own digest, because
/// a set that verifies as a whole and not per file cannot say which member is wrong.
/// </remarks>
public sealed record ModelFile
{
    /// <summary>
    /// Where this file goes, relative to the entry's directory: a bare name, or a <c>/</c>-separated
    /// path beneath it. Never absolute and never able to climb out — see
    /// <c>ModelCatalog.IsSafeRelativeFileName</c>, which is what the manifest is held to.
    /// </summary>
    /// <remarks>
    /// A bare name was the only shape until 2026-08-27. The pyannote entry is what widened it: its
    /// pipeline finds its segmentation model, its embedder and its PLDA matrices through paths
    /// written in its own <c>config.yaml</c>, and that config is pinned by digest — so the layout
    /// has to survive installation rather than be flattened and patched back up.
    /// </remarks>
    public required string FileName { get; init; }

    public required Uri Url { get; init; }

    /// <summary>Expected size in bytes, or null when it has not been pinned.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>
    /// Lowercase hex SHA-256 of this file, or null when nobody has pinned one. Pins are per file
    /// rather than per entry: a digest over a concatenation would tell a user their download is
    /// broken without telling them which of nine files to look at.
    /// </summary>
    public string? Sha256 { get; init; }

    public override string ToString() => FileName;
}

/// <summary>One downloadable set of weights.</summary>
public sealed record ModelDescriptor
{
    private readonly IReadOnlyList<ModelFile> _files = [];

    /// <summary>Stable identifier used on the command line and in settings.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// What the weights do. Read from the manifest's <c>"task"</c>; absent means
    /// <see cref="ModelTask.Transcription"/>, so every entry that predates the field keeps meaning
    /// what it always meant. A build older than a task word would still list that entry as an ASR
    /// model, which is why no such entry is added to the manifest until the word behind it ships —
    /// the discriminator goes first, both times it has been needed.
    /// </summary>
    public ModelTask Task { get; init; } = ModelTask.Transcription;

    /// <summary>Upstream checkpoint this was converted from, shared across quantisations.</summary>
    public required string Family { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>GGUF quantisation, e.g. <c>f16</c>, <c>q8_0</c>, <c>q4_k</c>.</summary>
    public required string Quantisation { get; init; }

    /// <summary>
    /// Every file this entry installs, in manifest order and never empty.
    /// </summary>
    /// <remarks>
    /// There is no <c>FileName</c>, <c>Url</c>, <c>SizeBytes</c> or <c>Sha256</c> on the descriptor
    /// any more, and their removal was the point rather than a side effect. Leaving them as
    /// shortcuts onto the first file would have let every existing call site keep compiling while
    /// silently meaning "the first of nine" — so the four were deleted and the compiler was made to
    /// name each place that had assumed one file. <see cref="TotalSizeBytes"/> and
    /// <see cref="IsFullyPinned"/> are what the display sites wanted anyway.
    /// </remarks>
    public required IReadOnlyList<ModelFile> Files
    {
        get => _files;
        init => _files = value is { Count: > 0 }
            ? value
            : throw new ArgumentException($"Model '{Id}' must have at least one file.", nameof(value));
    }

    /// <summary>
    /// Subdirectory of the model store this entry installs into, or null to install into the store
    /// root as a bare file.
    /// </summary>
    /// <remarks>
    /// Required of any entry with more than one file, and the reason is the file names rather than
    /// tidiness: the ONNX route ships <c>config.json</c>, <c>vocab.json</c> and
    /// <c>encoder_model.onnx</c>, none of which is a name one entry can own in a shared directory.
    /// A single-file entry may still ask for one, but none does — the five GGUF entries and the
    /// diariser predate this and stay exactly where they are.
    /// </remarks>
    public string? DirectoryName { get; init; }

    /// <summary>True when this entry installs into a directory of its own.</summary>
    public bool IsMultiFile => DirectoryName is not null;

    /// <summary>
    /// What this entry is called on disk: the directory for a multi-file entry, the file name for a
    /// single-file one. The thing <see cref="IModelStore.PathFor"/> resolves against the store root.
    /// </summary>
    public string StorageName => DirectoryName ?? Files[0].FileName;

    /// <summary>
    /// Every file's size added up, or null when any one of them is unpinned — because a total that
    /// silently omits a file is a smaller number than the truth, which is the direction that
    /// matters for a download the user is deciding whether to start.
    /// </summary>
    public long? TotalSizeBytes =>
        Files.All(f => f.SizeBytes is not null) ? Files.Sum(f => f.SizeBytes!.Value) : null;

    /// <summary>
    /// True when every file carries a digest. <see cref="ModelInstaller"/> refuses to install an
    /// entry that does not unless the caller explicitly opts in, and <b>one</b> unpinned file among
    /// nine is enough to make that true: a set is only as checked as its least-checked member.
    /// A 670 MB blob fetched over the network with no integrity check is not something to install
    /// quietly into a user's profile.
    /// </summary>
    public bool IsFullyPinned => Files.All(f => f.Sha256 is not null);

    /// <summary>
    /// False when the URL, size and digest in the manifest have not been checked against the
    /// live repository. Surfaced in the UI and the CLI: guessing is allowed, pretending is not.
    /// </summary>
    public bool Verified { get; init; }

    /// <summary>SPDX-style licence identifier of the weights.</summary>
    public required string License { get; init; }

    /// <summary>
    /// Keys into <see cref="Licensing.Attributions"/> for the notices this entry owes. Never empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A list rather than a key, because an entry can be one download and more than one upstream
    /// work. The second diariser is the case that forced it: DiariZen's checkpoint is CC BY-NC 4.0
    /// and the speaker-embedding model it clusters with is CC BY 4.0, the pipeline does not run
    /// without both, and there is no dependency concept here that would let two entries express
    /// "install these together". One entry, two notices.
    /// </para>
    /// <para>
    /// <b>Order is the order they are rendered in</b>, so the licence that constrains what a user
    /// may do goes first. The manifest writes either <c>attributionId</c> for the single case or
    /// <c>attributionIds</c> for the plural one — the same either/or the file already uses for
    /// <c>fileName</c> against <c>files</c>, and refused together for the same reason.
    /// </para>
    /// </remarks>
    public required IReadOnlyList<string> AttributionIds { get; init; }

    /// <summary>
    /// Which engine drives these weights, when a task has more than one. Null when the task has
    /// only ever had a single engine, which is every entry but the two diarisers.
    /// </summary>
    /// <remarks>
    /// Stated by the entry rather than derived from its id. The alternative was matching on an id
    /// prefix, which works until an entry is renamed and then routes weights to the wrong loader
    /// - a failure that would surface as a model-load error several hundred megabytes in, with
    /// nothing pointing at the rename that caused it.
    /// </remarks>
    public string? Engine { get; init; }

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

    /// <summary>
    /// The file for a single-file entry; the directory for a multi-file one. Both are "the thing
    /// you point an engine at", which is why one property carries both rather than the callers
    /// branching.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>Bytes on disk: the file's length, or every file in the directory added up.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Null when the file is on disk but not in the catalogue (sideloaded).</summary>
    public ModelDescriptor? Descriptor { get; init; }

    public bool IsSideloaded => Descriptor is null;
}
