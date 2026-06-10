using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #268: the Gateway proxies the two raw per-session WebSocket legs (live Terminal stream
/// and dictation) to the owning Director. These wire tests drive a real <see cref="GatewayHost"/>
/// over loopback HTTP and pin the resolution contract the Cockpit relies on:
///   - an unknown session id returns 404 (no owning Director across the fleet);
///   - the new sid-scoped routes are explicit endpoints, so they win over the fallback Cockpit
///     proxy (they never fall through to the "Cockpit starting" interstitial).
///
/// A genuine WebSocket upgrade to a live owning Director (and the 503 unreachable-Director path)
/// is exercised end-to-end in the cross-machine proof run; here, with no Directors registered,
/// the resolution leg is what is observable, which is the part that decides 404 vs proxy.
/// </summary>
public sealed class SessionWsProxyEndpointsTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // Dead Cockpit port: anything that fell through to the fallback proxy would answer the
        // 503 interstitial - so a NON-interstitial response proves the explicit WS route claimed it.
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: 1);
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    [Theory]
    [InlineData("sessions/00000000-0000-0000-0000-000000000000/stream")]
    [InlineData("sessions/00000000-0000-0000-0000-000000000000/dictate")]
    public async Task Unknown_session_returns_404_not_the_cockpit_interstitial(string path)
    {
        // No Directors registered -> no owner -> 404. (Plain GET, no WS upgrade header: the
        // resolution runs first and short-circuits with 404 before any upgrade is attempted.)
        var resp = await _http.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("not found", body, StringComparison.OrdinalIgnoreCase);
        // It must NOT have fallen through to the dead-cockpit interstitial.
        Assert.DoesNotContain("Cockpit starting", body);
    }

    [Theory]
    [InlineData("sessions/00000000-0000-0000-0000-000000000000/stream")]
    [InlineData("sessions/00000000-0000-0000-0000-000000000000/dictate")]
    public async Task Per_session_ws_routes_are_explicit_endpoints_not_the_fallback_proxy(string path)
    {
        // The explicit WS route owns this path; the fallback Cockpit proxy (dead port) would have
        // answered 503 "Cockpit starting". Proving it is 404 (resolution) proves precedence.
        var resp = await _http.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.DoesNotContain("Cockpit starting", await resp.Content.ReadAsStringAsync());
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
