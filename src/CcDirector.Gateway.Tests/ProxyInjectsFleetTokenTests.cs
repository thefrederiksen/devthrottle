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
/// Issue #457: when a Director runs auth-enabled (LAN mode), the Gateway must authenticate to it
/// on every proxied per-session call. The browser only carried the Gateway credential, which the
/// Director does not accept - so the proxy injects the SHARED fleet token as the Bearer. This wire
/// test runs an auth-enforcing stub Director (accepts only the fleet token) behind a real Gateway
/// and proves the forward authenticates; a stub that demands a DIFFERENT token returns 401, proving
/// the injected Bearer (not a copied browser header) is what authenticates.
/// </summary>
public sealed class ProxyInjectsFleetTokenTests : IAsyncLifetime
{
    private const string FleetToken = "fleet-secret-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private AuthStubDirector _director = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // The Gateway's own token IS the fleet token - exactly the production shape where the
        // Director's gateway.token equals the Gateway's token.
        _gateway = new GatewayHost(port: FreePort(), token: FleetToken, authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FleetToken);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        if (_director is not null) await _director.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    private async Task RegisterDirectorAsync()
    {
        var req = new DirectorRegistrationRequest
        {
            DirectorId = _director.DirectorId,
            TailnetEndpoint = _director.BaseUrl,
            Pid = 4321,
            MachineName = Environment.MachineName, // same-machine loopback stub (issue #457)
            User = "tester",
            Version = "test",
            StartedAt = DateTime.UtcNow,
        };
        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task Forward_authenticates_with_the_fleet_token()
    {
        _director = new AuthStubDirector(sessionId: "auth-session-1", requiredToken: FleetToken);
        await _director.StartAsync();
        await RegisterDirectorAsync();

        var resp = await _http.GetAsync("sessions/auth-session-1/git");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("{\"branch\":\"main\"}", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Forward_with_wrong_required_token_is_rejected_by_the_director()
    {
        // The Director demands a token the Gateway does NOT hold -> the injected fleet Bearer does
        // not match -> the Director 401s. Proves auth is actually enforced end-to-end on the forward.
        _director = new AuthStubDirector(sessionId: "auth-session-2", requiredToken: "a-different-token");
        await _director.StartAsync();
        await RegisterDirectorAsync();

        var resp = await _http.GetAsync("sessions/auth-session-2/git");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>A stub Director that enforces a Bearer token on every route except /sessions/{sid}
    /// ownership resolution (so the Gateway's fan-out can find it) and answers /sessions/{sid}/git.</summary>
    private sealed class AuthStubDirector : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string BaseUrl { get; private set; } = "";

        private readonly string _sessionId;
        private readonly string _requiredToken;
        private WebApplication? _app;

        public AuthStubDirector(string sessionId, string requiredToken)
        {
            _sessionId = sessionId;
            _requiredToken = requiredToken;
        }

        public async Task StartAsync()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "AuthStubDirector" });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Logging.ClearProviders();
            builder.Services.AddRoutingCore();

            _app = builder.Build();

            // Bearer gate on everything except ownership resolution (the fan-out probe must reach it
            // to learn the owner; ownership is not sensitive). The git verb requires the token.
            _app.Use(async (ctx, next) =>
            {
                var path = ctx.Request.Path.Value ?? "";
                var isOwnershipProbe = path == $"/sessions/{_sessionId}";
                if (!isOwnershipProbe)
                {
                    var auth = ctx.Request.Headers.Authorization.ToString();
                    if (auth != $"Bearer {_requiredToken}")
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }
                await next(ctx);
            });
            _app.UseRouting();

            _app.MapGet("/sessions/{sid}", (string sid) =>
                sid == _sessionId
                    ? Results.Json(new SessionDto { SessionId = _sessionId, Agent = "ClaudeCode", ActivityState = "Idle", StatusColor = "green" })
                    : Results.NotFound());

            _app.MapGet("/sessions/{sid}/git", () => Results.Text("{\"branch\":\"main\"}", "application/json"));

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
