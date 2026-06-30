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
/// HTTP wire tests for the Gateway Director-STARTUP telemetry endpoint (issue #631),
/// <c>POST /telemetry/director-startup</c>. Boots only <see cref="DirectorStartupTelemetryEndpoint"/>
/// over a durable retry queue on an ephemeral port, plus (for the configured-forward case) a SECOND
/// minimal WebApplication acting as the local cloud startup stub. The
/// <c>DEVTHROTTLE_STARTUP_TELEMETRY_URL</c> env var points the endpoint at the stub; each test sets and
/// then restores the env var, so they are self-contained.
///
/// Covers the two acceptance-criteria paths: the RECORD-ONLY path (no startup URL configured -> 202,
/// recorded locally, the "no cloud startup endpoint configured" log line, and NO forward), and the
/// CONFIGURED-FORWARD path (a startup URL set -> 202 and exactly one forwarded POST carrying the body
/// reaches the stub). Plus the 202-for-valid-body criterion and <see cref="ResolveTargetUrl"/>.
/// </summary>
public sealed class DirectorStartupTelemetryEndpointTests
{
    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>One captured request the stub cloud startup endpoint received.</summary>
    private sealed record CapturedRequest(string Method, string Path, string Body);

    /// <summary>
    /// Boots the startup endpoint over a durable retry queue and (optionally) a stub cloud startup
    /// endpoint that records every request it gets. When <paramref name="startStub"/> is false the
    /// env var is CLEARED so the endpoint runs the record-only (no-forward) path. Returns the endpoint's
    /// base address, the captured-request list, and an async disposer.
    /// </summary>
    private static async Task<(HttpClient client, ConcurrentQueue<CapturedRequest> captured, Func<Task> dispose)>
        StartAsync(bool startStub)
    {
        var captured = new ConcurrentQueue<CapturedRequest>();
        WebApplication? stub = null;
        string? targetUrl = null;

        if (startStub)
        {
            var stubPort = AllocateFreePort();
            var stubBuilder = WebApplication.CreateBuilder();
            stubBuilder.Logging.ClearProviders();
            stub = stubBuilder.Build();
            stub.Urls.Add($"http://127.0.0.1:{stubPort}");
            stub.MapPost("/api/v1/telemetry/director-startup", async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                captured.Enqueue(new CapturedRequest(
                    ctx.Request.Method,
                    ctx.Request.Path.Value ?? "",
                    body));
                return Results.StatusCode(200);
            });
            await stub.StartAsync();
            targetUrl = $"http://127.0.0.1:{stubPort}/api/v1/telemetry/director-startup";
        }

        var prev = Environment.GetEnvironmentVariable(DirectorStartupTelemetryEndpoint.TargetUrlEnvVar);
        // Configured-forward case sets the stub URL; record-only case CLEARS it (so no default fires).
        Environment.SetEnvironmentVariable(DirectorStartupTelemetryEndpoint.TargetUrlEnvVar, targetUrl);

        var endpointPort = AllocateFreePort();
        var endpointBuilder = WebApplication.CreateBuilder();
        endpointBuilder.Logging.ClearProviders();
        var app = endpointBuilder.Build();
        app.Urls.Add($"http://127.0.0.1:{endpointPort}");

        // Reuse the durable retry queue (issues #628 / #629). Short-timeout client + short retry
        // interval so the configured-forward delivery is observable within the test's poll window.
        // Isolated temp file so it never touches the real store.
        var queuePath = Path.Combine(Path.GetTempPath(), $"startup-telemetry-queue-{Guid.NewGuid():N}.json");
        var queue = new TelemetryRetryQueue(
            queuePath,
            new HttpClient { Timeout = TimeSpan.FromSeconds(3) },
            retryInterval: TimeSpan.FromMilliseconds(100));
        DirectorStartupTelemetryEndpoint.Map(app, queue);
        queue.StartFlushing();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{endpointPort}/") };

        Func<Task> dispose = async () =>
        {
            client.Dispose();
            await queue.DisposeAsync();
            await app.DisposeAsync();
            if (stub is not null) await stub.DisposeAsync();
            try { if (File.Exists(queuePath)) File.Delete(queuePath); } catch { /* temp cleanup */ }
            Environment.SetEnvironmentVariable(DirectorStartupTelemetryEndpoint.TargetUrlEnvVar, prev);
        };

