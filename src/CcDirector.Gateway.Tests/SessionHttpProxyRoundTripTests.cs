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
/// Issue #372 round-trip proof for the generic per-session HTTP forwarder: ANY method on
/// <c>/sessions/{sid}/{**rest}</c> the Gateway does not handle explicitly is reverse-proxied to the
/// owning Director at the SAME path. A stub Director (real Kestrel host) implements
/// <c>GET /sessions/{sid}</c> (ownership resolution) plus two arbitrary per-session verbs - a GET
/// (<c>/git</c>) and a POST (<c>/clear-context</c>). The test asserts the forward reaches the
/// Director at the correct path/method, carries the query string and request body through, and
/// streams the Director's response (status + body) back unchanged - including a Director 409.
/// </summary>
public sealed class SessionHttpProxyRoundTripTests : IAsyncLifetime
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

        _director = new StubDirector(sessionId: "http-session-1");
        await _director.StartAsync();

        var req = new DirectorRegistrationRequest
        {
            DirectorId = _director.DirectorId,
            TailnetEndpoint = _director.BaseUrl,
            Pid = 4242,
            // The stub runs on loopback on THIS machine, so it must register as same-machine:
            // issue #457 refuses a loopback endpoint advertised for a different machine.
            MachineName = Environment.MachineName,
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
    public async Task Get_verb_round_trips_through_the_gateway_to_the_same_director_path()
    {
        var resp = await _http.GetAsync("sessions/http-session-1/git");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal("{\"branch\":\"main\"}", body);
        Assert.Equal("GET /sessions/http-session-1/git", _director.LastRequest);
    }

    [Fact]
    public async Task Post_verb_carries_body_through_and_streams_the_response_back()
    {
        var resp = await _http.PostAsync("sessions/http-session-1/clear-context", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal("{\"accepted\":true}", body);
        Assert.Equal("POST /sessions/http-session-1/clear-context", _director.LastRequest);
    }

    [Fact]
    public async Task Director_409_passes_through_unchanged()
    {
        // The stub returns 409 for an unsupported verb; the proxy must not rewrite it to 502/503.
        var resp = await _http.PostAsync("sessions/http-session-1/history-picker", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Unknown_session_is_404_not_a_forward()
    {
        var resp = await _http.GetAsync("sessions/no-such-session/git");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ----- Issue #372 slice 3: screenshots list/delete + director-scoped settings -----

    [Fact]
    public async Task Screenshot_list_forwards_to_the_directors_machine_wide_screenshots()
    {
        var resp = await _http.GetAsync("sessions/http-session-1/screenshots?count=5");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal("{\"items\":[]}", body);
        // The sid-scoped Gateway path lands on the Director's machine-wide /screenshots,
        // with the ?count query carried through.
        Assert.Equal("GET /screenshots?count=5", _director.LastRequest);
    }

    [Fact]
    public async Task Screenshot_delete_forwards_as_DELETE_to_the_directors_screenshots_file()
    {
        var resp = await _http.DeleteAsync("sessions/http-session-1/screenshots/file?name=a%20b.png");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("DELETE /screenshots/file?name=a b.png", _director.LastRequest);
    }

    [Fact]
    public async Task Director_settings_round_trip_by_director_id()
    {
        var get = await _http.GetAsync($"directors/{_director.DirectorId}/settings");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("{\"voice\":{}}", await get.Content.ReadAsStringAsync());
        Assert.Equal("GET /settings", _director.LastRequest);

        var put = await _http.PutAsync($"directors/{_director.DirectorId}/settings",
            new StringContent("{\"voice\":{\"rate\":2}}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("PUT /settings {\"voice\":{\"rate\":2}}", _director.LastRequest);
    }

    [Fact]
    public async Task Unknown_director_id_is_404_for_settings()
    {
        var resp = await _http.GetAsync("directors/no-such-director/settings");
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
    /// (so the Gateway's ownership fan-out resolves it) and answers two arbitrary per-session verbs.
    /// Records the last method+path it saw so the test can prove the proxy hit the same path.
    /// </summary>
    private sealed class StubDirector : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string BaseUrl { get; private set; } = "";
        public string? LastRequest { get; private set; }

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

            // Ownership resolution leg used by the Gateway's LocateOwningDirectorAsync fan-out.
            _app.MapGet("/sessions/{sid}", (string sid) =>
                sid == _sessionId
                    ? Results.Json(new SessionDto { SessionId = _sessionId, Agent = "ClaudeCode", ActivityState = "Idle", StatusColor = "green" })
                    : Results.NotFound());

            _app.MapGet("/sessions/{sid}/git", (string sid) =>
            {
                LastRequest = $"GET /sessions/{sid}/git";
                return Results.Text("{\"branch\":\"main\"}", "application/json");
            });

            _app.MapPost("/sessions/{sid}/clear-context", (string sid) =>
            {
                LastRequest = $"POST /sessions/{sid}/clear-context";
                return Results.Text("{\"accepted\":true}", "application/json");
            });

            _app.MapPost("/sessions/{sid}/history-picker", (string sid) =>
                Results.Json(new { error = "not supported" }, statusCode: StatusCodes.Status409Conflict));

            // Issue #372 slice 3 legs: machine-wide screenshots + Director settings.
            _app.MapGet("/screenshots", (HttpContext ctx) =>
            {
                LastRequest = $"GET /screenshots{ctx.Request.QueryString.Value}";
                return Results.Text("{\"items\":[]}", "application/json");
            });
            _app.MapDelete("/screenshots/file", (string name) =>
            {
                LastRequest = $"DELETE /screenshots/file?name={name}";
                return Results.Json(new { deleted = true });
            });
            _app.MapGet("/settings", () =>
            {
                LastRequest = "GET /settings";
                return Results.Text("{\"voice\":{}}", "application/json");
            });
            _app.MapPut("/settings", async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                LastRequest = $"PUT /settings {body}";
                return Results.Json(new { applied = true });
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
