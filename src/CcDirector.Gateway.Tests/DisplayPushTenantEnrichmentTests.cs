using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Hosted Multi-Tenancy (MTR-10 Gap D): the display-state PUSH seam's voice enrichment
/// (<c>FleetDisplayStateObserver</c> -> <c>GatewayHost.EnrichVoiceThenFoldForPush</c>) reads the AMBIENT
/// tenant of the per-tenant display pass, NOT <c>TenantId.Local</c>. The tenant-partitioned voice service is
/// live on hosted (#1973); a Local read there is an EMPTY partition, so the push-only desktop rail would fold
/// <c>VoiceAudioReady=false</c> for every session and hold every voice-mode session permanently "Preparing
/// voice" (yellow) while the roster - which resolves the request tenant - served red.
///
/// This drives the REAL production seam through the live DirectorHub push: two Directors on two DIFFERENT
/// tenants share a voice-mode session id, a ready clip exists in ONLY one tenant's partition, and each
/// Director's own snapshot push triggers the Gateway to fold and stamp the display state back down over the
/// tunnel. The fold that reaches each Director is the production <c>EnrichVoiceThenFoldForPush</c> closure
/// wired in <see cref="GatewayHost"/>, not a re-implementation.
///
/// REVERT-PROOF against the production enrichment: change the closure back to
/// <c>IsGenerating(TenantId.Local, sid)</c> / <c>HasVoice(TenantId.Local, sid)</c> and
/// <see cref="Voice_ready_folds_red_for_the_owning_tenant_yellow_for_the_other"/> goes RED - the owning
/// tenant's ready clip is invisible in the Local partition, so its desktop is stamped "yellow" (Preparing
/// voice) instead of "red".
/// </summary>
public sealed class DisplayPushTenantEnrichmentTests : IAsyncLifetime
{
    private const string Token = "test-token";
    // The COLLIDING id: both accounts run a voice-mode session called "s".
    private const string SharedSid = "s";
    private TenantId TenantA { get; set; }
    private TenantId TenantB { get; set; }

    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    private readonly ConcurrentQueue<DirectorCommand> _seenByA = new();
    private readonly ConcurrentQueue<DirectorCommand> _seenByB = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-display-tenant-" + Guid.NewGuid().ToString("N"));

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
            dispatch: cmd => { _seenByA.Enqueue(cmd); return FakeTunnelDirector.Ok(new { ok = true }); });
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, keyB, "dir-b", "MB",
            dispatch: cmd => { _seenByB.Enqueue(cmd); return FakeTunnelDirector.Ok(new { ok = true }); });

        // A ready voice clip exists in tenant B's partition ONLY. Seeded BEFORE any push so the very first fold
        // already sees it - the enrichment must find it for B and NOT for A (same session id).
        _gateway.VoiceService!.StoreReadyAudioForTest(TenantB, SharedSid, "spoken", "reply", new byte[] { 1, 2, 3 });
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
    public async Task Voice_ready_folds_red_for_the_owning_tenant_yellow_for_the_other()
    {
        // Each Director pushes its OWN voice-mode waiting session (same id "s"). The push triggers the Gateway
        // to fold that tenant's fleet and stamp the display state back down over the tunnel.
        await _dirB.PushSnapshotAsync(VoiceModeWaiting(SharedSid));
        await _dirA.PushSnapshotAsync(VoiceModeWaiting(SharedSid));

        // Tenant B OWNS the ready clip: its ambient-tenant enrichment finds VoiceAudioReady=true, so the fold
        // stamped down to dir-B is RED. Reverting the enrichment to TenantId.Local reads an empty partition and
        // this becomes "yellow" (Preparing voice) - the exact stuck-yellow this gap describes.
        Assert.Equal("red", await WaitForDisplayColor(_seenByB, SharedSid));

        // Tenant A has NO clip for the same id: its own partition read yields VoiceAudioReady=false, so its
        // desktop is honestly "yellow". Same id, different tenant, different fold - the per-tenant proof.
        Assert.Equal("yellow", await WaitForDisplayColor(_seenByA, SharedSid));
    }

    /// <summary>Wait for a set-display-state command for the session and return its folded EffectiveColor.</summary>
    private static async Task<string?> WaitForDisplayColor(ConcurrentQueue<DirectorCommand> seen, string sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var hit = seen.LastOrDefault(c => c.Verb == "set-display-state" && c.SessionId == sessionId);
            if (hit?.PayloadJson is { Length: > 0 } payload)
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("effectiveColor", out var color))
                    return color.GetString();
            }
            await Task.Delay(50);
        }
        return null;
    }

    // A voice-mode session settled at a turn end (raw red, waiting) with the Gateway-only voice booleans at
    // their default false - exactly what a Director pushes; the Gateway enriches VoiceAudioReady from its own
    // per-tenant voice store before folding.
    private static SessionDto VoiceModeWaiting(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = "red",
        VoiceMode = true,
        VoiceGenerating = false,
        VoiceAudioReady = false,
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

}