        return (client, captured, dispose);
    }

    /// <summary>
    /// Polls until the stub has captured at least <paramref name="count"/> requests, or the timeout
    /// elapses. Delivery is asynchronous (the queue flusher), so the forward assertion waits here.
    /// </summary>
    private static async Task WaitForCapturedAsync(ConcurrentQueue<CapturedRequest> captured, int count, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (captured.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    private static HttpRequestMessage StartupRequest(object body) =>
        new(HttpMethod.Post, "telemetry/director-startup")
        {
            Content = JsonContent.Create(body),
        };

    // ----- AC1: 202 for a valid body -----

    [Fact]
    public async Task PostDirectorStartup_WithValidBody_Returns202()
    {
        var (client, _, dispose) = await StartAsync(startStub: false);
        try
        {
            var resp = await client.SendAsync(StartupRequest(new
            {
                director_id = "dir-abc",
                machine_name = "WORKSTATION-1",
                app_version = "1.2.3",
            }));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        }
        finally { await dispose(); }
    }

    // ----- AC2 + AC4: record-only path (no cloud URL) -----

    [Fact]
    public async Task PostDirectorStartup_NoCloudUrl_RecordsLocallyAndDoesNotForward()
    {
        // Issue #862: capture FileLog in a private, synchronously-drained sink so this assertion
        // reads exactly this test's lines - no carryover from the shared process-wide writer and no
        // background-flush timing race (which previously made this test flaky in CI).
        using var log = FileLog.RedirectForTests();

        var (client, captured, dispose) = await StartAsync(startStub: false);
        try
        {
            var resp = await client.SendAsync(StartupRequest(new
            {
                director_id = "dir-record-only",
                machine_name = "WORKSTATION-2",
                app_version = "7.8.9",
            }));
            // AC4: still 202 with no cloud URL configured.
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        }
        finally
        {
            await dispose();
        }

        // Nothing was forwarded (no stub was started; captured stays empty by construction).
        Assert.Empty(captured);

        var newLines = log.DrainAndReadLines();

        // AC2: the event is recorded Gateway-side with director_id + app_version.
        Assert.Contains(newLines, l =>
            l.Contains("[DirectorStartupTelemetryEndpoint] director-startup recorded")
            && l.Contains("director_id=dir-record-only")
            && l.Contains("app_version=7.8.9"));

        // AC4: a log line states no cloud startup endpoint is configured.
        Assert.Contains(newLines, l =>
            l.Contains("[DirectorStartupTelemetryEndpoint]")
            && l.Contains("no cloud startup endpoint configured"));
    }

    // ----- AC3: configured-forward path -----

    [Fact]
    public async Task PostDirectorStartup_WithCloudUrl_ForwardsExactlyOnceWithBody()
    {
        var (client, captured, dispose) = await StartAsync(startStub: true);
        try
        {
            var resp = await client.SendAsync(StartupRequest(new
            {
                director_id = "dir-forward",
                machine_name = "WORKSTATION-3",
                app_version = "9.9.9",
            }));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

            // Delivery is asynchronous (the queue flusher) - wait for the one forwarded POST.
            await WaitForCapturedAsync(captured, 1);
            Assert.Single(captured);
            Assert.True(captured.TryDequeue(out var fwd));
            Assert.NotNull(fwd);

            // Correct method + URL path.
            Assert.Equal("POST", fwd.Method);
            Assert.Equal("/api/v1/telemetry/director-startup", fwd.Path);

            // The body reached the stub unchanged (director_id + app_version present).
            using var doc = JsonDocument.Parse(fwd.Body);
            Assert.Equal("dir-forward", doc.RootElement.GetProperty("director_id").GetString());
            Assert.Equal("9.9.9", doc.RootElement.GetProperty("app_version").GetString());
            Assert.Equal("WORKSTATION-3", doc.RootElement.GetProperty("machine_name").GetString());
        }
        finally { await dispose(); }
    }

    // ----- ResolveTargetUrl -----

    [Fact]
    public void ResolveTargetUrl_NoEnv_ReturnsNull()
    {
        var prev = Environment.GetEnvironmentVariable(DirectorStartupTelemetryEndpoint.TargetUrlEnvVar);
        Environment.SetEnvironmentVariable(DirectorStartupTelemetryEndpoint.TargetUrlEnvVar, null);
        try
        {
            Assert.Null(DirectorStartupTelemetryEndpoint.ResolveTargetUrl());
        }
        finally { Environment.SetEnvironmentVariable(DirectorStartupTelemetryEndpoint.TargetUrlEnvVar, prev); }
    }

    [Fact]
    public void ResolveTargetUrl_WithEnv_ReturnsOverride()
    {
        var prev = Environment.GetEnvironmentVariable(DirectorStartupTelemetryEndpoint.TargetUrlEnvVar);
        Environment.SetEnvironmentVariable(DirectorStartupTelemetryEndpoint.TargetUrlEnvVar, "http://127.0.0.1:9/startup");
        try
        {
            Assert.Equal("http://127.0.0.1:9/startup", DirectorStartupTelemetryEndpoint.ResolveTargetUrl());
        }
        finally { Environment.SetEnvironmentVariable(DirectorStartupTelemetryEndpoint.TargetUrlEnvVar, prev); }
    }
}
