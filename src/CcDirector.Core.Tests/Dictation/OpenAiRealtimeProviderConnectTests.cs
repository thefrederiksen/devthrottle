using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Providers;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Connect-policy tests for <see cref="OpenAiRealtimeProvider"/> (issue #189):
/// per-attempt connect timeout, one automatic retry on transient failure, and
/// a human-readable <see cref="DictationConnectException"/> when the retry
/// budget is exhausted. Driven against an in-process fake server on loopback
/// so the tests are deterministic, offline, and cost nothing.
///
/// The 504 scenario reproduces the real outage observed on 2026-06-06 where
/// OpenAI's edge answered the WebSocket upgrade with HTTP 504 for ~90s.
/// </summary>
public sealed class OpenAiRealtimeProviderConnectTests
{
    private static readonly TimeSpan FastTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FastRetryDelay = TimeSpan.FromMilliseconds(50);

    private static OpenAiRealtimeProvider CreateProvider(FakeRealtimeServer server)
        => new(apiKey: "test-key", endpoint: server.WsUri.ToString())
        {
            ConnectTimeout = FastTimeout,
            ConnectRetryDelay = FastRetryDelay,
        };

    [Fact]
    public async Task StartAsync_504OnEveryAttempt_ThrowsConnectException_AfterTwoAttempts()
    {
        await using var server = new FakeRealtimeServer(_ => FakeRealtimeServer.Mode.Reject504);
        await using var provider = CreateProvider(server);

        var ex = await Assert.ThrowsAsync<DictationConnectException>(
            () => provider.StartAsync("prompt"));

        Assert.Equal(2, ex.Attempts);
        Assert.Equal(2, server.Attempts);
        Assert.IsType<WebSocketException>(ex.InnerException);
        // The message must be human-readable AND preserve the raw error.
        Assert.Contains("try again", ex.Message);
        Assert.Contains("504", ex.Message);
    }

    [Fact]
    public async Task StartAsync_504ThenAccept_RecoversOnRetry()
    {
        await using var server = new FakeRealtimeServer(attempt =>
            attempt == 1 ? FakeRealtimeServer.Mode.Reject504 : FakeRealtimeServer.Mode.AcceptUpgrade);
        await using var provider = CreateProvider(server);

        // Must not throw: the single automatic retry rides out a transient 504.
        await provider.StartAsync("prompt");

        Assert.Equal(2, server.Attempts);
    }

    [Fact]
    public async Task StartAsync_ServerNeverResponds_TimesOutPerAttempt_ThenThrowsConnectException()
    {
        await using var server = new FakeRealtimeServer(_ => FakeRealtimeServer.Mode.NeverRespond);
        await using var provider = CreateProvider(server);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DictationConnectException>(
            () => provider.StartAsync("prompt"));
        sw.Stop();

        Assert.Equal(2, server.Attempts);
        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Contains("timed out", ex.Message);
        // Two bounded attempts plus one retry delay, not the unbounded hang
        // (~15s observed in production) that this policy replaces.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"expected fast bounded failure, took {sw.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public async Task StartAsync_CallerCancels_PropagatesCancellation_WithoutRetry()
    {
        await using var server = new FakeRealtimeServer(_ => FakeRealtimeServer.Mode.NeverRespond);
        await using var provider = new OpenAiRealtimeProvider(
            apiKey: "test-key", endpoint: server.WsUri.ToString())
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ConnectRetryDelay = FastRetryDelay,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        // Caller cancellation is the user backing out - it must surface as
        // cancellation, not get rewrapped as a connect failure, and must not
        // burn time on a retry nobody is waiting for.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.StartAsync("prompt", cts.Token));
        Assert.Equal(1, server.Attempts);
    }

    [Fact]
    public async Task StartAsync_AfterExhaustedFailure_CanStartAgain()
    {
        // Attempts 1+2 belong to the first StartAsync (both 504); attempt 3 is
        // the second StartAsync, which must work because the failed start
        // reset the provider's session state.
        await using var server = new FakeRealtimeServer(attempt =>
            attempt <= 2 ? FakeRealtimeServer.Mode.Reject504 : FakeRealtimeServer.Mode.AcceptUpgrade);
        await using var provider = CreateProvider(server);

        await Assert.ThrowsAsync<DictationConnectException>(() => provider.StartAsync("prompt"));

        await provider.StartAsync("prompt");
        Assert.Equal(3, server.Attempts);
    }

    // ===== fake server ======================================================

    /// <summary>
    /// Minimal loopback server speaking just enough HTTP/WebSocket to drive
    /// the provider's connect path. Per-connection behavior is chosen by the
    /// attempt-number callback. AcceptUpgrade performs a real RFC 6455
    /// handshake, drains incoming frames, and answers a client close frame
    /// with a close ack so the provider's CloseAsync does not hang.
    /// </summary>
    private sealed class FakeRealtimeServer : IAsyncDisposable
    {
        public enum Mode { Reject504, AcceptUpgrade, NeverRespond }

        private const string WsAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        private readonly TcpListener _listener;
        private readonly Func<int, Mode> _modeForAttempt;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private int _attempts;

        public Uri WsUri { get; }
        public int Attempts => Volatile.Read(ref _attempts);

        public FakeRealtimeServer(Func<int, Mode> modeForAttempt)
        {
            _modeForAttempt = modeForAttempt;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            WsUri = new Uri($"ws://127.0.0.1:{port}/");
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            var handlers = new List<Task>();
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    var attempt = Interlocked.Increment(ref _attempts);
                    var mode = _modeForAttempt(attempt);
                    handlers.Add(Task.Run(() => HandleAsync(client, mode)));
                }
            }
            catch (OperationCanceledException) { /* disposing */ }
            catch (SocketException) { /* listener stopped */ }
        }

        private async Task HandleAsync(TcpClient client, Mode mode)
        {
            using var _ = client;
            try
            {
                var stream = client.GetStream();
                var headers = await ReadRequestHeadersAsync(stream);

                switch (mode)
                {
                    case Mode.Reject504:
                        await WriteAsciiAsync(stream,
                            "HTTP/1.1 504 Gateway Timeout\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        break;

                    case Mode.AcceptUpgrade:
                        var key = ExtractHeader(headers, "Sec-WebSocket-Key");
                        var accept = Convert.ToBase64String(
                            SHA1.HashData(Encoding.ASCII.GetBytes(key + WsAcceptGuid)));
                        await WriteAsciiAsync(stream,
                            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
                        await DrainFramesUntilCloseAsync(stream);
                        break;

                    case Mode.NeverRespond:
                        // Hold the socket open and say nothing - the client's
                        // connect timeout is what ends this connection.
                        await Task.Delay(Timeout.Infinite, _cts.Token);
                        break;
                }
            }
            catch (OperationCanceledException) { /* disposing */ }
            catch (IOException) { /* client went away */ }
        }

        private static async Task<string> ReadRequestHeadersAsync(NetworkStream stream)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await stream.ReadAsync(one);
                if (n == 0) throw new IOException("client closed during request");
                sb.Append((char)one[0]);
            }
            return sb.ToString();
        }

        private static string ExtractHeader(string headers, string name)
        {
            foreach (var line in headers.Split("\r\n"))
            {
                var idx = line.IndexOf(':');
                if (idx > 0 && line[..idx].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line[(idx + 1)..].Trim();
            }
            throw new InvalidOperationException($"header not found: {name}");
        }

        private static Task WriteAsciiAsync(NetworkStream stream, string text)
            => stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

        /// <summary>
        /// Read client frames (config frame, audio, etc.) until a close frame
        /// arrives, then send a close ack. Without the ack the provider's
        /// CloseAsync would wait forever for the peer's close frame.
        /// </summary>
        private async Task DrainFramesUntilCloseAsync(NetworkStream stream)
        {
            while (!_cts.IsCancellationRequested)
            {
                var head = new byte[2];
                await stream.ReadExactlyAsync(head, _cts.Token);
                var opcode = head[0] & 0x0F;
                var masked = (head[1] & 0x80) != 0;
                long len = head[1] & 0x7F;
                if (len == 126)
                {
                    var ext = new byte[2];
                    await stream.ReadExactlyAsync(ext, _cts.Token);
                    len = (ext[0] << 8) | ext[1];
                }
                else if (len == 127)
                {
                    var ext = new byte[8];
                    await stream.ReadExactlyAsync(ext, _cts.Token);
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
                }
                if (masked)
                {
                    var mask = new byte[4];
                    await stream.ReadExactlyAsync(mask, _cts.Token);
                }
                if (len > 0)
                {
                    var payload = new byte[len];
                    await stream.ReadExactlyAsync(payload, _cts.Token);
                }

                if (opcode == 0x8) // close
                {
                    await stream.WriteAsync(new byte[] { 0x88, 0x00 }, _cts.Token);
                    return;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { /* handler stuck on a dead socket; process teardown collects it */ }
            _cts.Dispose();
        }
    }
}
