using System.Threading.Channels;
using Parakeet.Core.Transcription;

namespace Parakeet.Core.Tidying;

/// <summary>
/// A stage over the segment stream: segments go in as the recogniser produces them, a few are in
/// flight against the model at any time, and each comes back through the contract as soon as its
/// rewrite lands — in whatever order the model finishes them.
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
/// The pass shape is this stage with every segment enqueued at once
/// (<see cref="TranscriptTidy.TidyAsync"/>), so the two callers run one piece of code.
/// </para>
/// </remarks>
public sealed class TidyStage : IAsyncDisposable
{
    private readonly ITranscriptTidier _tidier;
    private readonly TidyOptions _options;
    private readonly Action<int, TidyOutcome>? _onTidied;
    private readonly Channel<(int Index, TranscriptSegment Segment)> _queue;
    private readonly List<Task> _workers;
    private readonly List<TidyOutcome?> _outcomes = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop;
    private int _pending;
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
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _queue = Channel.CreateUnbounded<(int, TranscriptSegment)>(new UnboundedChannelOptions
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
        }

        if (segment.IsEmpty)
        {
            Land(index, new TidyOutcome { Accepted = true, Segment = segment });
            return index;
        }

        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite((index, segment)))
        {
            throw new InvalidOperationException("The stage's queue refused a segment.");
        }

        return index;
    }

    /// <summary>
    /// Says that nothing more is coming, waits for every line in flight, and returns every
    /// outcome in enqueue order. Throws the first failure any line met, after the rest have been
    /// stopped — the caller's pass policy is what turns that into "the transcript, whole".
    /// </summary>
    public async Task<IReadOnlyList<TidyOutcome>> CompleteAsync()
    {
        lock (_gate)
        {
            _completed = true;
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

    private async Task WorkAsync()
    {
        var ct = _stop.Token;
        await foreach (var (index, segment) in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var candidate = await _tidier.TidyLineAsync(segment.Text, ct).ConfigureAwait(false);
            var outcome = TidyContract.Apply(segment, candidate, _options.LowConfidenceThreshold);
            Interlocked.Decrement(ref _pending);
            Land(index, outcome);
        }
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
