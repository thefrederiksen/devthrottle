using System.Collections.Concurrent;
using System.Net;
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
/// HTTP wire tests for the Gateway login-telemetry RELAY (issue #628), <c>POST /telemetry/login</c>.
/// Boots only <see cref="TelemetryRelayEndpoint"/> on an ephemeral port, plus a SECOND minimal
/// WebApplication acting as the local backend stub. The <c>DEVTHROTTLE_TELEMETRY_URL</c> env var
/// points the relay at the stub (or, for the unreachable case, at a dead port). Each test sets and
/// then restores the env var, so they are self-contained.
///
/// Covers: the forward happy-path (exactly one backend POST, correct URL, POST method, a body with
/// <c>source:"app"</c>, and a 202 to the caller); backend 5xx and backend unreachable (the caller still
/// gets a non-5xx and the failure is logged); and that the access token is never written to the Gateway
/// log.
///
/// Gateway Centralization Phase 2 (issue #639): the relay no longer forwards an inbound Director Bearer.
/// These tests boot the queue with NO Gateway token source (the Phase 1 forwarder shape), so a forwarded
/// request carries NO Authorization header - the inbound Bearer is ignored and the Gateway attaches its
/// own token only when a token source is wired (covered by Issue639GatewayTelemetryTokenTests).
/// </summary>
public sealed class TelemetryRelayEndpointTests
{
    private const string AccessToken = "test-access-token-628-DO-NOT-LOG-abc123";

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>One captured request the stub backend received.</summary>
    private sealed record CapturedRequest(string Method, string Path, string? Authorization, string Body);

