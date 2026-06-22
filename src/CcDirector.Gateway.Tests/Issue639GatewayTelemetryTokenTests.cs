using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Centralization Phase 2 (issue #639): the Gateway forwards telemetry with its OWN account
/// token instead of the Director's. These tests cover the issue's five acceptance criteria:
/// <list type="number">
///   <item>A telemetry event posted WITHOUT an Authorization header is forwarded carrying the GATEWAY's
///     token (stub sees the Gateway token, not a Director token).</item>
///   <item>When the Gateway is NOT signed in, a posted event is QUEUED (not forwarded) and is then
///     flushed to the stub after the Gateway signs in (seeded token), in order.</item>
///   <item>An inbound Director Bearer, if still present, is IGNORED in favour of the Gateway's token
///     (no double-auth, no Director-token leakage to the cloud).</item>
///   <item>The Gateway's token is NEVER written to the Gateway log.</item>
///   <item>(this whole file is the unit coverage required by criterion 5.)</item>
/// </list>
///
/// The queue-level cases drive delivery deterministically through a fake
/// <see cref="IGatewayTelemetryTokenSource"/> + the public <see cref="TelemetryRetryQueue.FlushOnceAsync"/>
/// (no timers, no network). The end-to-end cases boot the real <see cref="TelemetryRelayEndpoint"/> over a
/// queue wired with the same fake token source, plus a local stub backend, so the tokenless-inbound ->
/// gateway-token-out path is exercised across the actual HTTP relay.
/// </summary>
public sealed class Issue639GatewayTelemetryTokenTests
{
    private const string TargetUrl = "http://backend.test/api/v1/telemetry/login";
    private const string GatewayToken = "GATEWAY-OWN-ACCOUNT-TOKEN-639-DO-NOT-LOG";
    private const string DirectorToken = "DIRECTOR-LEFTOVER-BEARER-639-SHOULD-NOT-LEAK";

    /// <summary>
    /// A fake Gateway token source whose signed-in state and token the test flips. Returns the configured
    /// token while <see cref="SignedIn"/> is true; returns false (not signed in) otherwise.
    /// </summary>
    private sealed class FakeGatewayTokenSource : IGatewayTelemetryTokenSource
    {
        public volatile bool SignedIn;
        public string Token = GatewayToken;

        public bool TryGetAccessToken(out string? accessToken)
        {
            if (!SignedIn)
            {
                accessToken = null;
                return false;
            }
            accessToken = Token;
            return true;
        }
    }

    /// <summary>
    /// A fake backend that records, in order, the body and Authorization header of every request it
    /// receives. Reachable is true by default (these tests gate delivery on sign-in, not reachability).
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public readonly List<string> ReceivedBodies = new();
        public readonly List<string?> ReceivedAuth = new();
        private readonly object _lock = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_lock)
            {
                ReceivedBodies.Add(body);
                ReceivedAuth.Add(request.Headers.Authorization?.ToString());
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static (TelemetryRetryQueue queue, RecordingHandler handler, FakeGatewayTokenSource source, string path) NewQueue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"telemetry-queue-639-{Guid.NewGuid():N}.json");
        var handler = new RecordingHandler();
        var source = new FakeGatewayTokenSource();
        var queue = new TelemetryRetryQueue(
            path,
            new HttpClient(handler),
            TimeSpan.FromMilliseconds(50),
            maxSize: 1000,
            gatewayTokenSource: source);
        return (queue, handler, source, path);
    }

    private static string BodyFor(int i) => JsonSerializer.Serialize(new { source = "app", seq = i });

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp cleanup */ }
    }

    // ----- AC1: forward carries the GATEWAY's token -----

    [Fact]
    public async Task Forward_WhenSignedIn_AttachesTheGatewayToken()
    {
        var (queue, handler, source, path) = NewQueue();
        try
        {
            source.SignedIn = true;
            // Enqueued with NO per-event bearer (the relay path): the Gateway token must still be attached.
            queue.Enqueue(TargetUrl, BodyFor(0), bearer: null);

            var delivered = await queue.FlushOnceAsync();

            Assert.Equal(1, delivered);
            Assert.Equal(0, queue.Depth);
            Assert.Single(handler.ReceivedAuth);
            Assert.Equal($"Bearer {GatewayToken}", handler.ReceivedAuth[0]);
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- AC2: not signed in -> queued; then flushed after sign-in, in order -----

    [Fact]
    public async Task NotSignedIn_QueuesEventsThenFlushesAfterSignIn_InOrder()
    {
        var (queue, handler, source, path) = NewQueue();
        try
        {
            source.SignedIn = false; // Gateway not signed in yet
            for (var i = 0; i < 4; i++)
                queue.Enqueue(TargetUrl, BodyFor(i), bearer: null);

            // Not forwarded while not signed in - the events stay queued.
            Assert.Equal(4, queue.Depth);
            var deliveredWhileSignedOut = await queue.FlushOnceAsync();
            Assert.Equal(0, deliveredWhileSignedOut);
            Assert.Equal(4, queue.Depth);
            Assert.Empty(handler.ReceivedBodies);

            // The Gateway signs in (seeded token); the next flush drains everything in FIFO order.
            source.SignedIn = true;
            var delivered = await queue.FlushOnceAsync();

            Assert.Equal(4, delivered);
            Assert.Equal(0, queue.Depth);
            Assert.Equal(4, handler.ReceivedBodies.Count);
            for (var i = 0; i < 4; i++)
            {
                using var doc = JsonDocument.Parse(handler.ReceivedBodies[i]);
                Assert.Equal(i, doc.RootElement.GetProperty("seq").GetInt32());
            }
            // Every forward carried the Gateway token.
            Assert.All(handler.ReceivedAuth, a => Assert.Equal($"Bearer {GatewayToken}", a));
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- AC3: an inbound Director Bearer (stored per-event) is IGNORED in favour of the Gateway token -----

    [Fact]
    public async Task InboundDirectorBearer_IsIgnored_GatewayTokenUsedInstead()
    {
        var (queue, handler, source, path) = NewQueue();
        try
        {
            source.SignedIn = true;
            // Simulate a leftover Director Bearer that somehow reached the queue: it must NOT be forwarded.
            queue.Enqueue(TargetUrl, BodyFor(0), bearer: DirectorToken);

            var delivered = await queue.FlushOnceAsync();

            Assert.Equal(1, delivered);
            Assert.Single(handler.ReceivedAuth);
            // The Gateway's token was attached, not the Director's - and the Director token never went out.
            Assert.Equal($"Bearer {GatewayToken}", handler.ReceivedAuth[0]);
            Assert.DoesNotContain(handler.ReceivedAuth, a => a is not null && a.Contains(DirectorToken));
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- AC4: the Gateway token is never written to the Gateway log on the forward path -----

    [Fact]
    public async Task Forward_NeverWritesTheGatewayTokenToTheLog()
    {
        var logDir = CcStorage.ToolLogs("director");
        var baseline = new Dictionary<string, int>();
        if (Directory.Exists(logDir))
            foreach (var f in Directory.EnumerateFiles(logDir, "*.log"))
                baseline[f] = ReadAllLinesShared(f).Count;

        FileLog.Start();
        var (queue, handler, source, path) = NewQueue();
        try
        {
            source.SignedIn = false;
            queue.Enqueue(TargetUrl, BodyFor(0), bearer: DirectorToken);
            // A deferred (not-signed-in) pass logs, then a signed-in pass forwards and logs - both paths
            // touch the token-bearing code; neither may write a token value.
            Assert.Equal(0, await queue.FlushOnceAsync());
            source.SignedIn = true;
            Assert.Equal(1, await queue.FlushOnceAsync());
            Assert.Single(handler.ReceivedBodies);
        }
        finally
        {
            await queue.DisposeAsync();
            Cleanup(path);
            await Task.Delay(500); // let the background writer flush our lines
        }

        var newLines = new List<string>();
        if (Directory.Exists(logDir))
            foreach (var f in Directory.EnumerateFiles(logDir, "*.log"))
            {
                var lines = ReadAllLinesShared(f);
                var start = baseline.TryGetValue(f, out var n) ? n : 0;
                for (var i = start; i < lines.Count; i++)
                    newLines.Add(lines[i]);
            }

        // The queue logged on this path (proves logging is wired)...
        Assert.Contains(newLines, l => l.Contains("[TelemetryRetryQueue]"));
        // ...and NEITHER the Gateway token NOR the (ignored) Director token appears anywhere in the log.
        Assert.DoesNotContain(newLines, l => l.Contains(GatewayToken));
        Assert.DoesNotContain(newLines, l => l.Contains(DirectorToken));
    }

    // ----- Back-compat: with NO token source the queue keeps the Phase 1 behaviour (per-event bearer) -----

    [Fact]
    public async Task NoTokenSource_FallsBackToTheStoredPerEventBearer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"telemetry-queue-639-nofb-{Guid.NewGuid():N}.json");
        var handler = new RecordingHandler();
        // No gatewayTokenSource argument: Phase 1 behaviour - forward the stored bearer unchanged.
        var queue = new TelemetryRetryQueue(path, new HttpClient(handler), TimeSpan.FromMilliseconds(50));
        try
        {
            queue.Enqueue(TargetUrl, BodyFor(0), bearer: DirectorToken);
            var delivered = await queue.FlushOnceAsync();

            Assert.Equal(1, delivered);
            Assert.Single(handler.ReceivedAuth);
            Assert.Equal($"Bearer {DirectorToken}", handler.ReceivedAuth[0]);
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- AC1 end-to-end: tokenless inbound POST -> forwarded to the stub with the Gateway token -----

    [Fact]
    public async Task PostLogin_WithoutAuthorizationHeader_ForwardsWithGatewayToken()
    {
        var (relay, captured, dispose) = await StartRelayAsync(signedIn: true);
        try
        {
            // No Authorization header on the inbound request at all.
            var req = new HttpRequestMessage(HttpMethod.Post, "telemetry/login")
            {
                Content = JsonContent.Create(new { source = "app", app_version = "1.2.3" }),
            };
            var resp = await relay.SendAsync(req);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            await WaitForCapturedAsync(captured, 1);
            Assert.Single(captured);
            Assert.True(captured.TryDequeue(out var fwd));
            Assert.NotNull(fwd);
            // The forward carried the GATEWAY's token (not a Director token).
            Assert.Equal($"Bearer {GatewayToken}", fwd.Authorization);
            using var doc = JsonDocument.Parse(fwd.Body);
            Assert.Equal("app", doc.RootElement.GetProperty("source").GetString());
        }
        finally { await dispose(); }
    }

    // ----- AC3 end-to-end: an inbound Director Bearer on the request is ignored -----

    [Fact]
    public async Task PostLogin_WithInboundDirectorBearer_ForwardsGatewayTokenAndNotTheDirectorToken()
    {
        var (relay, captured, dispose) = await StartRelayAsync(signedIn: true);
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "telemetry/login")
            {
                Content = JsonContent.Create(new { source = "app" }),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", DirectorToken);

            var resp = await relay.SendAsync(req);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            await WaitForCapturedAsync(captured, 1);
            Assert.Single(captured);
            Assert.True(captured.TryDequeue(out var fwd));
            Assert.NotNull(fwd);
            // The Gateway token went out; the Director token did NOT leak to the cloud.
            Assert.Equal($"Bearer {GatewayToken}", fwd.Authorization);
            Assert.DoesNotContain(DirectorToken, fwd.Authorization ?? "");
        }
        finally { await dispose(); }
    }

    // ----- AC2 end-to-end: not signed in -> queued; sign in -> flushed -----

    [Fact]
    public async Task PostLogin_WhenGatewayNotSignedIn_QueuesThenFlushesAfterSignIn()
    {
        var source = new FakeGatewayTokenSource { SignedIn = false };
        var (relay, captured, dispose) = await StartRelayAsync(source);
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "telemetry/login")
            {
                Content = JsonContent.Create(new { source = "app", app_version = "2.0.0" }),
            };
            var resp = await relay.SendAsync(req);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            // Not signed in: nothing reaches the stub even after a generous wait.
            await Task.Delay(600);
            Assert.Empty(captured);

            // The Gateway signs in: the queued event now flushes (the background flusher delivers it).
            source.SignedIn = true;
            await WaitForCapturedAsync(captured, 1);
            Assert.Single(captured);
            Assert.True(captured.TryDequeue(out var fwd));
            Assert.NotNull(fwd);
            Assert.Equal($"Bearer {GatewayToken}", fwd.Authorization);
        }
        finally { await dispose(); }
    }

    // ----- relay boot helpers -----

    private sealed record CapturedRequest(string Method, string Path, string? Authorization, string Body);

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Task<(HttpClient relay, ConcurrentQueue<CapturedRequest> captured, Func<Task> dispose)>
        StartRelayAsync(bool signedIn) =>
        StartRelayAsync(new FakeGatewayTokenSource { SignedIn = signedIn });

    /// <summary>
    /// Boots the real login relay over a durable queue wired with <paramref name="source"/> as its
    /// Gateway token source, plus a local stub backend that records every forwarded request. Short retry
    /// interval so the asynchronous flush completes within the test poll window.
    /// </summary>
    private static async Task<(HttpClient relay, ConcurrentQueue<CapturedRequest> captured, Func<Task> dispose)>
        StartRelayAsync(FakeGatewayTokenSource source)
    {
        var captured = new ConcurrentQueue<CapturedRequest>();

        var stubPort = AllocateFreePort();
        var stubBuilder = WebApplication.CreateBuilder();
        stubBuilder.Logging.ClearProviders();
        var stub = stubBuilder.Build();
        stub.Urls.Add($"http://127.0.0.1:{stubPort}");
        stub.MapPost("/api/v1/telemetry/login", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            captured.Enqueue(new CapturedRequest(
                ctx.Request.Method,
                ctx.Request.Path.Value ?? "",
                ctx.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null,
                body));
            return Results.StatusCode(200);
        });
        await stub.StartAsync();
        var targetUrl = $"http://127.0.0.1:{stubPort}/api/v1/telemetry/login";

        var prev = Environment.GetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar);
        Environment.SetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar, targetUrl);

        var relayPort = AllocateFreePort();
        var relayBuilder = WebApplication.CreateBuilder();
        relayBuilder.Logging.ClearProviders();
        var relayApp = relayBuilder.Build();
        relayApp.Urls.Add($"http://127.0.0.1:{relayPort}");

        var queuePath = Path.Combine(Path.GetTempPath(), $"telemetry-queue-639-e2e-{Guid.NewGuid():N}.json");
        var queue = new TelemetryRetryQueue(
            queuePath,
            new HttpClient { Timeout = TimeSpan.FromSeconds(3) },
            retryInterval: TimeSpan.FromMilliseconds(100),
            maxSize: TelemetryRetryQueue.DefaultMaxSize,
            gatewayTokenSource: source);
        TelemetryRelayEndpoint.Map(relayApp, queue);
        queue.StartFlushing();
        await relayApp.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{relayPort}/") };

        Func<Task> dispose = async () =>
        {
            client.Dispose();
            await queue.DisposeAsync();
            await relayApp.DisposeAsync();
            await stub.DisposeAsync();
            Cleanup(queuePath);
            Environment.SetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar, prev);
        };

        return (client, captured, dispose);
    }

    private static async Task WaitForCapturedAsync(ConcurrentQueue<CapturedRequest> captured, int count, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (captured.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    private static List<string> ReadAllLinesShared(string path)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            lines.Add(line);
        return lines;
    }
}
