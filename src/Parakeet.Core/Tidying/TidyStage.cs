using System.Diagnostics;
using System.Threading.Channels;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tidying;

/// <summary>
/// A stage over the segment stream: segments go in as the recogniser produces them, a few units
/// are in flight against the model at any time, and each segment comes back through the contract
/// as soon as the last unit carrying a piece of it lands — in whatever order the model finishes
/// them.
/// </summary>
/// <remarks>
/// <para>
/// This is the construction the tandem decision (2026-09-01, docs/PHASES.md) named as its cost:
/// "a stage over the segment stream rather than a pass over the finished document; a queue that is
/// never empty, with its backlog visible; and the pipeline failure modes the pass shape did not
/// have". <see cref="Pending"/> is the backlog. <see cref="CompleteAsync"/> is where a failure
/// surfaces, and it surfaces as one exception the caller turns into a pass failure that leaves
/// the transcript whole — nothing here ever holds a segment the recogniser produced.
/// </para>
/// <para>
/// What one request carries is <see cref="TidyOptions.Unit"/>'s to say, through
/// <see cref="TidyUnitShaper"/>: the shipped unit is the joined run (docs/PHASES.md, *Decided
/// 2026-09-03*; the segment until then), and the stage's outcomes are one per segment under every kind — a segment carried in pieces is assembled from them once
/// the last has landed, and refused whole when any of its units was.
/// </para>
/// <para>
/// The pass shape is this stage with every segment enqueued at once
/// (<see cref="TranscriptTidy.TidyAsync"/>), so the two callers run one piece of code.
/// </para>
/// </remarks>
public sealed class TidyStage : IAsyncDisposable
{
    private readonly ITranscriptTidier _tidier;
    private readonly TidyOptions _options;
    private readonly Action<int, TidyOutcome>? _onTidied;
    private readonly Channel<(int Ordinal, TimeSpan EnqueuedAt, TidyUnit Unit)> _queue;
    private readonly List<Task> _workers;
    private readonly List<TidyOutcome?> _outcomes = [];
    private readonly List<TranscriptSegment> _segments = [];
    private readonly List<TidyPieceOutcome?[]?> _pieces = [];
    private readonly List<TidyUnitTrace> _trace = [];
    private readonly TidyUnitShaper _shaper;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop;
    private int _pending;
    private int _units;
    private bool _completed;

