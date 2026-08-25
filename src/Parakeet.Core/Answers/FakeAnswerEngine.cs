using System.Runtime.CompilerServices;
using Parakeet.Core.Retrieval;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Answers;

public sealed record FakeAnswerOptions
{
    public static FakeAnswerOptions Default { get; } = new();

    /// <summary>Answer <c>NOT_IN_TRANSCRIPT</c> to everything, whatever the evidence says.</summary>
    public bool AlwaysAbstain { get; init; }

    /// <summary>Stream nothing at all — neither bullets nor the sentinel — so a caller's
    /// empty-output path is exercised: silence must never render as an abstention.</summary>
    public bool ProduceNothing { get; init; }

    /// <summary>Append the bullet that admits it has no anchor — the <c>[?]</c> case.</summary>
    public bool IncludeUncitedBullet { get; init; } = true;

    /// <summary>Fixed delay per streamed chunk, so a test can watch an answer arrive.</summary>
    public TimeSpan PerChunkDelay { get; init; } = TimeSpan.Zero;

    public TimeSpan LoadDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Hold <see cref="IAnswerEngine.LoadAsync"/> open until this completes, so a test
    /// can interleave other work with a load in flight — the window a cold load really has.</summary>
    public Task? LoadGate { get; init; }

    /// <summary>Throw from <see cref="IAnswerEngine.LoadAsync"/> instead of loading.</summary>
    public bool FailOnLoad { get; init; }

    /// <summary>Throw after this many chunks have been yielded, for mid-stream failure paths.</summary>
    public int? FailAfterChunks { get; init; }
}

/// <summary>
/// An engine that produces grammar-shaped answers citing real segments, with no model: the
/// counterpart of <see cref="FakeTranscriptionEngine"/>, and what makes the chat panel and its
/// tests buildable before a single native is vendored.
/// </summary>
/// <remarks>
/// The behaviour it fakes is the honest one, not the convenient one: it abstains on an empty
/// transcript and on empty evidence in every mode, because those are the paths the register's
/// decision 6 requires an answer to have, and a fake that always answered would let a panel ship
/// with no abstain state. Its citations come from the evidence it was handed, so everything it
/// says passes <see cref="CitationValidator"/> against the same transcript — and the one bullet
/// it marks <c>[?]</c> is there so a renderer's uncited state is exercised too. The v1 lesson
/// stands: a fake more forgiving than the device let two real defects through, so this one
/// refuses evidence that does not belong to the transcript it was asked about.
/// </remarks>
public sealed class FakeAnswerEngine : IAnswerEngine
{
    private readonly FakeAnswerOptions _options;
    private bool _loaded;

    public FakeAnswerEngine(FakeAnswerOptions? options = null) => _options = options ?? FakeAnswerOptions.Default;

    public int LoadCount { get; private set; }

    public int AskCount { get; private set; }

    /// <summary>The last request asked, so a test can see what a caller really sent.</summary>
    public AskRequest? LastRequest { get; private set; }

    public AnswerEngineCapabilities Capabilities { get; } = new()
    {
        EngineName = "fake",
        ModelId = "fake-answer-model",
        Backend = ComputeBackend.Cpu,
        Quantisation = "none",
        SupportsGrammar = true,
        TrainedContextTokens = 40_960,
    };

    public async ValueTask LoadAsync(CancellationToken ct = default)
    {
        if (_options.FailOnLoad)
        {
            throw new InvalidOperationException("The fake was told to fail its load.");
        }

        if (_options.LoadDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.LoadDelay, ct).ConfigureAwait(false);
        }

        if (_options.LoadGate is { } gate)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }

        _loaded = true;
        LoadCount++;
    }

    public async IAsyncEnumerable<string> AskAsync(
        AskRequest request,
        IProgress<AskProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_loaded)
        {
            throw new InvalidOperationException("Ask before load. Call LoadAsync first, as the real engine requires.");
        }

        foreach (var window in request.Evidence)
        {
            if (window.FirstSegment < 1 || window.LastSegment > request.Transcript.Segments.Count)
            {
                throw new ArgumentException(
                    $"Evidence window {window.CitationId} does not exist in a transcript of " +
                    $"{request.Transcript.Segments.Count} segments. A fake that answered anyway would " +
                    "hide exactly the mismatch the citation rule exists to catch.",
                    nameof(request));
            }
        }

        AskCount++;
        LastRequest = request;

        // The prefill is the wait a real engine makes the caller sit through; the fake reports
        // it completing so a panel's progress state is exercised rather than skipped.
        var prompt = 100 + request.Evidence.Sum(w => SearchTokenizer.Tokenize(w.Text).Count);
        progress?.Report(new AskProgress { PrefillTokens = 0, PrefillTotalTokens = prompt });
        progress?.Report(new AskProgress { PrefillTokens = prompt, PrefillTotalTokens = prompt });

        var generated = 0;
        foreach (var chunk in Answer(request))
        {
            ct.ThrowIfCancellationRequested();
            if (_options.PerChunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.PerChunkDelay, ct).ConfigureAwait(false);
            }

            if (_options.FailAfterChunks is { } cap && generated >= cap)
            {
                throw new InvalidOperationException("The fake was told to die mid-answer.");
            }

            generated++;
            progress?.Report(new AskProgress
            {
                PrefillTokens = prompt,
                PrefillTotalTokens = prompt,
                GeneratedTokens = generated,
            });

            yield return chunk;
        }
    }

    private IEnumerable<string> Answer(AskRequest request)
    {
        if (_options.ProduceNothing)
        {
            yield break;
        }

        // Empty evidence abstains whatever the mode: the whole-transcript path hands over
        // windows tiling the recording, it does not hand over nothing — and a fake that filled
        // the gap itself would be more forgiving than the real engine, which is how two v1
        // defects got through.
        if (_options.AlwaysAbstain
            || request.Transcript.IsEmpty
            || request.Evidence.Count == 0)
        {
            yield return AnswerParser.AbstainSentinel;
            yield return "\n";
            yield break;
        }

        var evidence = request.Evidence;

        // The overview opens with the framing sentence its prompt asks for, cited to where the
        // recording starts — so the panel's lead, its chips and its copy text are all exercised
        // without a model, and a fake that skipped it would let the shape ship untested.
        if (request.Mode == AnswerMode.WholeTranscript)
        {
            yield return "This recording covers several things ";
            yield return $"[{evidence[0].CitationId}]\n";
        }

        // One bullet per evidence window, citing the window's own run and quoting its first
        // words verbatim — so the quote check has something true to verify.
        var labels = new[] { "First", "Second", "Third", "Fourth", "Fifth" };
        for (var i = 0; i < evidence.Count && i < labels.Length; i++)
        {
            var window = evidence[i];
            var words = SearchTokenizer.Tokenize(window.Text);
            var quote = string.Join(' ', words.Take(4));

            yield return $"- {labels[i]}: the recording covers this ";
            yield return $"«{quote}» ";
            yield return $"[{window.CitationId}]\n";
        }

        if (_options.IncludeUncitedBullet)
        {
            yield return "- Something the model could not anchor [?]\n";
        }
    }

    public ValueTask DisposeAsync()
    {
        _loaded = false;
        return ValueTask.CompletedTask;
    }
}
