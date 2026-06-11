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
/// Issue #317 round-trip proof: <c>GET /sessions/{sid}/screenshots/file?name=...</c> on the
/// Gateway resolves the owning Director and streams the image bytes + content type through
/// unchanged. A stub Director (real Kestrel host) implements just the two endpoints the proxy
/// touches: <c>GET /sessions/{sid}</c> (ownership resolution) and
/// <c>GET /screenshots/file</c> (the bytes). The test asserts the bytes that come back from the
/// Gateway are byte-identical to the source, the content type survives, and the URL-escaped
/// file name arrives at the Director decoded.
/// </summary>
public sealed class ScreenshotProxyRoundTripTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private StubDirector _director = null!;

    // Tiny but real PNG header + payload - byte-identity is what matters, not renderability.
    private static readonly byte[] PngBytes =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0xDE, 0xAD, 0xBE, 0xEF, 0x13, 0x37, 0x42, 0x42,
    };

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1);
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        _director = new StubDirector(sessionId: "shot-session-1", pngBytes: PngBytes);
        await _director.StartAsync();

        var req = new DirectorRegistrationRequest
        {
            DirectorId = _director.DirectorId,
            TailnetEndpoint = _director.BaseUrl,
            Pid = 4242,
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

    [Fact]
    public async Task Screenshot_bytes_round_trip_through_the_gateway_with_content_type()
    {
        var resp = await _http.GetAsync(
            "sessions/shot-session-1/screenshots/file?name=Screenshot%202026-06-11%2010.30.45.png");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(resp.Content.Headers.ContentType);
        Assert.Equal("image/png", resp.Content.Headers.ContentType.MediaType);

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(PngBytes, bytes);

        // The escaped query reached the Director decoded - the proxy carried it through intact.
        Assert.Equal("Screenshot 2026-06-11 10.30.45.png", _director.LastRequestedName);
    }

    [Fact]
    public async Task Unknown_file_on_a_live_owner_passes_the_director_404_through()
    {
        var resp = await _http.GetAsync("sessions/shot-session-1/screenshots/file?name=missing.png");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>
    /// Minimal Kestrel host pretending to be a Director's Control API: owns exactly one session
    /// (so the Gateway's ownership fan-out resolves it) and serves one screenshot file's bytes.
    /// </summary>
    private sealed class StubDirector : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string BaseUrl { get; private set; } = "";
        public string? LastRequestedName { get; private set; }

        private readonly string _sessionId;
        private readonly byte[] _pngBytes;
        private WebApplication? _app;

        public StubDirector(string sessionId, byte[] pngBytes)
        {
            _sessionId = sessionId;
            _pngBytes = pngBytes;
        }

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
            _app.MapGet("/screenshots/file", (string name) =>
            {
                LastRequestedName = name;
                return name == "Screenshot 2026-06-11 10.30.45.png"
                    ? Results.Bytes(_pngBytes, "image/png")
                    : Results.NotFound();
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
