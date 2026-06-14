using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #412 round-trip proof for the screenshot LIST leg: <c>GET /sessions/{sid}/screenshots</c>
/// on the Gateway resolves the owning Director and forwards to its machine-wide <c>GET /screenshots</c>,
/// carrying the <c>?count=</c> query through and returning the JSON body unchanged. This is the leg
/// the Cockpit's Screenshots tab calls; before #412 it used a fresh fleet fan-out that returned a
/// spurious 503 whenever the owner's ownership probe was momentarily slow, even though the Director
/// was reachable. A stub Director (real Kestrel host) owns one session and serves a known list body.
/// Also pins that an offline owner still surfaces as 503 (the Cockpit's graceful-offline trigger).
/// </summary>
public sealed class ScreenshotListProxyRoundTripTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private StubDirector _director = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        _director = new StubDirector(sessionId: "list-session-1");
        await _director.StartAsync();

        var req = new DirectorRegistrationRequest
        {
            DirectorId = _director.DirectorId,
            TailnetEndpoint = _director.BaseUrl,
            Pid = 4243,
            MachineName = "STUB",
            User = "tester",
            Version = "test",
            StartedAt = DateTime.UtcNow,
        };
        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _director.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    private sealed record ShotItem(string FileName, string Path, string TimeLabel);
    private sealed record ShotList(string Directory, int Total, List<ShotItem> Items);

    [Fact]
    public async Task Screenshot_list_round_trips_through_the_gateway_with_count_carried_through()
    {
        var list = await _http.GetFromJsonAsync<ShotList>("sessions/list-session-1/screenshots?count=5");

        Assert.NotNull(list);
        Assert.Equal(7, list!.Total);                         // full folder count survives
        Assert.Equal(2, list.Items.Count);                    // the stub's seeded items
        Assert.Equal("newest.png", list.Items[0].FileName);
        // The ?count=5 query reached the Director intact (the proxy carried it through).
        Assert.Equal("5", _director.LastCount);
    }

    [Fact]
    public async Task Screenshot_list_for_an_offline_owner_returns_503()
    {
        // A KNOWN owner (cached) that no live Director answers for -> 503, which the Cockpit turns
        // into the graceful "Director offline" message + Retry (issue #412 AC4).
        var sid = "33333333-3333-3333-3333-333333333333";
        _gateway.SessionOwners.Remember(sid, "dead-director-id");

        var resp = await _http.GetAsync($"sessions/{sid}/screenshots");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("offline", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>
    /// Minimal Kestrel host pretending to be a Director: owns exactly one session (so the Gateway's
    /// ownership resolution finds it) and serves a fixed screenshots LIST body, recording the
    /// <c>?count=</c> it received so the proxy's query pass-through is observable.
    /// </summary>
    private sealed class StubDirector : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string BaseUrl { get; private set; } = "";
        public string? LastCount { get; private set; }

        private readonly string _sessionId;
        private WebApplication? _app;

        public StubDirector(string sessionId) => _sessionId = sessionId;

        public async Task StartAsync()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = "StubDirector",
            });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Logging.ClearProviders();
            builder.Services.AddRoutingCore();

            _app = builder.Build();
            _app.UseRouting();
            _app.MapGet("/sessions/{sid}", (string sid) =>
                sid == _sessionId
                    ? Results.Json(new SessionDto { SessionId = _sessionId, Agent = "ClaudeCode", ActivityState = "Idle", StatusColor = "green" })
                    : Results.NotFound());
            _app.MapGet("/screenshots", (int? count) =>
            {
                LastCount = count?.ToString();
                return Results.Json(new
                {
                    directory = @"C:\shots",
                    total = 7,
                    items = new[]
                    {
                        new { fileName = "newest.png", path = @"C:\shots\newest.png", timeLabel = "Jun 14, 9:00 AM" },
                        new { fileName = "older.png", path = @"C:\shots\older.png", timeLabel = "Jun 14, 8:00 AM" },
                    },
                });
            });

            await _app.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
                await _app.DisposeAsync();
                _app = null;
            }
        }
    }
}
