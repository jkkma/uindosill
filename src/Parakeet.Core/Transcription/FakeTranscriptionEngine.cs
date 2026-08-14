using Parakeet.Core.Segmentation;

namespace Parakeet.Core.Transcription;

public sealed record FakeEngineOptions
{
    public static FakeEngineOptions Default { get; } = new();

    /// <summary>Canned text, one phrase per segment, cycled.</summary>
    public IReadOnlyList<string> Phrases { get; init; } =
    [
        "the quick brown fox jumps over the lazy dog",
        "so anyway that was roughly the shape of it",
        "we should probably look at the numbers again before Friday",
        "right, and then the second thing was the migration",
    ];

    /// <summary>Fixed delay per decoded segment.</summary>
    public TimeSpan PerSegmentDelay { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// When above zero, each segment additionally takes this fraction of its own duration,
    /// so a test can simulate a slow machine without hardcoding wall-clock times.
    /// </summary>
    public double SimulatedRealTimeFactor { get; init; }

    public TimeSpan LoadDelay { get; init; } = TimeSpan.Zero;

    public bool EmitWordTimestamps { get; init; } = true;

    /// <summary>Segment index at which to throw, for exercising continue-on-error paths.</summary>
    public int? FailAtSegmentIndex { get; init; }

    /// <summary>Throw from <see cref="ITranscriptionEngine.LoadAsync"/> instead of loading.</summary>
    public bool FailOnLoad { get; init; }

    /// <summary>Return empty text for every segment, as a silent recording would.</summary>
    public bool ReturnEmptyText { get; init; }
}

/// <summary>
/// An engine that produces canned text on the real pipeline: real reading, real segmentation,
/// real progress, real cancellation, no model.
/// </summary>
/// <remarks>
/// Built early on purpose. Without it nothing downstream of the engine is testable until a
/// 670 MB file is in place, and CI can never exercise the application end to end — which is
/// how a UI ships with a job queue nobody has ever run.
/// </remarks>
public sealed class FakeTranscriptionEngine : SegmentingTranscriptionEngine
{
    private readonly FakeEngineOptions _options;
    private bool _loaded;

    public FakeTranscriptionEngine(FakeEngineOptions? options = null)
    {
        _options = options ?? FakeEngineOptions.Default;
        if (_options.Phrases.Count == 0)
        {
            throw new ArgumentException("The fake engine needs at least one phrase.", nameof(options));
        }
    }

    public int LoadCount { get; private set; }

    public int DecodedSegmentCount { get; private set; }

    public override EngineCapabilities Capabilities { get; } = new()
    {
        EngineName = "fake",
        ModelId = "fake-model",
        Backend = ComputeBackend.Cpu,
        SupportsWordTimestamps = true,
        SupportsBatchDecode = true,
        SupportsLanguageSelection = true,
        SupportsDecodeCancellation = false,
        MaxSingleDecodeLength = TimeSpan.FromSeconds(30),
    };

    public override async ValueTask LoadAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return;
        }

        if (_options.LoadDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.LoadDelay, ct).ConfigureAwait(false);
        }

        if (_options.FailOnLoad)
        {
            throw new InvalidOperationException("Fake engine was configured to fail on load.");
        }

        LoadCount++;
        _loaded = true;
    }

    protected override async ValueTask<IReadOnlyList<DecodedSegment>> DecodeAsync(
        IReadOnlyList<AudioSegment> batch,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        var results = new List<DecodedSegment>(batch.Count);

        foreach (var segment in batch)
        {
            ct.ThrowIfCancellationRequested();

            if (_options.FailAtSegmentIndex == segment.Index)
            {
                throw new InvalidOperationException($"Fake engine was configured to fail at segment {segment.Index}.");
            }

            var delay = _options.PerSegmentDelay;
            if (_options.SimulatedRealTimeFactor > 0)
            {
                delay += segment.Duration * _options.SimulatedRealTimeFactor;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            DecodedSegmentCount++;
            results.Add(_options.ReturnEmptyText
                ? new DecodedSegment { Text = string.Empty }
                : Compose(segment));
        }

        return results;
    }

    private DecodedSegment Compose(AudioSegment segment)
    {
        var phrase = _options.Phrases[Math.Abs(segment.Index) % _options.Phrases.Count];

        if (!_options.EmitWordTimestamps)
        {
            return new DecodedSegment { Text = phrase };
        }

        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var slice = segment.Duration / Math.Max(1, words.Length);
        var timed = new List<TranscriptWord>(words.Length);

        for (var i = 0; i < words.Length; i++)
        {
            var start = slice * i;

            // Deterministic pseudo-confidence: stable across runs so assertions can be exact,
            // varied enough that low-confidence filtering has something to find.
            var confidence = 0.72f + (((segment.Index * 7) + (i * 13)) % 27) / 100f;

            timed.Add(new TranscriptWord
            {
                Text = words[i],
                Start = start,
                End = start + (slice * 0.9),
                Confidence = confidence,
            });
        }

        return new DecodedSegment { Text = phrase, Words = timed };
    }
}