    /// <param name="onTidied">
    /// Called with each outcome as it lands, on a worker thread — a window marshals it. The index
    /// is the one <see cref="Enqueue"/> returned.
    /// </param>
    public TidyStage(
        ITranscriptTidier tidier,
        TidyOptions? options = null,
        Action<int, TidyOutcome>? onTidied = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tidier);
        _tidier = tidier;
        _options = options ?? TidyOptions.Default;
        _options.Validate();
        _onTidied = onTidied;
        _shaper = new TidyUnitShaper(_options.Unit);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _queue = Channel.CreateUnbounded<(int, TimeSpan, TidyUnit)>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = _options.Concurrency == 1,
        });

        _workers = new List<Task>(_options.Concurrency);
        for (var i = 0; i < _options.Concurrency; i++)
        {
            _workers.Add(Task.Run(WorkAsync, CancellationToken.None));
        }
    }

    /// <summary>Segments enqueued and not yet through the contract: the backlog the window shows.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>How many segments have gone in.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _outcomes.Count;
            }
        }
    }

    /// <summary>How many requests the stage has made or queued.</summary>
    public int Units => Volatile.Read(ref _units);

    /// <summary>The stage's own clock, started when it was constructed; every time in <see cref="Trace"/> is on it.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>What every request that has landed cost and came to, in landing order.</summary>
    public IReadOnlyList<TidyUnitTrace> Trace
    {
        get
        {
            lock (_gate)
            {
                return [.. _trace];
            }
        }
    }

    /// <summary>
    /// Hands the stage one more segment and returns its index. An empty segment is passed through
    /// untouched without a request; the model has nothing to tidy in it.
    /// </summary>
    public int Enqueue(TranscriptSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        int index;
        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The stage has been completed; nothing more can be enqueued.");
            }

            index = _outcomes.Count;
            _outcomes.Add(null);
            _segments.Add(segment);
            _pieces.Add(null);
        }

        if (segment.IsEmpty)
        {
            Land(index, new TidyOutcome { Accepted = true, Segment = segment });
            return index;
        }

        Interlocked.Increment(ref _pending);

        // The shaper is the single writer's: Enqueue and CompleteAsync are the only callers, and
        // the channel is declared single-writer for the same reason.
        var units = _shaper.Push(index, segment, out var pieces);
        lock (_gate)
        {
            _pieces[index] = new TidyPieceOutcome?[pieces];
        }

        foreach (var unit in units)
        {
            EnqueueUnit(unit);
        }

        return index;
    }

    /// <summary>
    /// Says that nothing more is coming, waits for every unit in flight, and returns every
    /// outcome in enqueue order. Throws the first failure any unit met, after the rest have been
    /// stopped — the caller's pass policy is what turns that into "the transcript, whole".
    /// </summary>
    public async Task<IReadOnlyList<TidyOutcome>> CompleteAsync()
    {
        lock (_gate)
        {
            _completed = true;
        }

        foreach (var unit in _shaper.Flush())
        {
            EnqueueUnit(unit);
        }

        _queue.Writer.TryComplete();

        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch
        {
            // One worker's failure; the others are told to stop rather than left to finish a
            // pass whose result will be thrown away.
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(_workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
#pragma warning disable CA1031 // The first failure is the one reported; a second worker's is noise.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            throw;
        }

        lock (_gate)
        {
            return _outcomes.Select(o => o ?? throw new InvalidOperationException("A segment was never tidied.")).ToList();
        }
    }

    private void EnqueueUnit(TidyUnit unit)
    {
        var ordinal = Interlocked.Increment(ref _units) - 1;
        if (!_queue.Writer.TryWrite((ordinal, _clock.Elapsed, unit)))
        {
            throw new InvalidOperationException("The stage's queue refused a unit.");
        }
    }

    private async Task WorkAsync()
    {
        var ct = _stop.Token;
        await foreach (var (ordinal, enqueuedAt, unit) in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var startedAt = _clock.Elapsed;
            var candidate = await _tidier.TidyLineAsync(unit.Composite.Text, ct).ConfigureAwait(false);
            var outcomes = TidyContract.Apply(unit, candidate, _options.LowConfidenceThreshold);
            var landedAt = _clock.Elapsed;

            lock (_gate)
            {
                _trace.Add(new TidyUnitTrace
                {
                    Ordinal = ordinal,
                    Segments = unit.Pieces.Select(p => p.Index).Distinct().ToList(),
                    Pieces = unit.Pieces.Count,
                    Words = unit.WordCount,
                    Speech = unit.Speech,
                    EnqueuedAt = enqueuedAt,
                    StartedAt = startedAt,
                    LandedAt = landedAt,
                    Accepted = outcomes[0].Accepted,
                    Refusal = outcomes[0].Refusal,
                });
            }

            foreach (var outcome in outcomes)
            {
                LandPiece(outcome);
            }
        }
    }

    /// <summary>One piece back; the segment's outcome lands when its last piece has.</summary>
    private void LandPiece(TidyPieceOutcome outcome)
    {
        var index = outcome.Piece.Index;
        TidyOutcome? ready = null;

        lock (_gate)
        {
            var slots = _pieces[index] ?? throw new InvalidOperationException($"Segment {index} was never cut into pieces.");
            slots[outcome.Piece.Ordinal] = outcome;
            if (slots.All(slot => slot is not null))
            {
                ready = Assemble(_segments[index], slots);
            }
        }

        if (ready is not null)
        {
            Interlocked.Decrement(ref _pending);
            Land(index, ready);
        }
    }

    /// <summary>
    /// A segment's outcome from its pieces': the tidied texts joined, the words concatenated, the
    /// counts summed — or the spoken segment kept whole with the first refusal, when any piece was
    /// refused. A segment carried in one whole piece comes out exactly as the contract judged it.
    /// </summary>
    private static TidyOutcome Assemble(TranscriptSegment source, TidyPieceOutcome?[] slots)
    {
        var pieces = slots.Select(slot => slot ?? throw new InvalidOperationException("A piece was never judged.")).ToArray();
        foreach (var piece in pieces)
        {
            if (!piece.Accepted)
            {
                return new TidyOutcome { Accepted = false, Segment = source, Refusal = piece.Refusal };
            }
        }

        var texts = new List<string>(pieces.Length);
        var words = new List<TranscriptWord>();
        var deleted = 0;
        var replacements = new List<TidyReplacement>();
        foreach (var piece in pieces)
        {
            if (piece.Text.Length > 0)
            {
                texts.Add(piece.Text);
            }

            words.AddRange(piece.Words);
            deleted += piece.DeletedWords;
            replacements.AddRange(piece.Replacements);
        }

        return new TidyOutcome
        {
            Accepted = true,
            Segment = source with { Text = string.Join(' ', texts), Words = words },
            DeletedWords = deleted,
            Replacements = replacements,
        };
    }

    private void Land(int index, TidyOutcome outcome)
    {
        lock (_gate)
        {
            _outcomes[index] = outcome;
        }

        _onTidied?.Invoke(index, outcome);
    }

    /// <summary>Stops whatever is in flight. Disposing before <see cref="CompleteAsync"/> abandons the pass.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Disposal reports nothing; CompleteAsync is where failures are read.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _stop.Dispose();
    }
}
