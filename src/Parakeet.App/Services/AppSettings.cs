using System.Text.Json;
using Parakeet.Core.Models;
using Parakeet.Core.Transcription;
using Parakeet.Engine.LlamaServer;

namespace Parakeet.App.Services;

/// <summary>Where an answer is drawn from, as the person has asked for it to be decided.</summary>
public enum AskModePreference
{
    /// <summary>The question decides, through <see cref="Parakeet.Core.Answers.QuestionRouter"/>.</summary>
    Automatic = 0,

    /// <summary>Always the parts that matched — the fast tier, and the only one with acceptable
    /// prefill on integrated graphics at length.</summary>
    Retrieval = 1,

    /// <summary>Always the whole recording, however long it is.</summary>
    WholeTranscript = 2,
}

/// <summary>
/// How many retrieved windows the retrieval tier hands the model — the dominant term in how long
/// an answer takes, and the one dial where speed is bought with something other than precision.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured 2026-08-27 on the second machine</b>, the 26B-A4B at UD-IQ4_XS with its drafting
/// head, thirteen retrieval questions: eight windows answered in a median 37.8 s, six in 31.7 s
/// and four in <b>16.6 s</b>. Prefill is about 60% of that wall and scales with the evidence, so
/// this is where the time is. The mechanical checks did not degrade as the evidence shrank —
/// every citation resolved at all three depths, all three adversarial questions were abstained
/// from at all three, and verified quotes did not fall.
/// </para>
/// <para>
/// <b>Why the default is still the slowest one.</b> What that run did not measure is recall: with
/// fewer windows the answer can simply not be in front of the model, and a question whose evidence
/// ranks fifth is answered worse or not at all. Scoring that needs gold ranges — the labelled
/// question set in <c>tests/fixtures/csb384/questions.json</c>, whose status is still
/// <c>template</c> — so the faster settings are offered rather than chosen, and
/// <c>docs/UNPROVEN.md</c> records which half of the trade has evidence behind it.
/// </para>
/// </remarks>
public enum AskEvidenceDepth
{
    /// <summary>Eight windows: what the panel has always used, and what every citation figure in
    /// the record was measured at.</summary>
    Thorough = 0,

    /// <summary>Six windows. A quarter off the wall in the measured run.</summary>
    Balanced = 1,

    /// <summary>Four windows, and roughly two and a half times faster — with the recall the
    /// paragraph above describes unmeasured.</summary>
    Fast = 2,
}

/// <summary>The handful of choices that outlive a run of the application.</summary>
public sealed record AppSettings
{
    /// <summary>
    /// Whether the application asks GitHub Releases, once at launch, whether a newer version
    /// exists. Default on, and the one thing this product does on the network unprompted —
    /// <c>docs/PHASES.md</c> decision 4, and the README says so where a user will read it.
    /// </summary>
    public bool CheckForUpdatesOnLaunch { get; init; } = true;

    /// <summary>
    /// The compute backend the user last chose, or null when they never have.
    /// </summary>
    /// <remarks>
    /// Null is not a synonym for CPU or for Vulkan — it means "nobody has said", which is what lets
    /// <see cref="Parakeet.App.ViewModels.MainWindowViewModel"/> pick the best backend actually
    /// present on disk instead. A stored value always wins over that, including when it is the
    /// slower one: someone who selected CPU because the GPU path misbehaves on their machine has
    /// said something, and having the application quietly reinstate the GPU on the next launch
    /// would be the setting mattering least exactly when it matters most.
    /// </remarks>
    public ComputeBackend? Backend { get; init; }

    /// <summary>
    /// Whether the ask model thinks before answering. Off as shipped: thinking runs at the
    /// model's own generation speed, which on integrated graphics turns a half-minute answer
    /// into several minutes — the measured 2026-08-24 basis is in docs/UNPROVEN.md — and the
    /// grammar-constrained default answered no worse in the same session's comparison.
    /// </summary>
    public bool AskThinking { get; init; }

    /// <summary>
    /// Where answers are drawn from. <see cref="AskModePreference.Automatic"/> as shipped — the
    /// question decides, which is the register's decision 3 router — because the alternative is
    /// a person having to know that "summarise this" and "when did they mention X" are served by
    /// different tiers. The two fixed settings stay for anyone who would rather decide once.
    /// </summary>
    public AskModePreference AskMode { get; init; } = AskModePreference.Automatic;

