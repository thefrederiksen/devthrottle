using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #268: the Gateway proxies the two raw per-session WebSocket legs (live Terminal stream
/// and dictation) to the owning Director; issue #317 adds the per-session screenshot-bytes leg
/// (<c>GET /sessions/{sid}/screenshots/file</c>) through the SAME resolution. These wire tests
/// drive a real <see cref="GatewayHost"/> over loopback HTTP and pin the resolution contract the
/// Cockpit relies on:
///   - an unknown session id returns 404 (no owning Director across the fleet);
///   - the new sid-scoped routes are explicit endpoints, so they win over the fallback Cockpit
///     proxy (they never fall through to the "Cockpit starting" interstitial);
///   - a session whose owner is KNOWN (recorded in <see cref="SessionOwnerCache"/>) but whose
///     Director is offline returns 503 (owner offline), not 404 - issue #288 / #268 AC4.
///
/// A genuine WebSocket upgrade to a live owning Director is exercised end-to-end in the
/// cross-machine proof run; here, with no Directors registered, the resolution leg is what is
/// observable, which is the part that decides 404 vs 503 vs proxy.
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
    [InlineData("sessions/00000000-0000-0000-0000-000000000000/screenshots/file?name=a.png")]
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
    [InlineData("sessions/00000000-0000-0000-0000-000000000000/screenshots/file?name=a.png")]
    public async Task Per_session_ws_routes_are_explicit_endpoints_not_the_fallback_proxy(string path)
    {
        // The explicit WS route owns this path; the fallback Cockpit proxy (dead port) would have
        // answered 503 "Cockpit starting". Proving it is 404 (resolution) proves precedence.
        var resp = await _http.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.DoesNotContain("Cockpit starting", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("dictate")]
    [InlineData("screenshots/file?name=a.png")]
    public async Task Known_session_with_offline_owner_returns_503_not_404(string leg)
    {
        // The aggregator (or a prior successful forward) recorded this session's owner, but no live
        // Director answers ownership now (none registered). That is "owner went offline", which must
        // be 503 - not 404 (unknown session) and not the dead-cockpit 503 interstitial. Issue #288.
        var sid = "11111111-1111-1111-1111-111111111111";
        _gateway.SessionOwners.Remember(sid, "dead-director-id");

        var resp = await _http.GetAsync($"sessions/{sid}/{leg}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("offline", body, StringComparison.OrdinalIgnoreCase);
        // It is OUR owner-offline 503, not the fallback Cockpit interstitial.
        Assert.DoesNotContain("Cockpit starting", body);
    }

    [Fact]
    public async Task Unknown_uncached_session_still_returns_404()
    {
        // Belt-and-suspenders: a session NOT in the owner cache and owned by no live Director stays 404.
        var resp = await _http.GetAsync("sessions/22222222-2222-2222-2222-222222222222/stream");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
