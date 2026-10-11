using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (MTR-10 Gap C): the turn-end tracker's transition memory
/// (<c>TurnEndWatcher._lastActivity</c>) is keyed by (tenant, sessionId), NEVER the bare session id. Two
/// accounts can run sessions that happen to share an id; a bare-sid key let one tenant's last-seen state
/// suppress - or fabricate - the OTHER tenant's Working -> Waiting boundary, and so its voice auto-refresh.
///
/// This drives the REAL production path: two Directors on two DIFFERENT tenants over the real tunnel, each
/// authenticated with its own per-device key, and the live <c>TurnEndWatcher.Observe</c> transition wired
/// exactly as <see cref="GatewayHost"/> installs it (onTurnEnd -> voice refresh, resolving from the owning
/// tenant carried on the signal). Both tenants own a session with the SAME id, and both are marked voice
/// sessions, so a correctly-partitioned watcher fires an independent turn-end refresh for EACH tenant's own
/// Director.
///
/// REVERT-PROOF against the production keying: change <c>_lastActivity</c> back to a bare
/// <c>ConcurrentDictionary&lt;string, string&gt;</c> keyed by session id alone and
/// <see cref="Same_sid_two_tenants_each_turn_end_reaches_only_its_own_director"/> goes RED - tenant B writing
/// "WaitingForInput" into the shared key first means tenant A's real Working -> Waiting transition sees no
/// change and its refresh is suppressed, so dir-A is never asked to read session "s".
/// </summary>
public sealed class TurnEndWatcherTenantIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";
    // The COLLIDING id: both accounts run a session called "s".
    private const string SharedSid = "s";
    private TenantId TenantA { get; set; }
    private TenantId TenantB { get; set; }

    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    private readonly ConcurrentQueue<DirectorCommand> _seenByA = new();
    private readonly ConcurrentQueue<DirectorCommand> _seenByB = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-turnend-tenant-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();

        var deviceA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", "MA");
        var deviceB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", "MB");
        TenantA = deviceA.Tenant;
        TenantB = deviceB.Tenant;
        var keyA = deviceA.DeviceKey;
        var keyB = deviceB.DeviceKey;

        _dirA = await FakeTunnelDirector.StartAsync(_gateway, keyA, "dir-a", "MA",
            dispatch: cmd => { _seenByA.Enqueue(cmd); return TurnsOrOk(cmd); });
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, keyB, "dir-b", "MB",
            dispatch: cmd => { _seenByB.Enqueue(cmd); return TurnsOrOk(cmd); });

        // Each tenant owns a session with the SAME id, in its OWN partition, and both are marked voice
        // sessions so a correctly-scoped turn-end fires a refresh for each.
        await _dirA.PushSnapshotAsync(Sample(SharedSid));
        await _dirB.PushSnapshotAsync(Sample(SharedSid));
        _gateway.VoiceService!.Mark(TenantA, SharedSid);
        _gateway.VoiceService!.Mark(TenantB, SharedSid);
    }

    public async Task DisposeAsync()
    {
        await _dirA.DisposeAsync();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Same_sid_two_tenants_each_turn_end_reaches_only_its_own_director()
    {
        var watcher = _gateway.TurnEndWatcherForTest!;

        // The narration reads the Gateway's STORE now, not a "turns" command on the tunnel (turn-push
        // mission), so each tenant's words are seeded into its own partition and the tunnel command this
        // waits for is the LIVE SCREEN read the narration still makes. Same routing, same tenant resolution,
        // same proof - and seeding BOTH tenants under the shared id is itself part of the point.
        _gateway.SeedStoredConversationForTest(TenantA, "dir-a", SharedSid, ("User", "ask A"), ("Assistant", "A answered"));
        _gateway.SeedStoredConversationForTest(TenantB, "dir-b", SharedSid, ("User", "ask B"), ("Assistant", "B answered"));

        // The interleaving that a bare-sid key gets wrong. Tenant A starts working; tenant B (same id) also
        // works then ENDS its turn first - writing "WaitingForInput" into the shared key. Then tenant A ends
        // its own turn. With a per-tenant key each transition is A's or B's alone; with a bare key A's real
        // transition sees the key already at "WaitingForInput" (from B) and fires nothing.
        watcher.Observe(TenantA, SharedSid, "Working", "dir-a");           // A working
        watcher.Observe(TenantB, SharedSid, "Working", "dir-b");           // B working (bare key: a no-op)
        watcher.Observe(TenantB, SharedSid, "WaitingForInput", "dir-b");   // B turn end -> refresh dir-B
        watcher.Observe(TenantA, SharedSid, "WaitingForInput", "dir-a");   // A turn end -> refresh dir-A

        // POSITIVE CONTROL: B's turn end reached its own Director, so the watcher is live and the harness works.
        Assert.NotNull(await WaitForVerb(_seenByB, "screen-grid", SharedSid));

        // THE PARTITION PROOF: A's turn end reached dir-A too - it was NOT suppressed by B sharing the id. A
        // bare-sid key reddens exactly here (A's transition is swallowed, dir-A sees no narration read).
        Assert.NotNull(await WaitForVerb(_seenByA, "screen-grid", SharedSid));
    }

    /// <summary>
    /// Poll for a verb+session rather than sleeping a fixed time: the refresh fires generation onto the thread
    /// pool and the tunnel round-trip is asynchronous, so a fixed wait would be either flaky or slow.
    /// </summary>
    private static async Task<DirectorCommand?> WaitForVerb(ConcurrentQueue<DirectorCommand> seen, string verb, string sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var hit = seen.FirstOrDefault(c => c.Verb == verb && c.SessionId == sessionId);
            if (hit is not null) return hit;
            await Task.Delay(50);
        }
        return null;
    }

    // A "turns" read answers with an empty widget set (no text reply -> generation stops before any hosted
    // call); everything else is a harmless ok.
    private static DirectorCommandResult TurnsOrOk(DirectorCommand cmd) =>
        cmd.Verb == "turns"
            ? FakeTunnelDirector.Ok(new { widgets = Array.Empty<object>() })
            : FakeTunnelDirector.Ok(new { ok = true });

    private static SessionDto Sample(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = "red",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

}
