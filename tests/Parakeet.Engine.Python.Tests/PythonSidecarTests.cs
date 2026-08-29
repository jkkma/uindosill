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
        // The constant, not a literal: this asserts that the handshake reply is kept, which is
        // about plumbing, and a number written here would be a third copy of the protocol version.
        Assert.Equal(PythonSidecar.ProtocolVersion, sidecar.Hello!.Value.GetProperty("protocol").GetInt32());
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
    public async Task ACancelLandingMidWriteLeavesTheLineWholeAndTheChannelUsable()
    {
        // The test above cancels a request that is already on the wire; this one cancels a
        // request still crossing it. A child asleep in a rule's delay is a child not reading its
        // stdin, so the pipe behind it fills and a long enough line parks the writer part-way
        // through — which is the real shape of a cancel during translation, where the whole
        // segment travels in the line with every non-ASCII character escaped to six. Before the
        // write was committed (see SendAsync), the token could tear the line between two flushes:
        // the next request's line glued onto the dangling prefix, the child answered the glue
        // with an id of null, and that request's caller waited forever.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new
            {
                op = "nap",
                announce = new[] { """{"id":{id},"type":"progress","completed":1,"total":99}""" },

                // Long enough that a stalled test process cannot see the child wake, drain the
                // pipe and answer before the 200 ms token below fires; the bounded waits at the
                // bottom keep the worst case a slow test, not a hung one.
                delayMilliseconds = 5000,
                emit = new[] { """{"id":{id},"type":"result"}""" },
            },
            new { op = "wide", emit = new[] { """{"id":{id},"type":"result"}""" } },
            new { op = "ping", emit = new[] { """{"id":{id},"type":"result","said":"fine"}""" } }));
        using var staged = fake;
        await using var child = sidecar;

        // Waited for, as above: the announce is the child saying it has the nap in hand — and,
        // because announce comes before the delay, that it has now stopped reading.
        var napping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nap = sidecar.SendAsync("nap", _ => { }, new SynchronousProgress(napping));
        await napping.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Megabytes against a pipe of kilobytes: the write is parked mid-line when the token
        // fires, and the caller comes back at once rather than waiting out the child's sleep —
        // the line itself finishes later, whole, once the child wakes and drains the pipe.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var wide = sidecar.SendAsync(
            "wide",
            writer => writer.WriteString("payload", new string('x', 4 * 1024 * 1024)),
            null,
            cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wide);

        // The bounded waits are the regression check: a torn line makes the child silent for
        // every request after it, and silence here should be a failed test, not a hung one.
        await nap.WaitAsync(TimeSpan.FromSeconds(10));
        var reply = await sidecar.SendAsync("ping", _ => { }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("fine", reply.GetProperty("said").GetString());
        Assert.False(sidecar.IsFaulted);
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
    public async Task ConcurrentFirstStartsBothCompleteWithoutParkingEitherCaller()
    {
        // The sequential case above is the fast path; this is the one the constructor parameter
        // that shares a sidecar between two engines makes reachable. Unguarded, both calls saw a
        // null process and both spawned an interpreter — one orphaned behind open pipes, and one
        // caller parked forever on a hello written to a child whose stdout nobody read. What this
        // pins is the parked-caller half, through the bounded wait; the one-child half has no
        // observable here (the fake cannot report how many of it were spawned), so the name
        // claims only what the assertions hold.
        var fake = FakeSidecarProcess.Scripted(HelloOnly());
        using var staged = fake;
        var sidecar = new PythonSidecar(fake.Resolution);
        await using var child = sidecar;

        await Task.WhenAll(sidecar.StartAsync(), sidecar.StartAsync()).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(PythonSidecar.ProtocolVersion, sidecar.Hello!.Value.GetProperty("protocol").GetInt32());
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

    [Fact]
    public async Task ALineThatIsJsonButNotAMessageIsRecordedRatherThanFatal()
    {
        // JSON is not the same thing as a message. A bare number, an array, a string, an id that
        // is not an integer — each parses, and each used to throw past the catch that guards
        // parsing, from inside the reader, which ended the reader for the rest of the run. The
        // contract is that a line the host cannot read is one more line of the stderr tail.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(new
        {
            unsolicited = new[]
            {
                "0",
                "[1,2]",
                "\"done\"",
                """{"id":"two","type":"result"}""",
                """{"id":1.5,"type":"result"}""",
                """{"id":99999999999,"type":"result"}""",
                """{"id":9999,"type":5}""",
            },
            rules = new object[]
            {
                new { op = "hello", emit = new[] { FakeSidecarProcess.Handshake } },
                new { op = "ping", emit = new[] { """{"id":{id},"type":"result","said":"still here"}""" } },
            },
        });
        using var staged = fake;
        await using var child = sidecar;

        Assert.Equal("still here", (await sidecar.SendAsync("ping", _ => { })).GetProperty("said").GetString());
        Assert.False(sidecar.IsFaulted);

        var recorded = sidecar.DescribeStandardError();
        Assert.Contains("non-protocol line on stdout: 0", recorded, StringComparison.Ordinal);
        Assert.Contains("non-protocol line on stdout: [1,2]", recorded, StringComparison.Ordinal);
        Assert.Contains("protocol message with no id: {\"id\":1.5", recorded, StringComparison.Ordinal);
        Assert.Contains("protocol message with no id: {\"id\":99999999999", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProgressReportWithACountThatIsNotAnIntegerIsSkippedAndTheResultStillArrives()
    {
        // A count that arrives as a string is not reported as zero — a progress bar that jumps
        // back to nothing is a lie — and it is not fatal either: the reader records it and reads
        // on, and the result behind it completes the request.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new
            {
                op = "label",
                emit = new[]
                {
                    """{"id":{id},"type":"progress","completed":"1","total":4}""",
                    """{"id":{id},"type":"progress","completed":2,"total":4.0}""",
                    """{"id":{id},"type":"progress","completed":3,"total":4}""",
                    """{"id":{id},"type":"result","turns":[]}""",
                },
            }));
        using var staged = fake;
        await using var child = sidecar;

        var seen = new SynchronousProgress();
        var reply = await sidecar.SendAsync("label", _ => { }, seen);

        Assert.True(reply.TryGetProperty("turns", out _));
        Assert.Equal([(3, 4)], seen.Reported);
        Assert.Contains(
            "progress message with a count that is not an integer",
            sidecar.DescribeStandardError(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorWhoseFieldsAreMistypedStillFailsItsRequestAndNotTheReader()
    {
        // The id and the type are enough to know which request failed. A kind that is not a
        // string is what a missing one is — internal, the bucket for the sidecar's own bugs — the
        // message likewise, and a traceback is kept when it is text and dropped when it is not.
        // The channel stays up for the next request.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new
            {
                op = "load",
                emit = new[] { """{"id":{id},"type":"error","kind":7,"message":["not","a","string"],"traceback":null}""" },
            },
            new { op = "ping", emit = new[] { """{"id":{id},"type":"result","said":"fine"}""" } }));
        using var staged = fake;
        await using var child = sidecar;

        var failure = await Assert.ThrowsAsync<PythonEngineException>(() => sidecar.SendAsync("load", _ => { }));
        Assert.Equal("internal", failure.Kind);
        Assert.Equal("", failure.Message);
        Assert.Null(failure.PythonTraceback);

        Assert.Equal("fine", (await sidecar.SendAsync("ping", _ => { })).GetProperty("said").GetString());
        Assert.False(sidecar.IsFaulted);
    }

    [Fact]
    public async Task ARequestCancelledBeforeItIsWrittenIsACancellationAndTheChannelIsStillUsable()
    {
        // The other half of cancellation: not in flight, but before the write. The token is
        // cancelled before the call, so the wait for the write gate is the first thing that sees
        // it and nothing reaches the child. What is pinned is that this surfaces as a cancellation
        // and not as a fault of the sidecar, and that the channel answers the next request.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new { op = "ping", emit = new[] { """{"id":{id},"type":"result","said":"fine"}""" } }));
        using var staged = fake;
        await using var child = sidecar;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sidecar.SendAsync("ping", _ => { }, null, new CancellationToken(canceled: true)));

        Assert.Equal("fine", (await sidecar.SendAsync("ping", _ => { })).GetProperty("said").GetString());
        Assert.False(sidecar.IsFaulted);
    }

    [Fact]
    public async Task DisposingWithACancelledRequestStillInFlightKillsAtOnceRatherThanWaiting()
    {
        // The child is mid-way through work nobody wants and cannot read the shutdown line until
        // it finishes, so asking nicely costs the full five seconds for nothing. Until 2026-08-22
        // every cancel-then-close paid them. The announced progress is the child saying it has
        // the request in hand, which is what makes "cancelled in flight" a fact rather than a race.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly(
            new
            {
                op = "slow",
                announce = new[] { """{"id":{id},"type":"progress","completed":1,"total":99}""" },
                delayMilliseconds = 20_000,
                emit = new[] { """{"id":{id},"type":"result"}""" },
            }));
        using var staged = fake;

        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var slow = sidecar.SendAsync("slow", _ => { }, new SynchronousProgress(received), cancel.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await sidecar.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"dispose took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task TheChildIsInAKillOnCloseJobOnWindowsAndSaysSoElsewhere()
    {
        // A host that dies without reaching DisposeAsync leaves a child holding a gigabyte of
        // weights behind a closed pipe; on Windows the job object is what takes it along. Off
        // Windows nothing does, and the property says so rather than pretending.
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(HelloOnly());
        using var staged = fake;
        await using var child = sidecar;

        Assert.Equal(OperatingSystem.IsWindows(), sidecar.InKillOnCloseJob);
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
