using Parakeet.Core.Answers;
using Parakeet.Core.Models;
using Parakeet.Engine.LlamaServer;

namespace Parakeet.App.Services;

/// <summary>
/// Whether the Ask panel can have a language model right now, and the sentence the panel shows
/// when it cannot. Both facts in one answer, because the panel that reads them must never say
/// "unavailable" without saying why — a covered control that does not explain itself reads as
/// broken rather than as unbuilt.
/// </summary>
public sealed record AnswerEngineAvailability
{
    public bool IsAvailable => WhyNot is null;

    /// <summary>The panel's notice when the tier is unavailable, or null when it is not.</summary>
    public string? WhyNot { get; init; }

    /// <summary>The model file an ask would load, when one was found.</summary>
    public string? ModelFileName { get; init; }
}

/// <summary>
/// Turns the size of an ask's prompt into the context the engine's child is started with. One
/// place, because two callers use it for two halves of one decision: the provider sizes the
/// engine it creates, and the panel compares against the engine it holds to know when a rebuild
/// is due. Retrieval's ~2k-token evidence always lands on <see cref="Minimum"/> — the default the
/// engine has always run at — and only the whole-transcript path grows past it, paying its KV
/// cost per recording rather than at the largest transcript anyone might ever open.
/// </summary>
public static class AnswerContextBudget
{
    /// <summary>The retrieval tier's context, and the floor for everything else.</summary>
    public const int Minimum = 16_384;

    /// <summary>
    /// The context, in tokens, for a prompt of roughly this many characters. Four characters
    /// per token is the same estimate the engine's own overflow guard uses, so a context sized
    /// here always passes that guard; the quarter margin covers languages that tokenize denser
    /// than English (an estimate, unmeasured across the 25 — docs/UNPROVEN.md); the flat
    /// allowance covers the instruction, the template's own tokens and the full
    /// answer-plus-thinking generation budget; and the result lands on a 4,096 boundary because
    /// a KV cache is allocated in big pieces either way.
    /// </summary>
    public static int ContextTokensFor(int promptChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(promptChars);
        var estimated = promptChars / 4;
        var needed = estimated + (estimated / 4) + 4_096;
        var rounded = (needed + 4_095) / 4_096 * 4_096;
        return Math.Max(Minimum, rounded);
    }
}

/// <summary>
/// Creates the engine behind the Ask panel — the same seam <see cref="IEngineProvider"/> is for
/// transcription, for the same reason: the headless tests exercise the real panel, the real
/// parser and the real validator against the canned engine, without multi-gigabyte answering
/// weights in CI.
/// </summary>
public interface IAnswerEngineProvider
{
    /// <summary>Cheap and side-effect free; the panel calls it whenever its state could have changed.</summary>
    AnswerEngineAvailability Check();

    /// <summary>
    /// Whether the next <see cref="Create"/> builds a think-before-answering engine. The panel
    /// reads it before every ask and drops an engine built the other way, so flipping the
    /// setting takes effect at the next question rather than at the next restart.
    /// </summary>
    bool ThinkingMode { get; }

    /// <summary>
    /// Where the next ask draws from, or that the question should decide — the register's
    /// decision 3, read by the panel before every question exactly as <see cref="ThinkingMode"/>
    /// is. Unlike thinking, the mode is a per-request fact rather than a child-process argument:
    /// changing it forces a fresh engine only when the context the new ask needs differs from
    /// the one the held engine was built with.
    /// </summary>
    AskModePreference ModePreference { get; }

    /// <summary>
    /// Where a mixture's experts go, read by the panel before every ask exactly as
    /// <see cref="ThinkingMode"/> is, and for the same reason: it is a child-process
    /// environment, so a changed setting can only take effect through a fresh child.
    /// </summary>
    MoeExpertPlacement ExpertPlacement { get; }

    /// <summary>
    /// How many retrieved windows the next ask should show the model, read by the panel before
    /// every question. A per-request fact like <see cref="ModePreference"/> and not a
    /// child-process argument, so changing it needs no new engine — only a different context
    /// size, which <see cref="Create"/> already sizes per prompt.
    /// </summary>
    int EvidenceWindows { get; }

    /// <summary>
    /// A new engine, not yet loaded, sized for a prompt of roughly <paramref name="promptChars"/>
    /// characters per <see cref="AnswerContextBudget.ContextTokensFor"/>. Throws when
    /// <see cref="Check"/> says unavailable.
    /// </summary>
    IAnswerEngine Create(int promptChars = 0);
}

