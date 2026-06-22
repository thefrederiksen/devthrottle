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
/// Covers: the forward happy-path (exactly one backend POST, correct URL, POST method, the SAME
/// Bearer token, a body with <c>source:"app"</c>, and a 202 to the caller); backend 5xx and backend
/// unreachable (the caller still gets a non-5xx and the failure is logged); and that the access token
/// is never written to the Gateway log.
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
    /// Boots the relay endpoint and (optionally) a stub backend that records every request it gets.
    /// Returns the relay's base address, the captured-request list, and an async disposer.
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
        // Short-timeout client so the unreachable case fails fast.
        TelemetryRelayEndpoint.Map(relay, new HttpClient { Timeout = TimeSpan.FromSeconds(3) });
        await relay.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{relayPort}/") };

        Func<Task> dispose = async () =>
        {
            client.Dispose();
            await relay.DisposeAsync();
            if (stub is not null) await stub.DisposeAsync();
            Environment.SetEnvironmentVariable(TelemetryRelayEndpoint.TargetUrlEnvVar, prev);
        };

        return (client, captured, dispose);
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
    public async Task PostLogin_ForwardsExactlyOnce_WithSameBearerAndSourceApp()
    {
        var (relay, captured, dispose) = await StartAsync(startStub: true);
        try
        {
            var resp = await relay.SendAsync(LoginRequest(AccessToken, new { source = "app", app_version = "9.9.9" }));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            // Exactly one forwarded POST reached the stub.
            Assert.Single(captured);
            Assert.True(captured.TryDequeue(out var fwd));
            Assert.NotNull(fwd);

            // Correct method, URL path.
            Assert.Equal("POST", fwd!.Method);
            Assert.Equal("/api/v1/telemetry/login", fwd.Path);

            // The SAME Bearer token, unchanged.
            Assert.Equal($"Bearer {AccessToken}", fwd.Authorization);

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
            // The forward still happened (the backend is the one that failed).
            Assert.Single(captured);
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
        // FileLog's sink directory is captured when the FileLog type first initializes, which may be
        // before this test runs - so we read whatever real log directory it actually writes to. The
        // live background writer holds the file open, so we read with a SHARED stream (FileShare.
        // ReadWrite) instead of File.ReadAllLines, which would throw on the open handle.
        var logDir = CcStorage.ToolLogs("director");

        // Snapshot the line count per existing log file, so we only inspect the lines THIS test added.
        var baseline = new Dictionary<string, int>();
        if (Directory.Exists(logDir))
            foreach (var f in Directory.EnumerateFiles(logDir, "*.log"))
                baseline[f] = ReadAllLinesShared(f).Count;

        FileLog.Start();
        var (relay, captured, dispose) = await StartAsync(startStub: true);
        try
        {
            var resp = await relay.SendAsync(LoginRequest(AccessToken, new { source = "app", app_version = "4.5.6" }));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            Assert.Single(captured); // confirm the path that logs actually ran
        }
        finally
        {
            await dispose();
            // Give the background writer a moment to flush our lines to disk (it does NOT stop here:
            // FileLog is a process-wide static other concurrent tests may still be using).
            await Task.Delay(500);
        }

        // Gather only the NEW lines (added after the baseline) across every log file.
        var newLines = new List<string>();
        if (Directory.Exists(logDir))
            foreach (var f in Directory.EnumerateFiles(logDir, "*.log"))
            {
                var lines = ReadAllLinesShared(f);
                var start = baseline.TryGetValue(f, out var n) ? n : 0;
                for (var i = start; i < lines.Count; i++)
                    newLines.Add(lines[i]);
            }

        // The relay must have logged SOMETHING on this path (proves logging is wired)...
        Assert.Contains(newLines, l => l.Contains("[TelemetryRelayEndpoint]"));
        // ...and the access token must appear in NONE of the log lines.
        Assert.DoesNotContain(newLines, l => l.Contains(AccessToken));
    }

    /// <summary>
    /// Reads all lines from a file that another process/thread may have open for writing (the live
    /// FileLog background writer). Uses <see cref="FileShare.ReadWrite"/> so the open handle does not
    /// block the read.
    /// </summary>
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
