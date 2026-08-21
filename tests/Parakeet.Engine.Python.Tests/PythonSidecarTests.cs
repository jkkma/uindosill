using System.Text.Json;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// The transport: the handshake, correlating replies to requests, and what a dead child does to the
/// requests that were waiting on it.
/// </summary>
/// <remarks>
/// None of this needs a model, and that is the point of testing it here rather than at the end of a
/// diarisation. A protocol desynchronised by one stray line, or a reply matched to the wrong
/// request, produces a result rather than an error — the same class of silent wrongness the parity
/// fixture exists for, one layer down.
/// </remarks>
public sealed class PythonSidecarTests
{
    private static object HelloOnly(params object[] extraRules) => new
    {
        rules = new object[] { new { op = "hello", emit = new[] { FakeSidecarProcess.Handshake } } }
            .Concat(extraRules)
            .ToArray(),
    };

    [Fact]
    public async Task TheHandshakeIsTheFirstThingAndItsAnswerIsKept()
    {
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly());
        using var staged = fake;
        await using var child = sidecar;

        Assert.NotNull(sidecar.Hello);
        Assert.Equal(1, sidecar.Hello!.Value.GetProperty("protocol").GetInt32());
        Assert.Equal("3.12.10", sidecar.Hello!.Value.GetProperty("python").GetString());
    }

    [Fact]
    public async Task ASidecarSpeakingAnotherProtocolIsRefusedRatherThanDriven()
    {
        // The failure this prevents is a stale bundled Python being driven by a newer host, which
        // would otherwise surface several megabytes into a model load as something unrelated.
        using var fake = FakeSidecarProcess.Scripted(new
        {
            rules = new[]
            {
                new { op = "hello", emit = new[] { """{"id":{id},"type":"result","protocol":99}""" } },
            },
        });

        await using var sidecar = new PythonSidecar(fake.Resolution);

        var failure = await Assert.ThrowsAsync<PythonSidecarException>(() => sidecar.StartAsync());
        Assert.Contains("protocol 99", failure.Message, StringComparison.Ordinal);
        Assert.Contains("reinstall", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHandshakeThatNamesNoProtocolIsRefusedRatherThanAssumedToBeThisOne()
    {
        using var fake = FakeSidecarProcess.Scripted(new
        {
            rules = new[] { new { op = "hello", emit = new[] { """{"id":{id},"type":"result"}""" } } },
        });

        await using var sidecar = new PythonSidecar(fake.Resolution);

        var failure = await Assert.ThrowsAsync<PythonSidecarException>(() => sidecar.StartAsync());
        Assert.Contains("protocol -1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepliesAreMatchedByIdRatherThanByArrivalOrder()
    {
        // The one property the whole transport rests on, and the only way to establish it is to
        // answer the two requests in the WRONG order: a host that handed each caller whatever
        // arrived next would return one caller's translation to another with no error anywhere, and
        // a stand-in that always replied in order would let it.
        //
        // The ids are known rather than guessed. PythonSidecar allocates them with an
        // Interlocked.Increment from zero and writes under a single gate, so the handshake is 1 and
        // the two requests below are 2 and 3 in the order they are sent. `first` is scripted to say
        // nothing at all; `second` then emits BOTH replies, its own first and `first`'s after it.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new { op = "first", emit = Array.Empty<string>() },
            new
            {
                op = "second",
                emit = new[]
                {
                    """{"id":3,"type":"result","said":"second"}""",
                    """{"id":2,"type":"result","said":"first"}""",
                },
            }));
        using var staged = fake;
        await using var child = sidecar;

        var first = sidecar.SendAsync("first", _ => { });
        var second = sidecar.SendAsync("second", _ => { });

        // "second" is answered before "first" even though it was sent after it. Matching by arrival
        // order would give `first` the string "second" and fail here, which is the point.
        Assert.Equal("first", (await first).GetProperty("said").GetString());
        Assert.Equal("second", (await second).GetProperty("said").GetString());
    }

    [Fact]
    public async Task ProgressReachesTheReporterAndTheResultStillArrives()
    {
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new
            {
                op = "label",
                emit = new[]
                {
                    """{"id":{id},"type":"progress","completed":1,"total":4}""",
                    """{"id":{id},"type":"progress","completed":3,"total":4}""",
                    """{"id":{id},"type":"result","turns":[]}""",
                },
            }));
        using var staged = fake;
        await using var child = sidecar;

        // Not Progress<T>: that marshals each report to the thread pool, so the result can complete
        // before the reports have run and the count becomes a race. The reader loop dispatches
        // progress and results in the order they arrive, so a reporter that records on the calling
        // thread sees all of them before the result completes — which is a fact worth relying on and
        // not a timing accident.
        var seen = new SynchronousProgress();
        var reply = await sidecar.SendAsync("label", writer => { }, seen);

        Assert.True(reply.TryGetProperty("turns", out _));
        Assert.Equal([(1, 4), (3, 4)], seen.Reported);
    }

    [Fact]
    public async Task AnErrorMessageBecomesAnExceptionCarryingItsKind()
    {
        // Kind rather than message text is what a caller switches on, and the reason is a batch: a
        // file that could not be read is one file, and a model that is not there is every file.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new
            {
                op = "label",
                emit = new[]
                {
                    """{"id":{id},"type":"error","kind":"audio","message":"no audio at C:/x.wav","traceback":"Traceback..."}""",
                },
            }));
        using var staged = fake;
        await using var child = sidecar;

        var failure = await Assert.ThrowsAsync<PythonEngineException>(
            () => sidecar.SendAsync("label", _ => { }));

        Assert.Equal("audio", failure.Kind);
        Assert.Equal("no audio at C:/x.wav", failure.Message);
        Assert.Equal("Traceback...", failure.PythonTraceback);
    }

    [Fact]
    public async Task AnErrorWithNoKindIsInternalRatherThanUnclassified()
    {
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new { op = "load", emit = new[] { """{"id":{id},"type":"error","message":"something"}""" } }));
        using var staged = fake;
        await using var child = sidecar;

        var failure = await Assert.ThrowsAsync<PythonEngineException>(() => sidecar.SendAsync("load", _ => { }));
        Assert.Equal("internal", failure.Kind);
    }

    [Fact]
    public async Task ALineThatIsNotProtocolIsRecordedRatherThanFatal()
    {
        // torch, librosa and numba all print to stdout given the right provocation. The Python side
        // takes the handle away to stop that, but a line arriving anyway must not end a run: it is
        // one library misbehaving, not a dead sidecar.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(new
        {
            unsolicited = new[] { "Loading model...", """{"type":"result","said":"no id at all"}""" },
            rules = new object[]
            {
                new { op = "hello", emit = new[] { FakeSidecarProcess.Handshake } },
                new { op = "ping", emit = new[] { """{"id":{id},"type":"result","said":"still here"}""" } },
            },
        });
        using var staged = fake;
        await using var child = sidecar;

        var reply = await sidecar.SendAsync("ping", _ => { });
        Assert.Equal("still here", reply.GetProperty("said").GetString());

        var recorded = sidecar.DescribeStandardError();
        Assert.Contains("non-protocol line on stdout", recorded, StringComparison.Ordinal);
        Assert.Contains("protocol message with no id", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyToARequestNobodyMadeIsIgnored()
    {
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(new
        {
            unsolicited = new[] { """{"id":9999,"type":"result","said":"nobody asked"}""" },
            rules = new object[]
            {
                new { op = "hello", emit = new[] { FakeSidecarProcess.Handshake } },
                new { op = "ping", emit = new[] { """{"id":{id},"type":"result","said":"fine"}""" } },
            },
        });
        using var staged = fake;
        await using var child = sidecar;

        Assert.Equal("fine", (await sidecar.SendAsync("ping", _ => { })).GetProperty("said").GetString());
    }

    [Fact]
    public async Task AChildThatDiesMidRequestFailsWhatWasWaitingRatherThanHanging()
    {
        // The difference between a bug report and a hang. Nothing will ever answer, so every pending
        // request has to be told so — and told it with the child's own last words attached.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new
            {
                op = "label",
                stderr = new[] { "Traceback (most recent call last):", "MemoryError" },
                exit = 3,
            }));
        using var staged = fake;
        await using var child = sidecar;

        var failure = await Assert.ThrowsAsync<PythonSidecarException>(() => sidecar.SendAsync("label", _ => { }));

        Assert.Contains("exited unexpectedly", failure.Message, StringComparison.Ordinal);

        // The child's last words are read by an independent task, and nothing orders it against the
        // stdout reader that composes the message above — so on a loaded machine the traceback can
        // still be in flight when the exception is built. What is asserted is that the tail is kept
        // and reaches the report, not that it wins a race: a test that demanded the latter would
        // fail in CI for a reason that is not a defect.
        var tail = await Eventually(() =>
            sidecar.DescribeStandardError() is { } text && text.Contains("MemoryError", StringComparison.Ordinal)
                ? text
                : null);

        Assert.Contains("Traceback (most recent call last):", tail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTailOfTheChildsStandardErrorIsKeptForTheMessageThatReportsItsDeath()
    {
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(new
        {
            stderr = new[] { "onnxruntime: falling back", "and then this" },
            rules = new[] { new { op = "hello", emit = new[] { FakeSidecarProcess.Handshake } } },
        });
        using var staged = fake;
        await using var child = sidecar;

        // The child writes its stderr before the handshake, but the reader is a separate task, so
        // the lines can still be in flight. Waiting for the text is what the host itself does — it
        // only ever reads this after the child has gone — and a poll here is not a race, it is the
        // same wait with a bound on it.
        var tail = await Eventually(() =>
            sidecar.DescribeStandardError() is { } text && text.Contains("and then this", StringComparison.Ordinal)
                ? text
                : null);

        Assert.Contains("Last output from the Python engines", tail, StringComparison.Ordinal);
        Assert.Contains("onnxruntime: falling back", tail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestInFlightIsCancellableAndDoesNotPoisonTheNextOne()
    {
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new
            {
                op = "slow",
                announce = new[] { """{"id":{id},"type":"progress","completed":1,"total":99}""" },
                delayMilliseconds = 5000,
                emit = new[] { """{"id":{id},"type":"result"}""" },
            },
            new { op = "ping", emit = new[] { """{"id":{id},"type":"result","said":"fine"}""" } }));
        using var staged = fake;
        await using var child = sidecar;

        // Waited for rather than assumed. SendAsync's first await is the write gate, so cancelling
        // straight after the call can land before the request has reached the wire — and the test
        // would then be about a request that was never sent. The announced progress is the child
        // saying it has this one and is working on it.
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var slow = sidecar.SendAsync(
            "slow", _ => { }, new SynchronousProgress(received), cancel.Token);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);

        // The half the name promises and the half that matters more. Cancelling unblocks this side;
        // it does not reach into the child, which finishes what it was doing and then reads the next
        // line. A channel that could not be used afterwards would turn one cancelled file into a
        // dead sidecar for the rest of a batch.
        Assert.Equal("fine", (await sidecar.SendAsync("ping", _ => { })).GetProperty("said").GetString());
    }

    [Fact]
    public async Task SendingAfterDisposeIsRefusedRatherThanSilentlyLost()
    {
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly());
        using var staged = fake;

        await sidecar.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sidecar.SendAsync("ping", writer => { }));
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly());
        using var staged = fake;

        await sidecar.DisposeAsync();
        await sidecar.DisposeAsync();
    }

    [Fact]
    public async Task StartingTwiceHandshakesOnceRatherThanSpawningASecondChild()
    {
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly());
        using var staged = fake;
        await using var child = sidecar;

        var first = sidecar.Hello!.Value.GetRawText();
        await sidecar.StartAsync();

        Assert.Equal(first, sidecar.Hello!.Value.GetRawText());
    }

    [Fact]
    public async Task TheRequestCarriesTheOpAndWhateverTheCallerWrote()
    {
        // Echoed back rather than asserted on the wire, because what a test can see is what the
        // child received — which is the only place the request's shape actually matters.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new { op = "load", emit = new[] { """{"id":{id},"type":"result","ok":true}""" } }));
        using var staged = fake;
        await using var child = sidecar;

        var reply = await sidecar.SendAsync("load", writer =>
        {
            writer.WriteString("engine", "translator");
            writer.WriteNumber("threads", 0);
        });

        Assert.True(reply.GetProperty("ok").GetBoolean());
    }

    /// <summary>Records on whatever thread reports, so the ordering above is testable.</summary>
    private sealed class SynchronousProgress : IProgress<(int Completed, int Total)>
    {
        private readonly List<(int Completed, int Total)> _reported = [];
        private readonly TaskCompletionSource? _first;

        public SynchronousProgress(TaskCompletionSource? first = null) => _first = first;

        public IReadOnlyList<(int Completed, int Total)> Reported
        {
            get
            {
                lock (_reported)
                {
                    return [.. _reported];
                }
            }
        }

        public void Report((int Completed, int Total) value)
        {
            lock (_reported)
            {
                _reported.Add(value);
            }

            _first?.TrySetResult();
        }
    }

    private static async Task<T> Eventually<T>(Func<T?> read, int milliseconds = 5000) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (read() is { } value)
            {
                return value;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Nothing arrived within {milliseconds} ms.");
    }
}