/// <summary>
/// The real tier: <c>llama-server</c> from the vendored drop, serving whatever GGUF file is in
/// the models folder.
/// </summary>
/// <remarks>
/// <para>
/// A model file the user put there works as well as one a catalogue entry installed, and that
/// stays deliberate: the folder is not this application's alone, and somebody with weights of
/// their own is not made to use ours.
/// </para>
/// <para>
/// What changed on 2026-08-28 is that there is now an answer to "which one, if you have not
/// said". The CSB384 measurements the register was waiting on have run, against the labelled
/// thirty-question set, and the catalogue marks one answering entry as the default
/// (<see cref="ModelCatalog.RecommendedAnswering"/>). Installed, it is served; absent, the
/// largest file present is served as before. Either way the answer's model line names what
/// answered, so nothing about the pick is hidden.
/// </para>
/// </remarks>
public sealed class LlamaAnswerEngineProvider : IAnswerEngineProvider
{
    private readonly IModelStore _store;
    private readonly Func<bool> _thinkingMode;
    private readonly Func<AskModePreference> _modePreference;
    private readonly Func<string?> _chosenModel;
    private readonly Func<MoeExpertPlacement> _expertPlacement;
    private readonly Func<int> _evidenceWindows;

    public LlamaAnswerEngineProvider(
        IModelStore store,
        Func<bool>? thinkingMode = null,
        Func<AskModePreference>? modePreference = null,
        Func<string?>? chosenModel = null,
        Func<MoeExpertPlacement>? expertPlacement = null,
        Func<int>? evidenceWindows = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _thinkingMode = thinkingMode ?? (static () => false);
        _modePreference = modePreference ?? (static () => AskModePreference.Automatic);
        _chosenModel = chosenModel ?? (static () => null);
        _expertPlacement = expertPlacement ?? (static () => MoeExpertPlacement.Automatic);

        // Eight when nobody says otherwise: the depth every citation figure in this project was
        // measured at, and the one whose recall is not in question.
        _evidenceWindows = evidenceWindows ?? (static () => 8);
    }

    /// <summary>
    /// Which model file would answer the next question, or null when the folder holds none.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Check"/> on purpose, and it is a distinction with teeth: Check
    /// answers "can this panel work at all", which is false on a build with no vendored
    /// <c>llama-server</c> whatever is in the models folder, and it returns before it ever looks
    /// at one. Asking it which file would be served therefore also asks whether this machine has
    /// the natives — two questions in one answer, and a test written against it passes or fails
    /// on the tester's vendored drop rather than on the code.
    /// </remarks>
    public string? ResolveModelFileName() => Path.GetFileName(FindModelFile());

    /// <summary>
    /// The .gguf files in the models folder, largest first — the list the picker offers, read
    /// fresh because the folder is not this application's alone to write.
    /// </summary>
    public IReadOnlyList<string> AvailableModelFileNames() =>
        [.. ModelFilesOnDisk().Select(file => file.Name)];