    /// <summary>
    /// How much evidence the retrieval tier shows the model. <see cref="AskEvidenceDepth.Thorough"/>
    /// as shipped, which is the eight windows every measured citation figure in this project was
    /// taken at; the faster settings trade an unmeasured amount of recall for a large and measured
    /// amount of time. See <see cref="AskEvidenceDepth"/>.
    /// </summary>
    public AskEvidenceDepth AskEvidence { get; init; } = AskEvidenceDepth.Thorough;

    /// <summary>The number of windows <see cref="AskEvidence"/> asks for.</summary>
    public int EvidenceWindows => WindowsFor(AskEvidence);

    /// <summary>
    /// The windows each depth asks for. Static so a caller holding the enum alone — the window
    /// model's picker, which never has an <see cref="AppSettings"/> in hand — can ask without
    /// building one.
    /// </summary>
    public static int WindowsFor(AskEvidenceDepth depth) => depth switch
    {
        AskEvidenceDepth.Fast => 4,
        AskEvidenceDepth.Balanced => 6,
        _ => 8,
    };

    /// <summary>
    /// The file name — not the path — of the .gguf the Ask panel should serve, or null to take
    /// the catalogue's answering default when it is installed and the largest file present when
    /// it is not. Null is the shipped default and stays meaningful: it means "nobody has chosen",
    /// so a folder whose contents change later still resolves to something.
    /// </summary>
    /// <remarks>
    /// A name rather than a path because the folder is the application's own and may move
    /// between installs, and because a stored path to a file someone has since deleted is a
    /// setting that fails silently. A name that no longer matches anything falls back the same
    /// way, exactly as if nothing had been chosen — the models on that disk are not this
    /// application's to keep track of between runs.
    /// </remarks>
    public string? AskModelFileName { get; init; }

    /// <summary>
    /// The catalogue id of the model that labels speakers, or null when nobody has chosen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One entry does this job since 2026-08-27, and this setting is kept rather than retired.</b>
    /// It existed because two did and they were not ranked: Sortformer was fast, shipped with the
    /// installer and stopped at four voices, while the pyannote pipeline has no such limit and is
    /// downloaded rather than bundled, needing a Hugging Face token because its repository is gated.
    /// Sortformer is in <c>attic/sortformer/</c> now, so the choice this stored has one option.
    /// <para>
    /// Kept because the reasoning that made it a choice is intact — which of two diarisers suits a
    /// recording is not something this project can answer for somebody — and because a third entry
    /// is the obvious next thing to want, given that nothing about the remaining one's accuracy has
    /// been measured. Retiring it would mean writing it again.
    /// </para>
    /// </para>
    /// <para>
    /// <b>Null keeps meaning "nobody has said", and it has to.</b> A stored id whose entry is not
    /// installed — removed from the Models tab, or carried over from another machine — resolves as
    /// if nothing had been chosen rather than turning speaker labelling off, because a setting that
    /// silently disables a feature is worse than one that is ignored. The same reasoning as
    /// <see cref="AskModelFileName"/>, and for the same reason: the id is stable where a path
    /// is not.
    /// </para>
    /// </remarks>
    public string? DiarisationModelId { get; init; }

    /// <summary>
    /// A Hugging Face access token, for the one catalogue entry whose repository is gated, or null
    /// when the user has not supplied one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every other entry downloads anonymously, and this exists for exactly one that cannot.</b>
    /// `pyannote/speaker-diarization-community-1` requires an accepted user agreement; an
    /// unauthenticated fetch returns 401 rather than the file. There is no token this product could
    /// ship on the user's behalf — the agreement is between them and the model's authors — so the
    /// entry is uninstallable until they paste one in.
    /// </para>
    /// <para>
    /// <b>It is stored in the settings file as written, which is worth knowing rather than
    /// discovering.</b> Uindosill does not encrypt it: the file already sits under the user's
    /// profile with their own ACL, and a key derived from something on the same machine would look
    /// like protection while adding none. A token pasted here should be a read-only one scoped to
    /// the model repositories, which is what Hugging Face's own guidance recommends and what its
    /// token page offers by default. <see cref="ModelInstaller"/> sends it to
    /// <c>huggingface.co</c> and to nowhere else.
    /// </para>
    /// <para>
    /// <b>The environment wins where it is set.</b> <c>HF_TOKEN</c> is the name Hugging Face's own
    /// tooling reads, so a machine that already exports one should not need it pasted a second
    /// time — and a token that only ever lives in the environment never reaches a file this product
    /// writes. See <c>HuggingFaceToken.Resolve</c>.
    /// </para>
    /// </remarks>
    public string? HuggingFaceToken { get; init; }

