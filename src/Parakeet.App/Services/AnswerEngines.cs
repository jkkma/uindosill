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
/// parser and the real validator against the canned engine, without a nine-gigabyte model in CI.
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
/// The model comes from a file the user put there rather than from a catalogue entry, and that
/// is deliberate: the decision register's model question is open until the CSB384 measurements
/// run, so nothing is recommended, nothing is downloaded on this application's advice — but a
/// person with a model file of their own is not made to wait for that. Where several files are
/// present the largest is served, and the answer's model line names it, so nothing about the
/// pick is hidden.
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
                    + "folder — the About window shows where that is — and come back here.",
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
                "The language model file is gone from the models folder. Put a .gguf file back — "
                + "the About window shows where the folder is.");

        return new LlamaServerAnswerEngine(new LlamaServerOptions
        {
            ModelPath = model,
            ThinkBeforeAnswer = ThinkingMode,
            ContextSize = AnswerContextBudget.ContextTokensFor(promptChars),
            ExpertPlacement = ExpertPlacement,

            // A drafting head is used when one is sitting beside the model, and there is no
            // setting for it: it is faster at the same answer rather than a different trade, so
            // there is nothing for a person to weigh. Measured 2026-08-27 on the second machine —
            // 1.32x on decode at 71.7% acceptance, citation checks unchanged (docs/UNPROVEN.md).
            // Absent, this is null and the child decodes one token at a time as before.
            DraftModelPath = DraftModelLocator.FindBeside(model),
        });
    }

    /// <summary>
    /// The .gguf to serve: the one chosen in Settings when it is still there, and otherwise the
    /// largest present.
    /// </summary>
    /// <remarks>
    /// Largest rather than newest for the unchosen case, because it is the pick a person can
    /// predict without asking — but predicting it is not the same as choosing it, which is why
    /// the picker exists as of 2026-08-25. A chosen name that no longer matches a file falls
    /// back silently to the largest: the models folder is not this application's alone, and a
    /// panel that refused to answer because a file someone deleted was once selected would be a
    /// setting failing loudly at the wrong person.
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

        return files.FirstOrDefault()?.FullName;
    }
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

    public AnswerEngineAvailability Check() => new()
    {
        WhyNot = _whyNot,
        ModelFileName = _whyNot is null ? "fake-answer-model.gguf" : null,
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