    /// <summary>
    /// Every .gguf a person could ask questions of, largest first: the ones dropped into the
    /// models folder by hand, and the ones a catalogue entry installed into a directory of its
    /// own. Drafting heads are not among them — they answer nothing.
    /// </summary>
    /// <remarks>
    /// One level down and no further, because that is the shape the catalogue writes: an entry
    /// with a <c>files</c> array names a directory to install into, and it must, since the two
    /// answering entries ship the same drafting head under the same name and would otherwise
    /// overwrite each other at the root.
    /// </remarks>
    private IReadOnlyList<FileInfo> ModelFilesOnDisk()
    {
        var root = _store.RootDirectory;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var found = new List<FileInfo>();
        foreach (var directory in Directory.EnumerateDirectories(root).Prepend(root))
        {
            try
            {
                found.AddRange(Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
                    .Where(path => !DraftModelLocator.IsDraftHead(path))
                    .Where(path => !IsAnotherJobsWeights(Path.GetFileName(path)))
                    .Select(path => new FileInfo(path)));
            }
            catch (IOException)
            {
                // A directory that vanished between listing and reading is one fewer model, not a
                // panel that stops working.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return [.. found.OrderByDescending(file => file.Length)];
    }

    /// <summary>
    /// Whether this file name is one the catalogue installs for some job other than answering —
    /// the recogniser's weights above all, which are a <c>.gguf</c> in the same folder and can
    /// answer nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **By name against the catalogue, and only against the catalogue.** A file nobody's manifest
    /// has heard of is still offered, because the models folder is not this application's alone
    /// and refusing someone's own weights on a guess about their contents would be the worse
    /// error. What is excluded is the narrow, certain case: this application put that file there,
    /// for a job that is not this one.
    /// </para>
    /// <para>
    /// Added 2026-08-28, when the answering default stopped being "the largest file present".
    /// The old rule hid this — a 1.34 GiB recogniser never outweighed a 12.66 GiB answering model,
    /// so it could not be reached by accident. It was still in the picker, where choosing it set
    /// <see cref="AppSettings.AskModelFileName"/> to weights that cannot serve an answer and bought
    /// a load failure instead of a refusal.
    /// </para>
    /// <para>
    /// A name that some answering entry also claims is not excluded. The question this asks is who
    /// owns the name, and a name two entries share is not evidence against it.
    /// </para>
    /// </remarks>
    private static bool IsAnotherJobsWeights(string fileName) =>
        OtherJobsFileNames.Contains(fileName);

    /// <inheritdoc cref="IsAnotherJobsWeights"/>
    private static readonly HashSet<string> OtherJobsFileNames = BuildOtherJobsFileNames();

    private static HashSet<string> BuildOtherJobsFileNames()
    {
        var catalogue = ModelCatalog.Default;

        var answering = catalogue.AnsweringModels
            .SelectMany(model => model.Files)
            .Select(file => file.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ToHashSet with the comparer, not a collection expression: case is not a distinction a
        // Windows file name makes, and a set built without the comparer would miss the same file
        // spelled differently.
        return catalogue.Models
            .Where(model => model.Task != ModelTask.Answering)
            .SelectMany(model => model.Files)
            .Select(file => file.FileName)
            .Where(name => !answering.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool ThinkingMode => _thinkingMode();

    public AskModePreference ModePreference => _modePreference();

    /// <inheritdoc />
    public int EvidenceWindows => _evidenceWindows();

    public MoeExpertPlacement ExpertPlacement => _expertPlacement();

    public AnswerEngineAvailability Check()
    {
        if (LlamaServerLocator.TryFind() is null)
        {
            return new AnswerEngineAvailability
            {
                WhyNot = "Asking needs the language-model engine, and this build does not include it.",
            };
        }

        if (FindModelFile() is not { } model)
        {
            return new AnswerEngineAvailability
            {
                WhyNot = "Asking needs a language model. Put a model file (.gguf) into the models "
                    + "folder: the About window shows where that is: and come back here.",
            };
        }

        return new AnswerEngineAvailability { ModelFileName = Path.GetFileName(model) };
    }

    public IAnswerEngine Create(int promptChars = 0)
    {
        // Reachable when the file was deleted between Check() and the ask; the message lands in
        // the chat verbatim, so it is user copy, not a developer's assertion.
        var model = FindModelFile()
            ?? throw new InvalidOperationException(
                "The language model file is gone from the models folder. Put a .gguf file back, "
                + "the About window shows where the folder is.");

        return new LlamaServerAnswerEngine(new LlamaServerOptions
        {
            ModelPath = model,
            ThinkBeforeAnswer = ThinkingMode,
            ContextSize = AnswerContextBudget.ContextTokensFor(promptChars),
            ExpertPlacement = ExpertPlacement,

            // A drafting head is used when one is sitting beside the model, and there is no
            // setting for it: it is faster at an answer that passes the same checks, so there is
            // nothing for a person to weigh. Measured 2026-08-27 on the second machine — 1.32x on
            // decode at 71.7% acceptance, every citation resolving and the adversarial questions
            // abstained from either way (docs/UNPROVEN.md). Not the same answer, though: the head
            // arm verified 16 quotes against 17 and cited 47 spans against 52, and greedy decoding
            // is not byte-identical under drafting or slot batching (docs/GOTCHAS.md, 41), which
            // is why nothing here claims identity, only that the checks held. Absent, this is null
            // and the child decodes one token at a time as before.
            DraftModelPath = DraftModelLocator.FindBeside(model),
        });
    }

    /// <summary>
    /// The .gguf to serve: the one chosen in Settings when it is still there, then the
    /// catalogue's own answering default when that is installed, and otherwise the largest
    /// present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The middle step arrived 2026-08-28 with a model that made the largest-file rule wrong.
    /// That rule was chosen because it is the pick a person can predict without asking, and it
    /// held while the only answering entries were one model at two quantisations. It stopped
    /// holding when a 6.25 GiB dense 12B matched the 12.66 GiB mixture beside it — same median
    /// wall, same grounding, same abstentions, fewer failed citations, at half the memory and
    /// running entirely on the graphics chip (docs/UNPROVEN.md, 2026-08-28). "The biggest thing
    /// installed" would serve the 26B to everyone who installed both and never said which, which
    /// is now the worse answer rather than the safe one.
    /// </para>
    /// <para>
    /// It is still the catalogue that decides, not this method: the entry carries the flag, and
    /// an entry that is not installed does not win. A person who has only ever dropped a file
    /// into the folder by hand reaches the largest-file rule exactly as before.
    /// </para>
    /// <para>
    /// A chosen name that no longer matches a file falls through both: the models folder is not
    /// this application's alone, and a panel that refused to answer because a file someone
    /// deleted was once selected would be a setting failing loudly at the wrong person.
    /// </para>
    /// </remarks>
    private string? FindModelFile()
    {
        var files = ModelFilesOnDisk();

        if (_chosenModel() is { Length: > 0 } chosen)
        {
            var match = files.FirstOrDefault(
                file => string.Equals(file.Name, chosen, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.FullName;
            }
        }

        if (CatalogueDefaultFileName() is { Length: > 0 } preferred)
        {
            var match = files.FirstOrDefault(
                file => string.Equals(file.Name, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.FullName;
            }
        }

        return files.FirstOrDefault()?.FullName;
    }

    /// <summary>
    /// The weights of the catalogue's recommended answering entry, by file name, or null when no
    /// entry claims the position. The drafting head is skipped: it is a file of the same entry
    /// and answers nothing.
    /// </summary>
    private static string? CatalogueDefaultFileName() =>
        ModelCatalog.Default.RecommendedAnswering?.Files
            .Select(file => file.FileName)
            .FirstOrDefault(name => !DraftModelLocator.IsDraftHead(name));
}

/// <summary>The canned tier, so the panel's whole life is testable with no server and no file.</summary>
public sealed class FakeAnswerEngineProvider : IAnswerEngineProvider
{
    private readonly FakeAnswerOptions? _options;
    private readonly string? _whyNot;

    public FakeAnswerEngineProvider(FakeAnswerOptions? options = null, string? whyNot = null)
    {
        _options = options;
        _whyNot = whyNot;
    }

    public int Created { get; private set; }

    public FakeAnswerEngine? LastCreated { get; private set; }

    /// <summary>Settable so the panel's mode-flip behaviour is testable without a server.</summary>
    public bool ThinkingMode { get; set; }

    /// <summary>Settable for the same reason as <see cref="ThinkingMode"/>.</summary>
    public AskModePreference ModePreference { get; set; } = AskModePreference.Automatic;

    /// <summary>Settable for the same reason as <see cref="ThinkingMode"/>.</summary>
    public int EvidenceWindows { get; set; } = 8;

    /// <summary>Settable for the same reason as <see cref="ThinkingMode"/>.</summary>
    public MoeExpertPlacement ExpertPlacement { get; set; } = MoeExpertPlacement.Automatic;

    /// <summary>What the panel said the last engine's prompt would roughly measure.</summary>
    public int LastPromptChars { get; private set; }

    /// <summary>Settable for the same reason as <see cref="ThinkingMode"/>: the panel drops a
    /// held engine when the picked model changes, and that behaviour needs driving without a
    /// models folder.</summary>
    public string ModelFileName { get; set; } = "fake-answer-model.gguf";

    public AnswerEngineAvailability Check() => new()
    {
        WhyNot = _whyNot,
        ModelFileName = _whyNot is null ? ModelFileName : null,
    };

    public IAnswerEngine Create(int promptChars = 0)
    {
        if (_whyNot is not null)
        {
            throw new InvalidOperationException(_whyNot);
        }

        Created++;
        LastPromptChars = promptChars;
        LastCreated = new FakeAnswerEngine(_options);
        return LastCreated;
    }
}