    /// <summary>
    /// Which execution provider labels speakers, or null for <c>auto</c> — a shortlist the sidecar
    /// tries best-first rather than a single name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Separate from <see cref="Backend"/>, and it cannot be folded into it.</b> That one is the
    /// recogniser's, whose backends are parakeet.cpp's — Vulkan, CUDA, CPU. The diariser is the
    /// torch pyannote pipeline, whose providers are <c>cpu</c>, <c>cuda</c> and <c>webgpu</c> —
    /// the last through two ONNX graphs the application derives for itself. The two sets overlap
    /// only in the word "CPU" and mean different runtimes even there. One control offering the
    /// union would offer Vulkan to a diariser that has no Vulkan path and WebGPU to a recogniser
    /// that has none, which is why the window went without a diariser picker until this setting
    /// existed.
    /// </para>
    /// <para>
    /// <b>Null means auto, and auto is a shortlist.</b> Since 2026-08-28 the sidecar tries CUDA
    /// where a torch with it is present (which is what the CUDA pack installs), then WebGPU where
    /// the derived graphs exist, then the processor. Each promotion rests on a measured
    /// equivalence — the same speakers and boundaries on what was tried — and none on accuracy:
    /// no published figure exists for any route on this pipeline. Naming a provider is how
    /// somebody refuses the shortlist knowingly, and a named provider refuses rather than
    /// falling back.
    /// </para>
    /// </remarks>
    public string? DiarisationProvider { get; init; }

    /// <summary>
    /// Windows of audio the diariser batches together, or null for the checkpoint's own value.
    /// (The retired ONNX diariser, whose batching was its exported graph's geometry, ignored
    /// this; it is in <c>attic/sortformer/</c> and every shipping labeller reports a number.)
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A memory setting, and it must not be presented as anything else.</b> Peak working set on
    /// a ten-minute recording was roughly 3.9, 6.8 and 11.7 GB at batch 8, 16 and 32, and the labels
    /// were identical at all three — 225 turns, 5 speakers. A speed difference was published
    /// alongside those and withdrawn on 2026-08-27: the sweep ran the three sizes once each, in one
    /// process, ascending, on a machine that cannot hold the largest resident, so batch size and
    /// paging were the same condition.
    /// </para>
    /// <para>
    /// <b>Null is the shipped value and is not a number.</b> It means the checkpoint's own 32, which
    /// is what makes running the published artefact's configuration the thing a person gets by not
    /// touching this. A default here would be this project choosing for every machine again.
    /// </para>
    /// </remarks>
    public int? DiarisationBatchSize { get; init; }

    /// <summary>
    /// Where a mixture-of-experts ask model's experts run.
    /// <see cref="MoeExpertPlacement.Automatic"/> as shipped — the Vulkan loader is asked, and a
    /// card holds its own experts where the processor's graphics cannot.
    /// </summary>
    /// <remarks>
    /// A setting rather than a detected constant because only one end of it has been measured:
    /// on the second machine, in system memory is the difference between a 26B-class mixture
    /// running and not loading at all, and no machine here has a discrete card on the Vulkan ask
    /// path to measure the other end on. Somebody whose card the automatic rule reads wrongly —
    /// or whose driver the loader cannot answer for — needs a way to say so that does not
    /// involve rebuilding.
    /// </remarks>
    public MoeExpertPlacement AskExpertPlacement { get; init; } = MoeExpertPlacement.Automatic;

    /// <summary>
    /// The output folder the user last chose, or null when they never have — blank in the box,
    /// files beside each input.
    /// </summary>
    /// <remarks>
    /// Restored at launch only when the directory still exists, and cleared from the file when it
    /// does not: the folder a user picks is often a removable drive, and a stored path with no
    /// drive behind it would point every export at a location that cannot take one. Missing means
    /// choose again, not fail later.
    /// </remarks>
    public string? OutputDirectory { get; init; }

