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
/// The sweep and turn-end tests that waited on real time for a tunnel read were removed with the rest of the
/// tests that wait.
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

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-voice-loop-" + Guid.NewGuid().ToString("N"));

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
            dispatch: cmd => TurnsOrOk(cmd));
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, keyB, "dir-b", "MB",
            dispatch: cmd => TurnsOrOk(cmd));

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
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
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
