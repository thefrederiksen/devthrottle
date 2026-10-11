using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (session-serving PR1): the cockpit READ path is tenant-aware end-to-end over real HTTP.
/// Two Directors connect on two DIFFERENT tenants, each authenticated with its OWN per-device key; the Gateway
/// resolves the request's tenant from that same authenticated key and serves ONLY that tenant's sessions:
///   - GET /sessions with device key A returns ONLY A's sessions, never B's;
///   - GET /sessions/{sid} for B's session, read with key A, is 404 (never located cross-tenant), while B's
///     own key reads it 200;
///   - a per-session tunnel leg (the WS-proxy screenshot list) for B's session, read with key A, is 503 (owner
///     not locatable under A's tenant), while B's own key reaches its Director over the tunnel;
///   - a request whose authenticated device key has NO bound tenant is DENIED 403 (deny-by-default), never
///     served the Local partition.
///
/// Revert-prove: change ResolveReadTenant (GatewayEndpoints) / ResolveTenantOrDeny (SessionWsProxyEndpoints)
/// back to TenantId.Local and key A reads the empty Local partition, so "A sees sess-a" and "A reaches its own
/// session" go RED (and the 403 deny collapses to an empty 200).
///
/// This drives the REAL mapped endpoints through the REAL auth middleware (which stashes the authenticated
/// device key) and the REAL tunnel Hello (which binds each Director's tenant) - it is the wired read path the
/// unit-level boundary tests (HostedTenancyActivationTests) do not exercise.
/// </summary>
public sealed class SessionServingReadIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SessA = "sess-a";
    private const string SessB = "sess-b";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ss-" + Guid.NewGuid().ToString("N"));
    private bool _cleanedUp;

    public async Task InitializeAsync()
    {
        try
        {
            await InitializeHostedAsync();
        }
        catch
        {
            await CleanupAsync();
            throw;
        }
    }

    private async Task InitializeHostedAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two accounts: two canonically minted tenants with atomically bound device keys, plus one
        // registered-but-unbound key that exercises the deny-by-default path.
        _keyA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", "MA").DeviceKey;
        _keyB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", "MB").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;

        // Each Director authenticates with its OWN device key -> the tunnel Hello binds its tenant -> its pushed
        // session lands in that tenant's partition. dir-B answers the screenshots-list verb so the same-tenant
        // WS-proxy leg proves it reaches its Director (the cross-tenant read is the 503 we assert on).
        _dirA = await FakeTunnelDirector.StartAsync(_gateway, _keyA, "dir-a", "MA");
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-b", "MB",
            dispatch: cmd => cmd.Verb == "screenshots-list"
                ? FakeTunnelDirector.Ok(new { items = Array.Empty<object>() })
                : DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}"));
        await _dirA.PushSnapshotAsync(Sample(SessA));
        await _dirB.PushSnapshotAsync(Sample(SessB));
    }

    public Task DisposeAsync() => CleanupAsync();

    private async Task CleanupAsync()
    {
        if (_cleanedUp)
            return;
        _cleanedUp = true;

        try
        {
            _http?.Dispose();
            if (_dirA is not null)
                await _dirA.DisposeAsync();
            if (_dirB is not null)
                await _dirB.DisposeAsync();
            if (_gateway is not null)
                await _gateway.StopAsync();
        }
        finally
        {
            try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task Sessions_roster_serves_only_the_requesting_tenants_sessions()
    {
        var seenByA = await SessionIds(_keyA);
        var seenByB = await SessionIds(_keyB);

        Assert.Contains(SessA, seenByA);
        Assert.DoesNotContain(SessB, seenByA);

        Assert.Contains(SessB, seenByB);
        Assert.DoesNotContain(SessA, seenByB);
    }

    [Fact]
    public async Task Sessions_envelope_never_names_another_tenants_director()
    {
        // The ?envelope response carries machineErrors + reachability, built from the fleet-global Director
        // registry. A tenant must never even see that another tenant's Director EXISTS: not as a session, and
        // not as an "unreachable" row leaking its id / machine name. Read the envelope as tenant A and assert
        // dir-b (tenant B's Director) appears NOWHERE in it.
        var resp = await Get("sessions?envelope=true", _keyA);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains(SessA, body);           // A's own session is present
        Assert.DoesNotContain(SessB, body);     // never B's session
        Assert.DoesNotContain("dir-b", body);   // never B's director id (machineErrors / reachability)
        Assert.DoesNotContain("\"MB\"", body);  // never B's machine name
    }

    [Fact]
    public async Task Directors_list_serves_only_the_requesting_tenants_directors()
    {
        // Issue #1847: the ENUMERATION surface. GET /directors was fleet-global while the by-id legs were
        // gated, so any authenticated account read back every other account's Director - id, machine name,
        // operating system user, process id, client version, liveness - and, holding the id, could address it.
        //
        // Revert-prove: change the handler back to registry.ListDirectors() (the fleet-global overload) and
        // A sees dir-b -> the DoesNotContain assertions go RED.
        var seenByA = await DirectorIds(_keyA);
        var seenByB = await DirectorIds(_keyB);

        Assert.Contains("dir-a", seenByA);
        Assert.DoesNotContain("dir-b", seenByA);

        Assert.Contains("dir-b", seenByB);
        Assert.DoesNotContain("dir-a", seenByB);

        // Not merely the ids: none of the other tenant's host or identity facts may appear in the body either.
        var bodyForA = await (await Get("directors", _keyA)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("dir-b", bodyForA);
        Assert.DoesNotContain("\"MB\"", bodyForA);
    }

    [Fact]
    public async Task Directors_list_denies_a_device_key_with_no_bound_tenant()
    {
        // Deny-by-default: a tenant-unbound hosted credential is invalid and refused at authentication. It
        // must never be served the Local partition or the fleet-global list.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("directors", _keyUnbound)).StatusCode);
    }

    [Fact]
    public async Task A_tenants_hello_cannot_overwrite_another_tenants_director_of_the_same_id()
    {
        // Issue #1847, the WRITE half - the worst of it. The tunnel Hello's director id is chosen by the
        // CLIENT, and the registry used to be keyed by that id alone, so tenant B - holding its own perfectly
        // valid device key - could say Hello naming tenant A's director id and overwrite A's entry with B's
        // machine name, operating system user, process id and client version. Keying the registry by
        // (tenant, id) makes that structurally impossible: B's Hello can only ever reach B's own partition.
        //
        // Revert-prove: change DirectorKey back to a bare director id (or drop the tenant from the key used by
        // RegisterFromStream) and B's Hello lands on A's entry -> the "A still reads MA" assertion goes RED.

        // Positive control BEFORE the attack: A really does have dir-a, and it really does say MA. Without
        // this, an assertion that A's entry is untouched would also pass if A had no entry at all.
        Assert.Equal("MA", (await DirectorFor(_keyA, "dir-a")).MachineName);

        // The attack: tenant B, authenticated as itself, claims tenant A's director id.
        await using var impostor = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-a", "MB-TAKEOVER");

        // Positive control ON THE ATTACK: the impostor's Hello really was accepted and really did register -
        // under B's OWN tenant. This is what stops the test passing merely because the impostor silently
        // failed to connect, which would make every "A is untouched" assertion vacuous.
        await WaitUntil(async () => (await DirectorFor(_keyB, "dir-a")).MachineName == "MB-TAKEOVER");

        // The takeover: A's entry still carries A's own facts, not B's.
        var aAfter = await DirectorFor(_keyA, "dir-a");
        Assert.Equal("MA", aAfter.MachineName);

        // The denial of service: A's Director has not been moved out of A's own list either.
        Assert.Contains("dir-a", await DirectorIds(_keyA));

        // And nothing of B's leaked into what A is served.
        var bodyForA = await (await Get("directors", _keyA)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("MB-TAKEOVER", bodyForA);
    }

    [Fact]
    public async Task Session_by_id_is_isolated_across_tenants()
    {
        // B's own key reads B's session; A's key cannot see B's session at all.
        Assert.Equal(HttpStatusCode.OK, (await Get($"sessions/{SessB}", _keyB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"sessions/{SessB}", _keyA)).StatusCode);

        // ...and symmetrically for A's session.
        Assert.Equal(HttpStatusCode.OK, (await Get($"sessions/{SessA}", _keyA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"sessions/{SessA}", _keyB)).StatusCode);
    }

    [Fact]
    public async Task WsProxy_leg_locate_is_tenant_scoped()
    {
        // The WS-proxy per-session legs resolve the owning Director from the pushed store under the REQUEST's
        // tenant. PR1 makes that LOCATE tenant-scoped; the relay that follows a successful locate lands in a
        // later slice, so this proves the locate line only - by which 503 body each request gets:
        //
        //  - A's key asking for B's session is NOT locatable under A's tenant, so it is rejected AT the locate
        //    with the honest "not connected right now ... will reconnect" reason (RejectAsync).
        //  - B's own key DOES locate B's session (tenant match), so it passes the locate and reaches the relay,
        //    which - until the relay slice - answers with the different "owning director is not connected" body.
        //    The point is that B's request never hits the not-locatable reject; the locate resolved its tenant.
        //
        // Revert-prove: change ResolveTenantOrDeny back to TenantId.Local and B's key locates nothing either
        // (B's session lives under tenant-bob, not Local), so B's request ALSO gets the "will reconnect" reject
        // -> the DoesNotContain below goes RED.
        var crossTenant = await Get($"sessions/{SessB}/screenshots?count=1", _keyA);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, crossTenant.StatusCode);
        Assert.Contains("reconnect", await crossTenant.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var sameTenant = await Get($"sessions/{SessB}/screenshots?count=1", _keyB);
        Assert.DoesNotContain("reconnect", await sameTenant.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_device_key_with_no_bound_tenant_is_denied()
    {
        // Deny-by-default: a tenant-unbound hosted credential is invalid and never falls back to Local.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("sessions", _keyUnbound)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get($"sessions/{SessA}", _keyUnbound)).StatusCode);
    }

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private async Task<string[]> SessionIds(string deviceKey)
    {
        var resp = await Get("sessions", deviceKey);
        resp.EnsureSuccessStatusCode();
        var sessions = await resp.Content.ReadFromJsonAsync<List<SessionDto>>(JsonOpts) ?? new();
        return sessions.Select(s => s.SessionId!).ToArray();
    }

    private async Task<string[]> DirectorIds(string deviceKey)
    {
        var resp = await Get("directors", deviceKey);
        resp.EnsureSuccessStatusCode();
        var directors = await resp.Content.ReadFromJsonAsync<List<DirectorDto>>(JsonOpts) ?? new();
        return directors.Select(d => d.DirectorId!).ToArray();
    }

    private async Task<DirectorDto> DirectorFor(string deviceKey, string directorId)
    {
        var resp = await Get("directors", deviceKey);
        resp.EnsureSuccessStatusCode();
        var directors = await resp.Content.ReadFromJsonAsync<List<DirectorDto>>(JsonOpts) ?? new();
        return Assert.Single(directors, d => d.DirectorId == directorId);
    }

    /// <summary>
    /// Readiness barrier for a condition the Gateway reaches asynchronously. A timeout is a HARD FAILURE, never
    /// a quiet pass - a barrier that gives up silently turns the assertion it guards into decoration.
    /// </summary>
    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try { if (await condition()) return; }
            catch (Xunit.Sdk.XunitException) { /* not there yet */ }
            await Task.Delay(50);
        }
        Assert.Fail("the condition was never reached before the deadline");
    }

    private static SessionDto Sample(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "Working",
        Status = "Running",
        StatusColor = "blue",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

}