    public static AppSettings Default { get; } = new();
}

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as JSON beside the model weights.
/// </summary>
/// <remarks>
/// <para>
/// Beside the weights, not in the install directory: a settings file under the install root is
/// destroyed by every update, so the update check a user switched off would switch itself back on
/// at the first update — which is the setting mattering least exactly when it matters most.
/// <see cref="UserDataPaths.RootDirectory"/> is the one copy of that path.
/// </para>
/// <para>
/// Every failure here is swallowed and answered with the defaults. A settings file is not worth a
/// window that will not open: an unreadable, truncated or hand-edited file has to degrade to "as
/// shipped" rather than to a crash on the first frame.
/// </para>
/// </remarks>
public sealed class AppSettingsStore
{
    /// <summary>Overrides the file's location, for portable installs and tests.</summary>
    public const string PathEnvironmentVariable = "UINDOSILL_SETTINGS_PATH";

    public AppSettingsStore(string? path = null) =>
        Path = path
            ?? Environment.GetEnvironmentVariable(PathEnvironmentVariable)
            ?? DefaultPath();

    public string Path { get; }

    /// <summary><c>%LOCALAPPDATA%\Uindosill\settings.json</c> on Windows.</summary>
    public static string DefaultPath() =>
        System.IO.Path.Combine(UserDataPaths.RootDirectory(), "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return AppSettings.Default;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(Path));
            var root = document.RootElement;

            return new AppSettings
            {
                CheckForUpdatesOnLaunch = ReadBool(root, "checkForUpdatesOnLaunch", AppSettings.Default.CheckForUpdatesOnLaunch),
                AskThinking = ReadBool(root, "askThinking", AppSettings.Default.AskThinking),
                AskMode = ReadAskMode(root),
                AskEvidence = ReadAskEvidence(root),
                AskModelFileName = ReadString(root, "askModelFileName"),
                HuggingFaceToken = ReadString(root, "huggingFaceToken"),
                DiarisationModelId = ReadString(root, "diarisationModelId"),
                DiarisationProvider = ReadDiarisationProvider(root),
                DiarisationBatchSize = ReadDiarisationBatchSize(root),
                AskExpertPlacement = ReadExpertPlacement(root),
                Backend = ReadBackend(root),
                OutputDirectory = ReadString(root, "outputDirectory"),
            };
        }
#pragma warning disable CA1031 // Any unreadable file means "as shipped", never a failure to start.
        catch (Exception)
#pragma warning restore CA1031
        {
            return AppSettings.Default;
        }
    }

    /// <summary>
    /// Reads the file, applies <paramref name="change"/> to it, and writes it back.
    /// </summary>
    /// <remarks>
    /// The only way callers should write a single setting, and the reason is a bug this file
    /// invited the moment it held more than one: <c>Save(new AppSettings { OneField = value })</c>
    /// compiles, reads correctly, and silently resets every other field to its default. Both
    /// call sites did exactly that when there was only one setting and it was harmless. Take the
    /// current settings and modify them, and it cannot happen.
    /// </remarks>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Save(change(Load()));
    }

    /// <summary>Returns false when the file could not be written; the caller carries on regardless.</summary>
    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var values = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["checkForUpdatesOnLaunch"] = settings.CheckForUpdatesOnLaunch,
                ["askThinking"] = settings.AskThinking,

                // The name, never the enum's number — same rule as the backend, and for the same
                // reason: reordering the enum would silently turn one user's saved choice into
                // another's.
                ["askMode"] = settings.AskMode.ToString().ToLowerInvariant(),
                ["askEvidence"] = settings.AskEvidence.ToString().ToLowerInvariant(),

                // The name for the same reason again. Always written, never omitted: unlike the
                // backend, "nobody has said" and the shipped default are the same behaviour here
                // — automatic asks the loader either way — so there is no second shape to keep.
                ["askExpertPlacement"] = settings.AskExpertPlacement.ToString().ToLowerInvariant(),
            };

            // Omitted rather than written as null when nobody has chosen, so "never chosen" and
            // "chosen and then unset" are the same file rather than two shapes Load has to agree
            // about. Written as the name, never the enum's number: the numbers are an
            // implementation detail, and reordering the enum would silently turn one user's saved
            // CUDA into Vulkan.
            if (settings.Backend is { } backend)
            {
                values["backend"] = backend.ToString().ToLowerInvariant();
            }

            // Omitted when blank for the same reason as the backend: "never chosen" and "cleared"
            // are one shape.
            if (settings.OutputDirectory is { Length: > 0 } outputDirectory)
            {
                values["outputDirectory"] = outputDirectory;
            }

            if (settings.AskModelFileName is { Length: > 0 } askModel)
            {
                values["askModelFileName"] = askModel;
            }

            // Absent means "not supplied", which is what `HuggingFaceToken.Resolve` treats as no
            // token at all rather than as an empty bearer credential. Written only when there is
            // something to write, like every other optional key here — so clearing the box removes
            // the key rather than leaving `""` behind for the next reader to puzzle over.
            if (settings.HuggingFaceToken is { Length: > 0 } huggingFaceToken)
            {
                values["huggingFaceToken"] = huggingFaceToken;
            }

            // Same shape again: absent means nobody has chosen a diariser, which is not the
            // same as choosing none. EngineProvider reads it that way.
            if (settings.DiarisationModelId is { Length: > 0 } diarisationModel)
            {
                values["diarisationModelId"] = diarisationModel;
            }

            // Absent means auto, and auto is a real choice rather than a missing one — so writing
            // "auto" here would be a second spelling of the same state for Load to reconcile.
            if (settings.DiarisationProvider is { Length: > 0 } diarisationProvider)
            {
                values["diarisationProvider"] = diarisationProvider;
            }

            // Absent means the checkpoint's own batch, which is the shipped behaviour and not a
            // number — so there is nothing to write when nobody has chosen.
            if (settings.DiarisationBatchSize is { } diarisationBatch)
            {
                values["diarisationBatchSize"] = diarisationBatch;
            }

            var json = JsonSerializer.Serialize(values);

            // Written beside the target and moved into place, rather than over it.
            // File.WriteAllText truncates first, so a process that dies between the truncate and the
            // flush leaves an empty file — and Load turns an unreadable file into the shipped
            // defaults, which would silently switch the update check back on. This application has a
            // documented way of dying abruptly (gotcha 19), so that is not a hypothetical.
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, Path, overwrite: true);
            return true;
        }
