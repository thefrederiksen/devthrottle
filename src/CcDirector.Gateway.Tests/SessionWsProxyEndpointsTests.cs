using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Gateway Cleanup mission (the cut): the browser-facing per-session legs
/// (<c>GET /sessions/{sid}/stream</c>, <c>/file</c>, the per-session <c>/screenshots</c> list / bytes / delete)
/// and the director-scoped <c>POST /directors/{id}/backfill-numbers</c> ride THE TUNNEL now - there is no HTTP
/// dial to a Director left. The owning Director is resolved from the PUSH store, which only returns a
/// stream-connected, fresh Director.
///
/// These wire tests drive a real streamMode <see cref="GatewayHost"/> over loopback HTTP and pin the resolution
/// contract the Cockpit relies on AFTER the cut:
///   - a session whose owning Director is NOT tunnel-connected (nothing in the push store) returns 503 with a
///     clear "not connected" reason (the replacement for the old 404/owner-offline distinction) - the honest
///     offline signal, never a silent failure and never the SPA "Cockpit starting" interstitial;
///   - a session pushed by a tunnel-connected Director drives its finite legs (screenshot list / delete,
///     backfill-numbers) over unary tunnel verbs - proven tunnel-by-construction because the Director is
///     registered UNREACHABLE, so a working body could only have come over the tunnel;
///   - the sid-scoped routes are explicit endpoints, so they win over the fallback Cockpit SPA.
///
/// The live terminal/file/screenshot-BYTE stream legs ride the up-stream producer, exercised end-to-end in the
/// whole-surface real-exe proof; here the resolution + finite-verb dispatch is what is observable.
/// (The old <c>/sessions/{sid}/dictate</c> leg was DROPPED at the cut - dictation is client-&gt;Gateway audio now.)
/// </summary>
public sealed class SessionWsProxyEndpointsTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string DirectorId = "dir-wsproxy";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _director = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        // THE SESSION SUPERVISOR IS SWITCHED OFF. A pushed session that has stopped is a turn end, and the
        // supervisor answers it with one "screen-grid" read on its own task, so that read reaches the tunnel at
        // an unpredictable moment. It was captured here as the command a route sent - "expected
        // screenshot-delete, actual screen-grid" - on a different route each run.
        _gateway.TenantSettingsResolver.SetSessionSupervisorEnabled(
            CcDirector.Core.Tenancy.TenantId.Local, false, DateTime.UtcNow);

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        _director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, dispatch: cmd => cmd.Verb switch
        {
            "screenshots-list" => FakeTunnelDirector.Ok(new { items = Array.Empty<object>() }),
            "screenshot-delete" => FakeTunnelDirector.Ok(new { deleted = true }),
            "backfill-numbers" => FakeTunnelDirector.Ok(new { assigned = 3 }),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"),
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _director.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    // ---------- offline / unlocatable: the honest "not connected" signal ----------

    [Theory]
    [InlineData("sessions/00000000-0000-0000-0000-000000000000/stream")]
    [InlineData("sessions/00000000-0000-0000-0000-000000000000/file?path=x.txt")]
    [InlineData("sessions/00000000-0000-0000-0000-000000000000/screenshots?count=5")]
    [InlineData("sessions/00000000-0000-0000-0000-000000000000/screenshots/file?name=a.png")]
    public async Task Unlocatable_session_returns_503_not_connected_not_the_spa_interstitial(string path)
    {
        // No session pushed -> the owning Director is not tunnel-connected -> 503 with the "not connected"
        // reason. (Plain GET, no WS upgrade: the resolution runs first and short-circuits before any upgrade.)
        var resp = await _http.GetAsync(path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("not connected", body, StringComparison.OrdinalIgnoreCase);
        // It must NOT have fallen through to the dead-cockpit SPA interstitial.
        Assert.DoesNotContain("Cockpit starting", body);
    }

    [Fact]
    public async Task Dictate_leg_is_gone_after_the_cut()
    {
        // Dictation is client->Gateway audio now; the special dictation reverse-proxy WS leg was dropped.
        // /sessions/{sid}/dictate is no longer a distinct handshake - it is just an unmapped session verb, so
        // the generic tunnel catch-all handles it: for an owner that is not tunnel-connected it returns the
        // ordinary 503 "not connected" reject, never a dictation WebSocket upgrade.
        var resp = await _http.GetAsync("sessions/00000000-0000-0000-0000-000000000000/dictate");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Contains("not connected", await resp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------- located over the tunnel: finite verbs ride unary tunnel commands ----------

    [Fact]
    public async Task Screenshot_list_for_a_pushed_session_rides_the_tunnel()
    {
        var sid = Guid.NewGuid().ToString();
        await _director.PushSnapshotAsync(Session(sid));

        var resp = await _http.GetAsync($"sessions/{sid}/screenshots?count=5");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // The body is the verb result verbatim; a working body proves the tunnel (Director is unreachable).
        Assert.Contains("items", await resp.Content.ReadAsStringAsync());
        Assert.Equal("screenshots-list", _director.LastCommand?.Verb);
    }

    [Fact]
    public async Task Screenshot_delete_for_a_pushed_session_rides_the_tunnel()
    {
        var sid = Guid.NewGuid().ToString();
        await _director.PushSnapshotAsync(Session(sid));

        var resp = await _http.DeleteAsync($"sessions/{sid}/screenshots/file?name=a%20b.png");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("screenshot-delete", _director.LastCommand?.Verb);
    }

    [Fact]
    public async Task Backfill_numbers_rides_the_tunnel_by_director_id()
    {
        var resp = await _http.PostAsync($"directors/{DirectorId}/backfill-numbers", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("assigned", await resp.Content.ReadAsStringAsync());
        Assert.Equal("backfill-numbers", _director.LastCommand?.Verb);
    }

    [Fact]
    public async Task Backfill_numbers_for_an_unknown_director_id_returns_404()
    {
        // MTR-01 (Codex round 1): the backfill leg now resolves the owned Director in the request's tenant
        // BEFORE dispatch, so an id that is in no Director registry entry is a 404 at the gate - it never
        // reaches the tunnel dispatch (which is what used to answer an unknown id with a 503). On self-host
        // the request tenant is Local, so this is an ordinary present/absent lookup.
        var resp = await _http.PostAsync("directors/no-such-director/backfill-numbers", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static SessionDto Session(string sid) => new()
    {
        SessionId = sid,
        Agent = "ClaudeCode",
        Status = "WaitingForInput",
        ActivityState = "WaitingForInput",
    };

}
