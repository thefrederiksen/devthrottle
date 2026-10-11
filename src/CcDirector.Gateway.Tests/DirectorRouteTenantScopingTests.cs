using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// MTR-01: cross-tenant RCE / read / control via BARE director ids, proven over the REAL mapped endpoints and
/// the REAL tunnel.
///
/// Before this fix ~20 <c>/directors/{id}/*</c> routes and the <c>/interrupted</c> plane resolved a Director by
/// a bare id that scanned EVERY tenant's entries and returned the freshest match. So tenant A, holding its own
/// valid device key, could enumerate tenant B's director id and then <c>POST /directors/{id}/sessions</c> to
/// spawn a coding agent on B's machine, read B's repos/handovers/filesystem, overwrite B's settings, or stop
/// B's Director. The fix deletes the bare-id registry accessor and forces every per-director route through the
/// tenant-scoped <c>DirectorRegistry.Get(tenant, id)</c>, resolved from the request's authenticated device key.
///
/// The sharp proof is the SAME director id under TWO tenants: <c>dir-shared</c> exists in both tenant-alice and
/// tenant-bob. A caller naming it can only ever reach ITS OWN "dir-shared", never the other tenant's - the id is
/// identical, only the authenticated tenant differs. A "no command reached the other tenant" assertion is made
/// directly: each fake Director records every verb the Gateway sends it, and B's Director records NOTHING for
/// A's requests.
///
/// Revert-prove: restore the bare <c>DirectorRegistry.Get(string)</c> overload and point the routes back at it,
/// and A's spawn lands on (or at minimum reaches) B's Director / the foreign-id read returns 200 - so the
/// "DoesNotContain create/repos-list on B" and the 404 assertions go RED.
///
/// This drives the REAL auth middleware (which stashes the authenticated device key) and the REAL tunnel Hello
/// (which binds each Director's tenant), like <c>SessionServingReadIsolationTests</c>.
/// </summary>
public sealed class DirectorRouteTenantScopingTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    // Two tenants, one shared director id: dir-shared exists in BOTH alice and bob.
    private FakeTunnelDirector _dirAShared = null!; // tenant-alice, id "dir-shared"
    private FakeTunnelDirector _dirBShared = null!; // tenant-bob,   id "dir-shared" (SAME id)
    private FakeTunnelDirector _dirBOnly = null!;   // tenant-bob,   id "dir-b-only" (only bob has it)

    // Every verb the Gateway sends each Director over the tunnel, so "no command reached the other tenant" is a
    // direct assertion, not an inference from a status code.
    private readonly ConcurrentQueue<string> _aSharedVerbs = new();
    private readonly ConcurrentQueue<string> _bSharedVerbs = new();
    private readonly ConcurrentQueue<string> _bOnlyVerbs = new();

    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";
    private TenantId _tenantA;
    private TenantId _tenantB;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-mtr01-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two accounts: two device keys, each bound to its OWN tenant, plus one registered-but-unbound key.
        var deviceA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", "MA");
        var deviceB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", "MB");
        _tenantA = deviceA.Tenant;
        _tenantB = deviceB.Tenant;
        _keyA = deviceA.DeviceKey;
        _keyB = deviceB.DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;

        // Each Director authenticates with its OWN device key -> the tunnel Hello binds its tenant -> its entry
        // lands in that tenant's partition. dir-shared is registered under BOTH tenants (the sharp case).
        _dirAShared = await FakeTunnelDirector.StartAsync(_gateway, _keyA, "dir-shared", "MA", dispatch: Recorder(_aSharedVerbs));
        _dirBShared = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-shared", "MB", dispatch: Recorder(_bSharedVerbs));
        _dirBOnly = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-b-only", "MB", dispatch: Recorder(_bOnlyVerbs));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dirAShared.DisposeAsync();
        await _dirBShared.DisposeAsync();
        await _dirBOnly.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Spawn_on_a_shared_director_id_reaches_only_the_callers_own_director()
    {
        // The RCE vector: POST /directors/{id}/sessions spawns a coding agent on that Director's machine. A and
        // B both hold a Director with the IDENTICAL id "dir-shared"; A's spawn must land on A's OWN Director and
        // never on B's. This is the whole exploit closed: same id, different tenant, no crossing.
        var resp = await Post("directors/dir-shared/sessions", _keyA,
            new NewSessionRequest { RepoPath = "/repo", Agent = "claude" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Contains("create", _aSharedVerbs);       // A's own Director ran the spawn
        Assert.DoesNotContain("create", _bSharedVerbs);  // B's same-id Director NEVER saw the command
    }

    [Fact]
    public async Task A_foreign_director_id_is_404_and_no_command_is_sent()
    {
        // An id that exists ONLY in tenant B. Tenant A naming it is refused AT the registry gate (404), and B's
        // Director is never reached over the tunnel.
        var crossTenant = await Get("directors/dir-b-only/repos", _keyA);
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.DoesNotContain("repos-list", _bOnlyVerbs);

        // Positive control: B's OWN key reaches its Director, proving the id is real and routable - so the 404
        // above is the tenant gate, not a missing Director.
        var sameTenant = await Get("directors/dir-b-only/repos", _keyB);
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);
        Assert.Contains("repos-list", _bOnlyVerbs);
    }

    [Fact]
    public async Task A_write_route_on_a_foreign_director_id_is_404_and_spawns_nothing()
    {
        // The same guarantee on the WRITE/RCE path: A cannot spawn on an id it does not own, and B's Director
        // gets no create command.
        var resp = await Post("directors/dir-b-only/sessions", _keyA,
            new NewSessionRequest { RepoPath = "/repo", Agent = "claude" });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain("create", _bOnlyVerbs);
    }

    [Fact]
    public async Task A_device_key_with_no_bound_tenant_is_denied()
    {
        // Deny-by-default: a tenant-unbound hosted credential is invalid and rejected by authentication before
        // any per-director route can fall back to the Local partition - read OR write.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("directors/dir-shared/repos", _keyUnbound)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post("directors/dir-shared/sessions", _keyUnbound, new NewSessionRequest { RepoPath = "/repo" })).StatusCode);
    }

    [Fact]
    public async Task Interrupted_plane_fans_out_only_to_the_callers_own_directors()
    {
        // GET /interrupted used the fleet-global director list, so it enumerated and queried every tenant's
        // Directors. Scoped to the caller's tenant, A's request reaches only A's Directors' crash journals; B's
        // Directors are never queried over the tunnel.
        var resp = await Get("interrupted", _keyA);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Contains("interrupted-list", _aSharedVerbs);        // A's own Director was queried
        Assert.DoesNotContain("interrupted-list", _bSharedVerbs);  // B's same-id Director never was
        Assert.DoesNotContain("interrupted-list", _bOnlyVerbs);    // nor B's other Director
    }

    // ===== Codex round 1: the three MISSED surfaces =====

    [Fact]
    public async Task Backfill_on_a_shared_director_id_reaches_only_the_callers_own_director()
    {
        // The director-scoped command surface POST /directors/{id}/backfill-numbers used to dispatch the bare
        // id straight over the tunnel. Gated on the owned Director, A's backfill lands on A's OWN dir-shared and
        // never on B's same-id Director.
        var resp = await PostEmpty("directors/dir-shared/backfill-numbers", _keyA);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("backfill-numbers", _aSharedVerbs);
        Assert.DoesNotContain("backfill-numbers", _bSharedVerbs);
    }

    [Fact]
    public async Task Backfill_on_a_foreign_director_id_is_404_and_no_command_is_sent()
    {
        // An id that exists only in tenant B. A naming it is refused at the registry gate (404) BEFORE dispatch,
        // so B's Director is never sent the backfill verb over the tunnel.
        var resp = await PostEmpty("directors/dir-b-only/backfill-numbers", _keyA);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain("backfill-numbers", _bOnlyVerbs);

        // Positive control: B's own key reaches its Director, proving the id is real and routable.
        var sameTenant = await PostEmpty("directors/dir-b-only/backfill-numbers", _keyB);
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);
        Assert.Contains("backfill-numbers", _bOnlyVerbs);
    }

    [Fact]
    public async Task Backfill_with_no_bound_tenant_is_denied()
    {
        // Deny-by-default: a tenant-unbound hosted credential is rejected by authentication, never falls back
        // to the Local partition on the backfill command surface, and dispatches no verb.
        var resp = await PostEmpty("directors/dir-shared/backfill-numbers", _keyUnbound);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.DoesNotContain("backfill-numbers", _aSharedVerbs);
        Assert.DoesNotContain("backfill-numbers", _bSharedVerbs);
    }

    [Fact]
    public async Task Event_ring_is_isolated_by_tenant_for_the_same_director_id()
    {
        // The Local-shadow read: the event ring was one global queue per bare id, so tenant A could read tenant
        // B's ring for the same id. Keyed by (tenant, id), A's ring and B's ring for the IDENTICAL id
        // "dir-shared" are distinct queues. Seed both, then prove each account reads only its own.
        _gateway.DirectorEvents.Record(_tenantA, "dir-shared", "sess-alice", DoorbellEvents.CronRunCompleted, "started");
        _gateway.DirectorEvents.Record(_tenantB, "dir-shared", "sess-bob", DoorbellEvents.CronRunCompleted, "started");

        var aBody = await (await Get("directors/dir-shared/events", _keyA)).Content.ReadAsStringAsync();
        Assert.Contains("sess-alice", aBody);
        Assert.DoesNotContain("sess-bob", aBody);

        var bBody = await (await Get("directors/dir-shared/events", _keyB)).Content.ReadAsStringAsync();
        Assert.Contains("sess-bob", bBody);
        Assert.DoesNotContain("sess-alice", bBody);
    }

    [Fact]
    public async Task Event_ring_read_on_a_foreign_director_id_is_404()
    {
        // Even with a seeded ring for the foreign id under B, A naming dir-b-only is refused at the registry
        // gate (404) - A does not own that Director, so it can never reach that ring.
        _gateway.DirectorEvents.Record(_tenantB, "dir-b-only", "sess-bob", DoorbellEvents.CronRunCompleted, "started");

        Assert.Equal(HttpStatusCode.NotFound, (await Get("directors/dir-b-only/events", _keyA)).StatusCode);

        // Positive control: B's own key reads its own ring for that id.
        var bBody = await (await Get("directors/dir-b-only/events", _keyB)).Content.ReadAsStringAsync();
        Assert.Contains("sess-bob", bBody);
    }

    [Fact]
    public async Task Event_ring_read_with_no_bound_tenant_is_denied()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("directors/dir-shared/events", _keyUnbound)).StatusCode);
    }

    [Fact]
    public async Task Events_feed_for_tenant_a_does_not_announce_tenant_b_director()
    {
        // Subscribe both accounts before B adds a new Director. B is the positive control that proves the real
        // server-sent-events route is attached and publishing; A must see no event for the same registry change.
        using var tenantASubscription = await SubscribeToEventsAsync(_keyA);
        using var tenantBSubscription = await SubscribeToEventsAsync(_keyB);
        using var tenantAReader = new StreamReader(await tenantASubscription.Content.ReadAsStreamAsync());
        using var tenantBReader = new StreamReader(await tenantBSubscription.Content.ReadAsStreamAsync());

        await using var tenantBDirector = await FakeTunnelDirector.StartAsync(
            _gateway, _keyB, "dir-b-feed-only", "MB", dispatch: Recorder(new ConcurrentQueue<string>()));

        using var positiveControlTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tenantBEvent = await ReadNextEventDataAsync(tenantBReader, positiveControlTimeout.Token);
        Assert.Contains("dir-b-feed-only", tenantBEvent);

        using var isolationWindow = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ReadNextEventDataAsync(tenantAReader, isolationWindow.Token));
    }

    [Fact]
    public async Task Events_feed_with_no_bound_tenant_is_denied()
    {
        using var response = await SubscribeToEventsAsync(_keyUnbound);

        // MTR-14B contract: under the DB-authoritative device registry, a registered-but-unbound device is
        // not a valid credential on hosted (invalidHostedBinding -> Revoked), so it is denied at the auth gate
        // with 401 - it never authenticates far enough to reach the tenant boundary's 403. Either way the
        // isolation property holds: no bound tenant -> no access, no cross-tenant read. The denial simply moves
        // from the tenant layer (403) to the credential layer (401) because an unbound device cannot authenticate.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Events_feed_for_tenant_a_does_not_announce_tenant_b_director_removal()
    {
        await using var tenantBDirector = await FakeTunnelDirector.StartAsync(
            _gateway, _keyB, "dir-b-feed-remove", "MB", dispatch: Recorder(new ConcurrentQueue<string>()));

        using var tenantASubscription = await SubscribeToEventsAsync(_keyA);
        using var tenantBSubscription = await SubscribeToEventsAsync(_keyB);
        using var tenantAReader = new StreamReader(await tenantASubscription.Content.ReadAsStreamAsync());
        using var tenantBReader = new StreamReader(await tenantBSubscription.Content.ReadAsStreamAsync());

        var registered = _gateway.Registry.Get(_tenantB, "dir-b-feed-remove");
        Assert.NotNull(registered);
        registered.LastSeen = DateTime.UtcNow - DirectorRegistry.DefaultEvictionHorizon - TimeSpan.FromSeconds(1);
        _gateway.Registry.SweepStale();

        using var positiveControlTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tenantBEvent = await ReadNextEventDataAsync(tenantBReader, positiveControlTimeout.Token);
        Assert.Contains("dir-b-feed-remove", tenantBEvent);

        using var isolationWindow = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ReadNextEventDataAsync(tenantAReader, isolationWindow.Token));
    }

    [Fact]
    public async Task Legacy_discovery_plane_is_unavailable_on_hosted_and_leaves_no_shadow()
    {
        // The discovery legs (register / heartbeat / doorbell / unregister) are the same-machine HTTP plane and
        // the Local-shadow registration path. On hosted they are explicitly unavailable (403), which closes the
        // shadow: a hosted caller cannot fabricate a Local registration of another tenant's id, nor inject into
        // or delete a Local registration.
        var register = await Post("directors/register", _keyA, new DirectorRegistrationRequest
        {
            DirectorId = "shadow-x",
            TailnetEndpoint = "http://127.0.0.1:9/",
            MachineName = "attacker",
            Pid = 1,
            Version = "x",
            StartedAt = DateTime.UtcNow,
        });
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);

        // No-side-effect: the shadow id was never registered, in the attacker's tenant OR in Local.
        Assert.Null(_gateway.Registry.Get(_tenantA, "shadow-x"));
        Assert.Null(_gateway.Registry.Get(TenantId.Local, "shadow-x"));

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post("directors/dir-shared/heartbeat", _keyA, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post("directors/dir-shared/doorbell", _keyA,
                new DoorbellRequest { SessionId = "s", NewState = "Working", Event = DoorbellEvents.SessionCreated })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Delete("directors/dir-shared/registration", _keyA)).StatusCode);
    }

    // A dispatch that RECORDS every verb the Gateway sends this Director, then answers the read/create verbs
    // this test drives with an OK body so a route that DID reach it returns success (the positive controls).
    // The point is the verb log, not the body.
    private static Func<DirectorCommand, DirectorCommandResult> Recorder(ConcurrentQueue<string> log) =>
        cmd =>
        {
            log.Enqueue(cmd.Verb);
            return cmd.Verb switch
            {
                "create" => FakeTunnelDirector.Ok(new SessionDto
                {
                    SessionId = "new-sess",
                    Agent = "claude",
                    RepoPath = "/repo",
                    Status = "Running",
                    StatusColor = "blue",
                    CreatedAt = DateTime.UtcNow,
                    LastActivityAt = DateTime.UtcNow,
                }),
                "repos-list" => FakeTunnelDirector.Ok(Array.Empty<RepositoryDto>()),
                "interrupted-list" => FakeTunnelDirector.Ok(Array.Empty<CrashJournalDto>()),
                _ => FakeTunnelDirector.Ok(new { ok = true }),
            };
        };

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> SubscribeToEventsAsync(string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }

    private static async Task<string> ReadNextEventDataAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                return line[6..];
        }

        throw new EndOfStreamException("The events feed closed before publishing an event.");
    }

    private Task<HttpResponseMessage> Post(string path, string deviceKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> PostEmpty(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> Delete(string path, string deviceKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

}
