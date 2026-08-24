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
/// Creates the engine behind the Ask panel — the same seam <see cref="IEngineProvider"/> is for
/// transcription, for the same reason: the headless tests exercise the real panel, the real
/// parser and the real validator against the canned engine, without a nine-gigabyte model in CI.
/// </summary>
public interface IAnswerEngineProvider
{
    /// <summary>Cheap and side-effect free; the panel calls it whenever its state could have changed.</summary>
    AnswerEngineAvailability Check();

    /// <summary>A new engine, not yet loaded. Throws when <see cref="Check"/> says unavailable.</summary>
    IAnswerEngine Create();
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

    public LlamaAnswerEngineProvider(IModelStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

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

    public IAnswerEngine Create()
    {
        var model = FindModelFile()
            ?? throw new InvalidOperationException("No .gguf in the models folder; Check() said so.");

        return new LlamaServerAnswerEngine(new LlamaServerOptions { ModelPath = model });
    }

    /// <summary>The largest .gguf in the models folder, or null. Largest rather than newest,
    /// because it is the pick a person can predict without asking.</summary>
    private string? FindModelFile()
    {
        if (!Directory.Exists(_store.RootDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(_store.RootDirectory, "*.gguf", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.Length)
            .FirstOrDefault()?.FullName;
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

    public AnswerEngineAvailability Check() => new()
    {
        WhyNot = _whyNot,
        ModelFileName = _whyNot is null ? "fake-answer-model.gguf" : null,
    };

    public IAnswerEngine Create()
    {
        if (_whyNot is not null)
        {
            throw new InvalidOperationException(_whyNot);
        }

        Created++;
        LastCreated = new FakeAnswerEngine(_options);
        return LastCreated;
    }
}
