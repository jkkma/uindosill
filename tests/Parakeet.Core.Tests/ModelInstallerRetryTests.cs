using System.Net;
using System.Security.Cryptography;
using Parakeet.Core.Models;

namespace Parakeet.Core.Tests;

/// <summary>
/// What happens when the connection dies mid-download, which until 2026-08-29 was that the
/// application closed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this file exists for.</b> Hugging Face ended a response after 149,148 bytes of a
/// 6,716,356,800-byte file. `HttpIOException` came out of the installer's read loop, matched
/// neither of the Models tab's two catch clauses, escaped an async command — where nothing awaits
/// it — and the process was terminated. The download had every ingredient needed to survive it
/// already on disk: a `.part` file, resume metadata, and a range request. None of them was used,
/// because nothing caught the exception.
/// </para>
/// <para>
/// <b>There were no HTTP-level tests of this class before these</b>, which is how a download path
/// that cannot survive a dropped connection shipped. `HttpClient` is injectable on the constructor,
/// so the transport can be faked entirely: no socket is opened by anything here.
/// </para>
/// </remarks>
public class ModelInstallerRetryTests
{
    private static readonly byte[] Payload = CreatePayload();

    private static byte[] CreatePayload()
    {
        var bytes = new byte[64 * 1024];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private static string PayloadSha => Convert.ToHexStringLower(SHA256.HashData(Payload));

    /// <summary>A stream that hands back a prefix and then dies, as a cut-off response does.</summary>
    private sealed class TruncatingStream(byte[] data, int failAfter) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAfter)
            {
                throw new IOException("The response ended prematurely.");
            }

            var take = Math.Min(count, failAfter - _position);
            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Serves the payload, cutting the first <c>cutFirst</c> responses short. Honours Range so a
    /// retry resumes, which is the behaviour under test rather than an incidental convenience.
    /// </summary>
    private sealed class FlakyHandler(int cutFirst, int failAfterBytes) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public List<long> RangeStarts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var from = (int)(request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0);
            RangeStarts.Add(from);

            var remaining = Payload.Length - from;
            var body = new byte[remaining];
            Array.Copy(Payload, from, body, 0, remaining);

            HttpContent content = Requests <= cutFirst
                ? new StreamContent(new TruncatingStream(body, Math.Min(failAfterBytes, remaining)))
                : new ByteArrayContent(body);

            var response = new HttpResponseMessage(
                from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = content,
            };

            response.Content.Headers.ContentLength = remaining;
            if (from > 0)
            {
                response.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(from, Payload.Length - 1, Payload.Length);
            }

            return Task.FromResult(response);
        }
    }

    private static ModelDescriptor Descriptor() => new()
    {
        Id = "flaky-test-model",
        Family = "test",
        DisplayName = "Flaky",
        Quantisation = "none",
        License = "none",
        AttributionIds = [],
        Files =
        [
            new ModelFile
            {
                FileName = "flaky.bin",
                Url = new Uri("https://example.invalid/flaky.bin"),
                SizeBytes = Payload.Length,
                Sha256 = PayloadSha,
            },
        ],
    };

    [Fact]
    public async Task ADroppedConnectionIsResumedRatherThanFatal()
    {
        // The reproduction: the first response dies part way through, exactly as the real one did.
        var handler = new FlakyHandler(cutFirst: 1, failAfterBytes: 1024);
        var store = new LocalModelStore(TestTemp.NewDirectory("uindosill-flaky"));
        using var installer = new ModelInstaller(store, new HttpClient(handler));

        var result = await installer.InstallAsync(Descriptor());

        Assert.Equal(PayloadSha, result.Files.Single().Sha256);
        Assert.Equal(Payload.Length, result.Files.Single().SizeBytes);

        // Two requests, and the second asked to continue rather than to start again. That second
        // assertion is the one that matters: a retry that re-fetched from zero would also make this
        // test pass on the digest alone, and would still be re-downloading 6.3 GB in the real case.
        Assert.Equal(2, handler.Requests);
        Assert.Equal(0, handler.RangeStarts[0]);
        Assert.True(handler.RangeStarts[1] > 0, "the retry did not resume from the partial file");
    }

    [Fact]
    public async Task RepeatedDropsStillFinishWhileProgressIsBeingMade()
    {
        // A genuinely flaky link: several cut-offs, each after some progress. The budget resets on
        // progress, so this must complete rather than exhaust the attempt count.
        var handler = new FlakyHandler(cutFirst: 8, failAfterBytes: 4096);
        var store = new LocalModelStore(TestTemp.NewDirectory("uindosill-flaky"));
        using var installer = new ModelInstaller(store, new HttpClient(handler));

        var result = await installer.InstallAsync(Descriptor());

        Assert.Equal(PayloadSha, result.Files.Single().Sha256);
        Assert.True(handler.Requests > 2);
        Assert.True(
            handler.RangeStarts.Zip(handler.RangeStarts.Skip(1)).All(pair => pair.Second > pair.First),
            "each retry should have resumed further on than the last");
    }

    [Fact]
    public async Task AConnectionThatNeverDeliversFailsAsAModelInstallException()
    {
        // The other half of the fix. When retrying cannot help, the caller must get this class's
        // own exception type — which is the one the Models tab catches and turns into a row that
        // says "Failed" — rather than a raw HttpIOException that nothing above it handles.
        var handler = new FlakyHandler(cutFirst: int.MaxValue, failAfterBytes: 0);
        var store = new LocalModelStore(TestTemp.NewDirectory("uindosill-flaky"));
        using var installer = new ModelInstaller(store, new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<ModelInstallException>(
            () => installer.InstallAsync(Descriptor()));

        // The message has to tell the user the download is resumable, because it is: the partial
        // file is deliberately kept.
        Assert.Contains("resume", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationIsStillCancellationAndNotARetry()
    {
        // `OperationCanceledException` is retryable *only* when the caller's token is not the
        // reason — an HttpClient timeout presents that way. A real cancel must come straight out,
        // or Cancel in the window would take five attempts and a backoff to do nothing.
        var handler = new FlakyHandler(cutFirst: int.MaxValue, failAfterBytes: 512);
        var store = new LocalModelStore(TestTemp.NewDirectory("uindosill-flaky"));
        using var installer = new ModelInstaller(store, new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => installer.InstallAsync(Descriptor(), ct: cancellation.Token));
    }
}
