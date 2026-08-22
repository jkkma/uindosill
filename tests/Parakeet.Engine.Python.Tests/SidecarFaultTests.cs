using Parakeet.Core.Audio;
using Parakeet.Core.Diarisation;
using Parakeet.Engine.Python;

namespace Parakeet.Engine.Python.Tests;

/// <summary>
/// What a sidecar remembers about its own death, and who is told when.
/// </summary>
/// <remarks>
/// Before 2026-08-22 a dead child was discovered by the next write and only by it. For the
/// diariser that write comes after the whole file has been decoded, resampled and staged, so in a
/// batch every file after the one the child died on paid its own decode and then failed the same
/// way — and a handshake that failed was forgotten by the next <c>StartAsync</c>, which returned as
/// though it had succeeded. These hold the fault to being recorded once and refused everywhere.
/// </remarks>
public sealed class SidecarFaultTests
{
    private static object Script(params object[] rules) => new
    {
        rules = new object[] { new { op = "hello", emit = new[] { FakeSidecarProcess.Handshake } } }
            .Concat(rules)
            .ToArray(),
    };

    private const string DiariserCapabilities =
        """{"id":{id},"type":"result","capabilities":{"engineName":"sortformer-onnx-python","modelId":"sortformer-4spk-v2.1","backend":"cpu","supportsFixedSpeakerCount":false,"maxSpeakers":4,"reliableUpToSeconds":3000}}""";

    [Fact]
    public async Task AfterTheChildDiesEveryLaterRequestIsRefusedAtOnceWithTheSameReason()
    {
        var (fake, sidecar) = await FakeSidecarProcess.StartAsync(Script(
            new { op = "label", stderr = new[] { "Traceback (most recent call last):", "RuntimeError: out of memory" }, exit = 3 }));
        using var staged = fake;
        await using var child = sidecar;

        var first = await Assert.ThrowsAsync<PythonSidecarException>(() => sidecar.SendAsync("label", _ => { }));
        Assert.Contains("out of memory", first.Message, StringComparison.Ordinal);
        Assert.True(sidecar.IsFaulted);

        // The second request is refused before it is written, with the first failure's message —
        // which is the one that carries the traceback — rather than waiting on a reply from a
        // process that is not there. Bounded, because the failure this guards against is a hang.
        var second = await Assert.ThrowsAsync<PythonSidecarException>(
            () => sidecar.SendAsync("label", _ => { }).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("out of memory", second.Message, StringComparison.Ordinal);
        Assert.NotSame(first, second);

        Assert.Throws<PythonSidecarException>(sidecar.ThrowIfFaulted);
    }

    [Fact]
    public async Task AHandshakeThatFailedIsRefusedAgainByTheNextStartRatherThanForgotten()
    {
        // The child answers protocol 99 and would answer anything after it. Without the fault the
        // second StartAsync saw a process and returned, and a protocol-2 sidecar refused once was
        // labelling files for a protocol-1 host from the second file on.
        using var fake = FakeSidecarProcess.Scripted(new
        {
            rules = new[]
            {
                new { op = "hello", emit = new[] { """{"id":{id},"type":"result","protocol":99}""" } },
            },
        });

        await using var sidecar = new PythonSidecar(fake.Resolution);

        var first = await Assert.ThrowsAsync<PythonSidecarException>(() => sidecar.StartAsync());
        Assert.Contains("protocol 99", first.Message, StringComparison.Ordinal);
        Assert.True(sidecar.IsFaulted);

        var second = await Assert.ThrowsAsync<PythonSidecarException>(() => sidecar.StartAsync());
        Assert.Contains("protocol 99", second.Message, StringComparison.Ordinal);

        var request = await Assert.ThrowsAsync<PythonSidecarException>(
            () => sidecar.SendAsync("load", _ => { }).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("protocol 99", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALoadedLabellerWhoseSidecarDiedRefusesTheNextFileBeforeReadingIt()
    {
        // The diariser stages a whole file before it sends anything, so the death has to be found
        // out at LoadAsync — which every LabelAsync goes through — and not by the write after the
        // staging. The audio source below throws if it is read at all.
        using var fake = FakeSidecarProcess.Scripted(Script(
            new { op = "load", emit = new[] { DiariserCapabilities } },
            new { op = "label", exit = 3 }));

        await using var sidecar = new PythonSidecar(fake.Resolution);
        await using var labeller = new SidecarSpeakerLabeller(
            new SidecarLabellerOptions { ModelPath = typeof(SidecarFaultTests).Assembly.Location }, sidecar);

        await labeller.LoadAsync();

        // The first file reaches the child, which dies on it.
        await Assert.ThrowsAsync<PythonSidecarException>(
            () => labeller.LabelAsync(new OneSecondOfSilence(), SpeakerLabellingOptions.Default));

        // The second is refused at once, and its audio is never opened for reading.
        var unread = new NeverRead();
        var refused = await Assert.ThrowsAsync<PythonSidecarException>(
            () => labeller.LabelAsync(unread, SpeakerLabellingOptions.Default).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("Python engines", refused.Message, StringComparison.Ordinal);
        Assert.False(unread.WasRead);
    }

    private sealed class OneSecondOfSilence : IAudioSource
    {
        public int SampleRate => 16_000;

        public TimeSpan? Duration => TimeSpan.FromSeconds(1);

        public async IAsyncEnumerable<ReadOnlyMemory<float>> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new float[16_000];
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverRead : IAudioSource
    {
        public bool WasRead { get; private set; }

        public int SampleRate => 16_000;

        public TimeSpan? Duration => TimeSpan.FromSeconds(1);

        public async IAsyncEnumerable<ReadOnlyMemory<float>> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            WasRead = true;
            await Task.Yield();
            yield return new float[16_000];
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
