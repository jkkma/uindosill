using Parakeet.Core.Text;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tidying;

public sealed record FakeTidierOptions
{
    public static FakeTidierOptions Default { get; } = new();

    /// <summary>Fixed delay per line, for exercising the backlog and cancellation.</summary>
    public TimeSpan PerLineDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Throw from <see cref="ITranscriptTidier.LoadAsync"/> instead of loading.</summary>
    public bool FailOnLoad { get; init; }

    /// <summary>Load, then throw from every line — the shape of a child that died mid-file.</summary>
    public bool FailOnTidy { get; init; }

    /// <summary>
    /// Change this word to <see cref="ReplaceWith"/> wherever it occurs, so the contract's
    /// refusal and its door both have something real to judge. Null changes nothing.
    /// </summary>
    public string? Replace { get; init; }

    public string ReplaceWith { get; init; } = "something";

    /// <summary>
    /// Put this word at the front of every rewrite, so an insertion reaches the contract. Null
    /// adds nothing.
    /// </summary>
    public string? Insert { get; init; }

    /// <summary>The backend the fake claims, so provenance can be told from a default.</summary>
    public ComputeBackend Backend { get; init; } = ComputeBackend.Cpu;
}

/// <summary>
/// A tidier that reads no model and tidies anyway: drops the six filler tokens, collapses an
/// immediately repeated word, and capitalises the first letter — every one of them a change the
/// contract admits, so the fake's output is visibly tidied and always accepted unless an option
/// above makes it break the contract on purpose.
/// </summary>
/// <remarks>
/// Mandatory for the same reason the canned translator is: the suite runs with no weights, and
/// without this nothing downstream of the seam — the contract, the stage, the tandem wiring, the
/// <c>.tidy</c> naming, the pane — is testable until a language model is installed. It exercises
/// the real invariants rather than standing beside them: what it returns goes through
/// <see cref="TidyContract"/> like any model's rewrite.
/// </remarks>
public sealed class FakeTranscriptTidier : ITranscriptTidier
{
    private readonly FakeTidierOptions _options;
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();
    private bool _loaded;

    public FakeTranscriptTidier(FakeTidierOptions? options = null)
    {
        _options = options ?? FakeTidierOptions.Default;
        Capabilities = new TidierCapabilities
        {
            EngineName = "fake",
            ModelId = "fake-tidier",
            Backend = _options.Backend,
            Quantisation = "none",
        };
    }

    public TidierCapabilities Capabilities { get; }

    public int LoadCount { get; private set; }

    /// <summary>Every line handed to the fake, in the order the calls arrived.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public ValueTask LoadAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return ValueTask.CompletedTask;
        }

        if (_options.FailOnLoad)
        {
            throw new InvalidOperationException("Fake tidier was configured to fail on load.");
        }

        LoadCount++;
        _loaded = true;
        return ValueTask.CompletedTask;
    }

    public async Task<string> TidyLineAsync(string line, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        await LoadAsync(ct).ConfigureAwait(false);

        lock (_gate)
        {
            _lines.Add(line);
        }

        if (_options.FailOnTidy)
        {
            throw new InvalidOperationException("Fake tidier was configured to fail on every line.");
        }

        if (_options.PerLineDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.PerLineDelay, ct).ConfigureAwait(false);
        }

        return Tidy(line);
    }

    /// <summary>The canned rewrite, exposed so a test can say what the fake will return for a line.</summary>
    public string Tidy(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<string>(words.Length);
        string? previous = null;

        foreach (var word in words)
        {
            var token = TranscriptNormalizer.AlphanumericToken(word);
            if (TranscriptNormalizer.Fillers.Contains(token))
            {
                continue;
            }

            if (token.Length > 0 && token == previous)
            {
                continue;
            }

            var written = _options.Replace is { } from && string.Equals(from, word, StringComparison.OrdinalIgnoreCase)
                ? _options.ReplaceWith
                : word;

            kept.Add(written);
            previous = token.Length > 0 ? token : previous;
        }

        if (_options.Insert is { Length: > 0 } inserted)
        {
            kept.Insert(0, inserted);
        }

        if (kept.Count > 0 && kept[0].Length > 0 && char.IsLower(kept[0][0]))
        {
            kept[0] = char.ToUpperInvariant(kept[0][0]) + kept[0][1..];
        }

        return string.Join(' ', kept);
    }

    public ValueTask DisposeAsync()
    {
        _loaded = false;
        return ValueTask.CompletedTask;
    }
}
