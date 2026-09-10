namespace Parakeet.Engine.Python.Tests;

public sealed class PythonSidecarPreparationTests
{
    private static object HelloOnly() => new
    {
        rules = new[] { new { op = "hello", emit = new[] { FakeSidecarProcess.Handshake } } },
    };

    [Fact]
    public async Task RuntimePreparationIsDeferredAndSharedByConcurrentStarts()
    {
        using var fake = FakeSidecarProcess.Scripted(HelloOnly());
        var ready = new TaskCompletionSource<PythonRuntime.Resolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var sidecar = new PythonSidecar(_ =>
        {
            Interlocked.Increment(ref calls);
            return ready.Task;
        });

        Assert.Equal(0, calls);
        var first = sidecar.StartAsync();
        var second = sidecar.StartAsync();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Null(sidecar.Hello);

        ready.SetResult(fake.Resolution);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, calls);
        Assert.NotNull(sidecar.Hello);
    }

    [Fact]
    public async Task CancellingRuntimePreparationAllowsALaterStart()
    {
        using var fake = FakeSidecarProcess.Scripted(HelloOnly());
        var ready = new TaskCompletionSource<PythonRuntime.Resolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var sidecar = new PythonSidecar(ct =>
            Interlocked.Increment(ref calls) == 1 ? ready.Task.WaitAsync(ct) : Task.FromResult(fake.Resolution));
        using var cancellation = new CancellationTokenSource();

        var first = sidecar.StartAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Null(sidecar.Hello);

        await sidecar.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, calls);
        Assert.NotNull(sidecar.Hello);
    }

    [Fact]
    public async Task DisposingDuringPreparationDoesNotStartAChildAfterward()
    {
        using var fake = FakeSidecarProcess.Scripted(HelloOnly());
        var ready = new TaskCompletionSource<PythonRuntime.Resolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sidecar = new PythonSidecar(_ => ready.Task);

        var start = sidecar.StartAsync();
        await sidecar.DisposeAsync();
        ready.SetResult(fake.Resolution);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => start);
        Assert.Null(sidecar.Hello);
    }
}