    /// <summary>
    /// Boots the relay endpoint over a durable retry queue (issue #629) and (optionally) a stub
    /// backend that records every request it gets. Returns the relay's base address, the
    /// captured-request list, and an async disposer. The queue uses a short retry interval so the
    /// happy-path forward (which is now asynchronous - the relay enqueues and the flusher delivers)
    /// completes quickly.
    /// </summary>
    private static async Task<(HttpClient relay, ConcurrentQueue<CapturedRequest> captured, Func<Task> dispose)>
        StartAsync(bool startStub, int backendStatus = 200)
    {
        var captured = new ConcurrentQueue<CapturedRequest>();
        WebApplication? stub = null;
        string targetUrl;

        if (startStub)
        {
            var stubPort = AllocateFreePort();
            var stubBuilder = WebApplication.CreateBuilder();
            stubBuilder.Logging.ClearProviders();
            stub = stubBuilder.Build();
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
                return Results.StatusCode(backendStatus);
            });
            await stub.StartAsync();
            targetUrl = $"http://127.0.0.1:{stubPort}/api/v1/telemetry/login";
        }
        else
        {
            // A free-but-unbound port: the forward attempt fails to connect (the unreachable case).
            targetUrl = $"http://127.0.0.1:{AllocateFreePort()}/api/v1/telemetry/login";
        }

        var prev = Environment.GetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar);
        Environment.SetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar, targetUrl);

        var relayPort = AllocateFreePort();
        var relayBuilder = WebApplication.CreateBuilder();
        relayBuilder.Logging.ClearProviders();
        var relay = relayBuilder.Build();
        relay.Urls.Add($"http://127.0.0.1:{relayPort}");

        // Issue #629: the relay enqueues into a durable retry queue and the queue's flusher delivers.
        // Short-timeout client so the unreachable case fails fast; short retry interval so the
        // happy-path delivery is observable within the test's poll window. Isolated temp file.
        var queuePath = Path.Combine(Path.GetTempPath(), $"telemetry-queue-{Guid.NewGuid():N}.json");
        var queue = new TelemetryRetryQueue(
            queuePath,
            new HttpClient { Timeout = TimeSpan.FromSeconds(3) },
            retryInterval: TimeSpan.FromMilliseconds(100));
        TelemetryRelayEndpoint.Map(relay, queue);
        queue.StartFlushing();
        await relay.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{relayPort}/") };

        Func<Task> dispose = async () =>
        {
            client.Dispose();
            await queue.DisposeAsync();
            await relay.DisposeAsync();
            if (stub is not null) await stub.DisposeAsync();
            try { if (File.Exists(queuePath)) File.Delete(queuePath); } catch { /* temp cleanup */ }
            Environment.SetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar, prev);
        };

        return (client, captured, dispose);
    }

    /// <summary>
    /// Polls until the stub has captured at least <paramref name="count"/> requests, or the timeout
    /// elapses. Delivery is asynchronous now (the queue flusher), so happy-path assertions wait here.
    /// </summary>
    private static async Task WaitForCapturedAsync(ConcurrentQueue<CapturedRequest> captured, int count, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (captured.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    private static HttpRequestMessage LoginRequest(string accessToken, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "telemetry/login")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    // ----- AC1 + AC2: happy path -----

    [Fact]
    public async Task PostLogin_WithValidBodyAndBearer_Returns202()
    {
        var (relay, _, dispose) = await StartAsync(startStub: true);
        try
        {
            var resp = await relay.SendAsync(LoginRequest(AccessToken, new { source = "app", app_version = "1.2.3" }));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        }
        finally { await dispose(); }
    }

    [Fact]
    public async Task PostLogin_ForwardsExactlyOnce_IgnoringInboundBearer_WithSourceApp()
    {
        var (relay, captured, dispose) = await StartAsync(startStub: true);
        try
        {
            var resp = await relay.SendAsync(LoginRequest(AccessToken, new { source = "app", app_version = "9.9.9" }));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            // Delivery is asynchronous now (the queue flusher) - wait for the one forwarded POST.
            await WaitForCapturedAsync(captured, 1);
            // Exactly one forwarded POST reached the stub.
            Assert.Single(captured);
            Assert.True(captured.TryDequeue(out var fwd));
            Assert.NotNull(fwd);

            // Correct method, URL path.
            Assert.Equal("POST", fwd!.Method);
            Assert.Equal("/api/v1/telemetry/login", fwd.Path);

            // Issue #639: the inbound Director Bearer is IGNORED. With no Gateway token source wired here,
            // the forward carries NO Authorization header at all - and the Director token never leaked.
            Assert.Null(fwd.Authorization);

            // A body carrying source:"app".
            using var doc = JsonDocument.Parse(fwd.Body);
            Assert.Equal("app", doc.RootElement.GetProperty("source").GetString());
            Assert.Equal("9.9.9", doc.RootElement.GetProperty("app_version").GetString());
        }
        finally { await dispose(); }
    }

    // ----- AC3: backend failure (5xx) -----

    [Fact]
    public async Task PostLogin_WhenBackendReturns5xx_CallerStillGets202()
    {
        var (relay, captured, dispose) = await StartAsync(startStub: true, backendStatus: 500);
        try
        {
            var resp = await relay.SendAsync(LoginRequest(AccessToken, new { source = "app" }));
            // Best-effort: a backend 5xx must NOT propagate as a 5xx to the caller.
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            Assert.True((int)resp.StatusCode < 500);
            // The forward attempt still happened (the backend is the one that failed). The queue keeps
            // retrying a 5xx, so at least one POST reached the stub.
            await WaitForCapturedAsync(captured, 1);
            Assert.True(captured.Count >= 1);
        }
        finally { await dispose(); }
    }

    // ----- AC3: backend unreachable -----

    [Fact]
    public async Task PostLogin_WhenBackendUnreachable_CallerStillGetsNon5xx()
    {
        var (relay, _, dispose) = await StartAsync(startStub: false);
        try
        {
            var resp = await relay.SendAsync(LoginRequest(AccessToken, new { source = "app" }));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            Assert.True((int)resp.StatusCode < 500);
        }
        finally { await dispose(); }
    }

    // ----- AC4: the access token is never written to the Gateway log -----

    [Fact]
    public async Task PostLogin_NeverWritesAccessTokenToTheGatewayLog()
    {
        // Issue #862: capture FileLog in a private, synchronously-drained sink so we read exactly
        // this test's lines - no carryover from the shared process-wide writer and no background-
        // flush timing race (the old baseline + Task.Delay version was flaky in CI).
        using var log = FileLog.RedirectForTests();

        var (relay, captured, dispose) = await StartAsync(startStub: true);
        try
        {
            var resp = await relay.SendAsync(LoginRequest(AccessToken, new { source = "app", app_version = "4.5.6" }));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            await WaitForCapturedAsync(captured, 1);
            Assert.Single(captured); // confirm the enqueue + flush path that logs actually ran
        }
        finally
        {
            await dispose();
        }

        var newLines = log.DrainAndReadLines();

        // The relay must have logged SOMETHING on this path (proves logging is wired)...
        Assert.Contains(newLines, l => l.Contains("[TelemetryRelayEndpoint]"));
        // ...and the access token must appear in NONE of the log lines.
        Assert.DoesNotContain(newLines, l => l.Contains(AccessToken));
    }

    // ----- ResolveTargetUrl -----

    [Fact]
    public void ResolveTargetUrl_NoEnv_ReturnsDefault()
    {
        var prev = Environment.GetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar);
        Environment.SetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar, null);
        try
        {
            Assert.Equal(TelemetryRelayEndpoint.DefaultTargetUrl, TelemetryRelayEndpoint.ResolveTargetUrl());
        }
        finally { Environment.SetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar, prev); }
    }

    [Fact]
    public void ResolveTargetUrl_WithEnv_ReturnsOverride()
    {
        var prev = Environment.GetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar);
        Environment.SetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar, "http://127.0.0.1:9/x");
        try
        {
            Assert.Equal("http://127.0.0.1:9/x", TelemetryRelayEndpoint.ResolveTargetUrl());
        }
        finally { Environment.SetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar, prev); }
    }
}