#pragma warning disable CA1031 // A read-only profile must not stop the application working.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// The stored backend name, or null for absent, unreadable or unrecognised.
    /// </summary>
    /// <remarks>
    /// An unknown name — a future backend read by an older build, or a hand-edited file — becomes
    /// null and therefore "pick the best one present", rather than throwing or defaulting to CPU.
    /// Same rule as the rest of this file: an unreadable setting degrades to as-shipped.
    /// </remarks>
    private static ComputeBackend? ReadBackend(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("backend", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "cpu" => ComputeBackend.Cpu,
            "vulkan" => ComputeBackend.Vulkan,
            "cuda" => ComputeBackend.Cuda,
            _ => null,
        };
    }

    /// <summary>
    /// The diariser provider names this window offers, lowercase, <c>auto</c> first.
    /// </summary>
    /// <remarks>
    /// <b>A presentation subset, not the protocol's vocabulary.</b> The sidecar is the authority and
    /// accepts <c>torch</c> as well, which is what <c>auto</c> already resolves to and would be a
    /// second spelling of the same choice in a menu.
    /// <para>
    /// <b><c>webgpu</c> left this list on 2026-08-27 and came back on 2026-08-28.</b> It was here
    /// for the ONNX diariser, which is now in <c>attic/sortformer/</c>; the torch pipeline that
    /// replaced it refused the name, so a stored <c>webgpu</c> would have failed every
    /// speaker-labelling run with no row in the picker to clear it from. What brings it back is
    /// that the two neural stages now have an ONNX export, so the name selects something again.
    /// <para>
    /// <b>The hazard that removal was guarding against is still real and is handled elsewhere.</b>
    /// The graphs are a derived artefact and a machine may not have them, so being in this list is
    /// only permission to store the word — the Settings window offers the row against
    /// <c>DiariserGraphs.AreInstalled</c>, and a stored <c>webgpu</c> whose graphs have since been
    /// deleted shows the row with its one-time setup offered again rather than failing at load.
    /// <c>dml</c> is deliberately still absent: it is reachable from the command line, where an
    /// unmeasured provider belongs, and nothing has been run on it.
    /// </para>
    /// A name arriving here that is not in this list is refused by the sidecar rather than silently
    /// accepted, so the cost of this list being short is a clear failure and never a wrong answer.
    /// </remarks>
    public static IReadOnlyList<string> DiarisationProviders { get; } = ["auto", "cpu", "cuda", "webgpu"];

    /// <summary>
    /// The batch sizes this window offers for the diariser. Empty entry is the model's own.
    /// </summary>
    /// <remarks>
    /// The three the sweep of 2026-08-26 covered, and the reason not to offer arbitrary numbers:
    /// these are the only sizes at which anything has been observed at all. The sweep was
    /// DiariZen's — on the pyannote pipeline nothing has been observed at any of them, which the
    /// picker's own explanation says.
    /// </remarks>
    public static IReadOnlyList<int> DiarisationBatchSizes { get; } = [8, 16, 32];

    /// <summary>The stored diariser provider, or null for absent, unreadable or unrecognised.</summary>
    /// <remarks>
    /// <c>auto</c> in the file reads back as null, because absent and "automatic" are the same
    /// state and this file keeps one shape per state — the rule <see cref="Save"/> follows when it
    /// omits the key rather than writing the word.
    /// </remarks>
    private static string? ReadDiarisationProvider(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("diarisationProvider", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var name = value.GetString()?.Trim().ToLowerInvariant();
        return name is not null && name is not "auto" && DiarisationProviders.Contains(name)
            ? name
            : null;
    }

    /// <summary>The stored diariser batch size, or null for absent, unreadable or out of range.</summary>
    /// <remarks>
    /// Checked against the offered sizes rather than merely against "is a positive number": a
    /// hand-edited 512 would be accepted by the sidecar and would ask a 16 GB machine for a working
    /// set it cannot produce, which fails somewhere far less legible than here.
    /// </remarks>
    private static int? ReadDiarisationBatchSize(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("diarisationBatchSize", out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var size))
        {
            return null;
        }

        return DiarisationBatchSizes.Contains(size) ? size : null;
    }

    /// <summary>
    /// The stored ask mode, falling back to the boolean this setting replaced.
    /// </summary>
    /// <remarks>
    /// <c>askWholeTranscript</c> was the 2026-08-25 shipped shape and lived for one day. A stored
    /// <c>true</c> was a deliberate choice and is honoured as the fixed whole-transcript setting;
    /// a stored <c>false</c> carries no choice at all — it was the default nobody had to touch —
    /// so it becomes <see cref="AskModePreference.Automatic"/> rather than pinning a user to
    /// retrieval on the strength of a value they never set.
    /// </remarks>
    /// <summary>
    /// The evidence depth, or the shipped default when the file says nothing this build knows.
    /// An unreadable value becomes <see cref="AskEvidenceDepth.Thorough"/> rather than a faster
    /// one: the settings whose recall is unmeasured are chosen deliberately or not at all.
    /// </summary>
    private static AskEvidenceDepth ReadAskEvidence(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("askEvidence", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return AppSettings.Default.AskEvidence;
        }

        return value.GetString()?.ToLowerInvariant() switch
        {
            "thorough" => AskEvidenceDepth.Thorough,
            "balanced" => AskEvidenceDepth.Balanced,
            "fast" => AskEvidenceDepth.Fast,
            _ => AppSettings.Default.AskEvidence,
        };
    }

    private static AskModePreference ReadAskMode(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("askMode", out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?.Trim().ToLowerInvariant() switch
            {
                "automatic" => AskModePreference.Automatic,
                "retrieval" => AskModePreference.Retrieval,
                "wholetranscript" => AskModePreference.WholeTranscript,
                _ => AppSettings.Default.AskMode,
            };
        }

        return ReadBool(root, "askWholeTranscript", false)
            ? AskModePreference.WholeTranscript
            : AppSettings.Default.AskMode;
    }

    /// <summary>
    /// The stored expert placement. Absent, unreadable or a name this build does not know all
    /// degrade to as-shipped, exactly as the backend and the ask mode do — a future name read by
    /// an older build must not pin anyone to a placement they never chose.
    /// </summary>
    private static MoeExpertPlacement ReadExpertPlacement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("askExpertPlacement", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return AppSettings.Default.AskExpertPlacement;
        }

        return value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "automatic" => MoeExpertPlacement.Automatic,
            "device" => MoeExpertPlacement.Device,
            "systemmemory" => MoeExpertPlacement.SystemMemory,
            _ => AppSettings.Default.AskExpertPlacement,
        };
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } read
            ? read
            : null;

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;
}
