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
/// Hosted Multi-Tenancy (hosted voice-serving): the Gateway's BACKGROUND VOICE loops are tenant-aware. Both
/// the idle voice sweep and the turn-end voice refresh resolve each session's OWNING tenant from the push
/// store and act only within it - a tunnel read for one tenant's session must never reach another tenant's
/// Director, or that Director could be asked to hand over a session whose audio belongs to someone else.
///
/// Two Directors connect on two DIFFERENT tenants over the real tunnel, each authenticated with its OWN
/// per-device key, exactly as the read/loop session-serving harnesses arrange. The tests drive the REAL
/// production functions - <c>GatewayHost.SweepVoiceSessionsAsync</c> and the real turn-end callback via the
/// live <c>TurnEndWatcher.Observe</c> transition - not a re-implementation.
///
/// Every absence claim is preceded by a POSITIVE CONTROL in the same test: we first prove the tunnel read DOES
/// arrive for the tenant that owns the session, then prove it never reaches the other tenant's Director.
/// Without that ordering a total failure to deliver anything would satisfy every "did not arrive" assertion
/// and the test would pass vacuously.
///
/// REVERT-PROOFS (each against the real production line):
///  - <c>SweepVoiceSessionsAsync</c>: replace its <c>_tenantPass.ForEachTenant</c> per-tenant pass with the
///    old single <c>TenantId.Local</c> pass and <see cref="Voice_sweep_reaches_only_the_owning_tenants_director"/>
///    goes RED - the Local partition holds no marked voice session, so the tunnel read that must reach dir-B is
///    never sent.
///  - the turn-end callback: replace its <c>signal.Tenant</c>-passed <c>IsVoiceSession</c>/<c>GenerateAsync</c>
///    (the owning tenant resolved BEFORE Observe and carried on the signal, MTR-10 Gap C) with the old
///    <c>TenantId.Local</c> and <see cref="Turn_end_voice_refresh_reaches_only_the_owning_tenants_director"/>
///    goes RED - IsVoiceSession is false in the Local partition, so no refresh is issued.
///
/// The assembly runs sequentially (TestParallelization), so toggling CC_GATEWAY_HOSTED here is safe; it is
/// reset in DisposeAsync.
/// </summary>
public sealed class VoiceServingLoopIsolationTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SessA = "voice-loop-sess-a";
    private const string SessB = "voice-loop-sess-b";
    // Account tenants are minted GUIDs in production (WingmanVoiceService refuses a non-GUID, non-Local tenant
    // as a voice-state partition), so the device bindings here use real GUID tenant ids, not friendly labels.
    private TenantId TenantA { get; set; }
    private TenantId TenantB { get; set; }

    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    private readonly ConcurrentQueue<DirectorCommand> _seenByA = new();
    private readonly ConcurrentQueue<DirectorCommand> _seenByB = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-voice-loop-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();

        var deviceA = HostedTestEnrollment.Enroll(
            _gateway, "sub-alice", "alice@example.com", "dev-a", "MA");
        var deviceB = HostedTestEnrollment.Enroll(
            _gateway, "sub-bob", "bob@example.com", "dev-b", "MB");
        TenantA = deviceA.Tenant;
        TenantB = deviceB.Tenant;

        // THE SESSION SUPERVISOR IS SWITCHED OFF HERE, and before the snapshots below are pushed, because
        // pushing a settled session IS a turn end and the supervisor answers every turn end with one read of
        // that session's live screen (it is looking for a dropped connection to recover from). That read is
        // the same "screen-grid" verb this test counts, on the tenant's OWN session: it is correct traffic,
        // and it made both halves of this proof dishonest. The absence below failed on dir-A's own supervisor
        // read, and the positive control on dir-B could have passed on B's supervisor read without the voice
        // sweep reaching anything. Switched off, the only screen read left in this Gateway is the voice
        // sweep's, which is the thing under test.
        _gateway.TenantSettingsResolver.SetSessionSupervisorEnabled(TenantA, false, DateTime.UtcNow);
        _gateway.TenantSettingsResolver.SetSessionSupervisorEnabled(TenantB, false, DateTime.UtcNow);

        var keyA = deviceA.DeviceKey;
        var keyB = deviceB.DeviceKey;

        // Each Director captures every command it receives. A "turns" read answers with an empty widget set so
        // the generation reaches its "nothing to narrate" branch and never attempts a live hosted brain call -
        // the ARRIVAL of the read at the right Director is the whole point, not what it produces.
        _dirA = await FakeTunnelDirector.StartAsync(_gateway, keyA, "dir-a", "MA",
            dispatch: cmd => { _seenByA.Enqueue(cmd); return TurnsOrOk(cmd); });
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, keyB, "dir-b", "MB",
            dispatch: cmd => { _seenByB.Enqueue(cmd); return TurnsOrOk(cmd); });

        // Both tenants have a settled session in their OWN partition; only B's is a marked voice session, so a
        // correctly tenant-scoped loop issues a voice read for B and NONE for A.
        await _dirA.PushSnapshotAsync(Sample(SessA));
        await _dirB.PushSnapshotAsync(Sample(SessB));
        _gateway.VoiceService!.Mark(TenantB, SessB);
    }

    public async Task DisposeAsync()
    {
        await _dirA.DisposeAsync();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Voice_sweep_reaches_only_the_owning_tenants_director()
    {
        // The narration reads the Gateway's STORE now, not a "turns" command on the tunnel (turn-push
        // mission), so B's words are seeded here and the tunnel command this waits for is the LIVE SCREEN
        // read the narration still makes - same routing, same tenant resolution, same proof.
        _gateway.SeedStoredConversationForTest(TenantB, "dir-b", SessB, ("User", "do the thing"), ("Assistant", "it is done"));

        // The REAL idle voice sweep - the production timer callback, not a helper.
        await _gateway.SweepVoiceSessionsAsync();

        // POSITIVE CONTROL FIRST: the sweep's tunnel read for B's marked voice session reached dir-B. If this
        // did not hold the absence assertion below would pass vacuously in a sweep that did nothing.
        Assert.NotNull(await WaitForVerb(_seenByB, "screen-grid", SessB));

        // ABSENCE: dir-A was never asked to read B's session, and TenantA's own pass (its session is not a
        // voice session) issued no voice read at all - so dir-A saw no live-screen read whatsoever. Scoped to
        // the VOICE read on purpose: dir-A does receive ordinary traffic for its OWN session (its role and
        // display state), and asserting it saw nothing at all would fail on work that is none of this test's
        // business.
        Assert.DoesNotContain(_seenByA, c => c.Verb == "screen-grid");
    }

    [Fact]
    public async Task Turn_end_voice_refresh_reaches_only_the_owning_tenants_director()
    {
        // Drive a REAL Working -> Waiting transition for B's session on dir-B through the live watcher, which
        // fires the production onSessionWorking (clear) then onTurnEnd (refresh) callbacks. Both resolve the
        // owning tenant from the director id the signal carries.
        // The narration reads the Gateway's STORE now, not a "turns" command on the tunnel (turn-push
        // mission). So the session's words are seeded here, and the tunnel command this waits for is the
        // LIVE SCREEN read the narration still makes - same routing, same tenant resolution, same proof.
        _gateway.SeedStoredConversationForTest(TenantB, "dir-b", SessB, ("User", "do the thing"), ("Assistant", "it is done"));
        _gateway.TurnEndWatcherForTest!.Observe(TenantB, SessB, "Working", "dir-b");
        _gateway.TurnEndWatcherForTest!.Observe(TenantB, SessB, "WaitingForInput", "dir-b");

        // POSITIVE CONTROL FIRST: the turn-end refresh's tunnel read for B's session reached dir-B.
        Assert.NotNull(await WaitForVerb(_seenByB, "screen-grid", SessB));

        // ABSENCE: dir-A was never asked anything about B's session.
        Assert.DoesNotContain(_seenByA, c => c.SessionId == SessB);
    }

    [Fact]
    public void Voice_state_is_partitioned_per_tenant()
    {
        // A completed narration leaves a ready clip in exactly ONE tenant's partition. Seeding it under B and
        // asserting it is invisible to Local and to A is the direct proof that B's audio can never be read for
        // another tenant - the property the sweep/turn-end routing above protects at the boundary.
        _gateway.VoiceService!.StoreReadyAudioForTest(TenantB, SessB, "spoken", "reply", new byte[] { 1, 2, 3 });

        Assert.True(_gateway.VoiceService.HasVoice(TenantB, SessB));         // present for its own tenant
        Assert.False(_gateway.VoiceService.HasVoice(TenantId.Local, SessB)); // never the Local partition
        Assert.False(_gateway.VoiceService.HasVoice(TenantA, SessB));        // never another account's tenant
    }

    /// <summary>
    /// Poll for a verb+session rather than sleeping a fixed time: the loop fires generation onto the thread
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
        // Settled at a turn end - the idle sweep only pre-builds Idle / WaitingForInput / WaitingForPerm.
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = "red",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

}
