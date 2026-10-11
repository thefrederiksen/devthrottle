using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading.Tasks;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1884, the WIRING canaries. The store, the mark and the phase rule are each pinned elsewhere
/// (<see cref="VoiceUploadStoreTenantPartitionTests"/>, <see cref="TranscribingSessionsTenantIsolationTests"/>,
/// <c>DictationPhase</c>'s own tests). What THOSE tests do not exercise is the PRODUCTION WIRING that selects
/// the tenant at each of the three seams - they call the store, the dictionary and the phase function
/// directly. So a bundle that reverts every dictation/transcribing call-site tenant argument back to
/// <see cref="TenantId.Local"/> - the transcribing route's mark, the roster's dictationStatusFor callback and
/// the voice-turn retention timer - left all of those direct tests green while re-opening the cross-account
/// hole. A test that supplies the tenant itself cannot catch a production line that supplies the WRONG tenant.
///
/// These three canaries close that gap by driving the ACTUAL production wiring end to end - a real
/// <see cref="GatewayHost"/> on hosted, two accounts over real HTTP, two tunnel Directors pushing the SAME
/// session id on two tenants - and asserting on what the caller's OWN request produces:
///
///   1. <see cref="Transcribing_mark_and_roster_are_isolated_per_tenant_over_the_real_route"/> drives
///      POST /sessions/{sid}/transcribing and reads the mark back through the roster's transcribingFor
///      callback. Revert the route's <c>Begin/End(reqTenant, sid)</c> (GatewayEndpoints, the transcribing
///      route) OR the roster's <c>transcribingFor(reqTenant.Value, ...)</c> to Local and the caller's own
///      positive control reddens; revert BOTH and the cross-tenant isolation assertions redden.
///
///   2. <see cref="A_hosted_tenants_pending_dictation_paints_its_OWN_roster_row_through_the_production_callback"/>
///      reads DictationStatus off the roster, which the GatewayHost dictationStatusFor callback
///      (<c>DictationStatusFor(tenant, sid, _transcribingSessions, _dictationUploads.ForTenant(tenant))</c>)
///      produces. Revert that callback to the Local/base handle (<c>_dictationUploads</c>) and the phase
///      collapses to null, because the base projection never descends into <c>base/tenants/&lt;id&gt;</c>.
///
///   3. <see cref="The_voice_turn_retention_timer_sweeps_each_tenants_aged_upload_in_its_OWN_partition"/>
///      stages an aged upload in each tenant's own voice-turn partition and lets the REAL retention timer
///      (the <c>_tenantPass.ForEachTenant(() =&gt; _voiceTurnUploads.ForTenant(tenant).SweepAbandoned(...))</c>
///      timer) run. Revert it to the base handle (<c>_voiceTurnUploads.SweepAbandoned(...)</c>) and neither
///      aged upload is ever swept, because the base sweep does not descend into the partition container.
///
/// The assembly runs sequentially, so toggling CC_DIRECTOR_ROOT and the static sweep schedule here is safe;
/// both are restored in DisposeAsync.
/// </summary>
public sealed class HostedDictationWiringCanaryTests : IAsyncLifetime
{
    private const string Token = "test-token";

    // ONE session id, pushed by BOTH Directors on BOTH tenants. That is what puts the same id in both
    // accounts' rosters - the exact shape a bare-session-id mark would cross - and what makes the per-tenant
    // sweep iterate both tenants (they are both "known").
    private const string Sid = "shared-session-id";

    private GatewayHost _gateway = null!;
    private HttpClient _httpA = null!;
    private HttpClient _httpB = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    private TenantId _tenantA;
    private TenantId _tenantB;

    private string? _priorRoot;
    private TimeSpan? _priorSweepSchedule;

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-wiring-canary-storage-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-wiring-canary-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // Isolate the storage root BEFORE the Gateway starts so its upload stores bind the temp root, never the
        // developer's real one - and so a store constructed here reads the SAME on-disk root the Gateway does.
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);

        // Drive the REAL voice-turn retention timer fast, so canary 3 exercises the production timer wiring
        // rather than a hand-called SweepAbandoned. Set before the host is constructed (the timer reads this
        // static at StartAsync); restored in DisposeAsync.
        _priorSweepSchedule = GatewayHost.VoiceTurnUploadSweepScheduleForTests;
        GatewayHost.VoiceTurnUploadSweepScheduleForTests = TimeSpan.FromMilliseconds(200);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();

        // Two accounts: two device keys, each bound to its OWN minted tenant. The tenants are minted by the
        // real registry (canonical GUIDs), the only shape the upload-store partition admits.
        var keyA = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var keyB = _gateway.Devices.Register("dev-b", "MB").DeviceKey;
        _tenantA = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _tenantB = _gateway.TenantRegistry.MintOrLookupBySubject("sub-bob", "bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", _tenantA.Value);
        _gateway.Devices.SetAccountBinding("dev-b", "sub-bob", _tenantB.Value);

        _httpA = NewClient(keyA);
        _httpB = NewClient(keyB);

        // One tunnel Director per account, each authenticated as that account's device key and pushing a
        // session under the SAME id. The push binds the session into that account's partition (and the account
        // into KnownTenants), so A's roster and B's roster each carry their own row for id Sid.
        _dirA = await FakeTunnelDirector.StartAsync(_gateway, keyA, "dir-a", "MA", dispatch: AnswerAnything);
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, keyB, "dir-b", "MB", dispatch: AnswerAnything);
        await _dirA.PushSnapshotAsync(Sample(Sid));
        await _dirB.PushSnapshotAsync(Sample(Sid));
    }

    private static DirectorCommandResult AnswerAnything(DirectorCommand cmd) =>
        FakeTunnelDirector.Ok(new { ok = true, lines = Array.Empty<string>(), items = Array.Empty<object>() });

    public async Task DisposeAsync()
    {
        _httpA.Dispose();
        _httpB.Dispose();
        await _dirA.DisposeAsync();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        GatewayHost.VoiceTurnUploadSweepScheduleForTests = _priorSweepSchedule;
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* cleanup */ }
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    // ===== canary 1: the transcribing mark, through the real route AND the real roster callback ==========

    [Fact]
    public async Task Transcribing_mark_and_roster_are_isolated_per_tenant_over_the_real_route()
    {
        // A marks its own session transcribing through the PRODUCTION route.
        Assert.Equal(HttpStatusCode.OK, (await SetTranscribingAsync(_httpA, Sid, true)).StatusCode);

        // Positive control, read through the production transcribingFor callback: A's OWN roster row is orange.
        // Reddens if the route's Begin(reqTenant, sid) OR the roster's transcribingFor(reqTenant.Value, ...) is
        // reverted onto Local - A's mark would then land under, or be read from, a tenant that is not A's.
        Assert.True((await RosterRow(_httpA, Sid)).Transcribing,
            "A must see its own transcribing mark on its own roster row");

        // Isolation: B - whose Director pushed the SAME session id - must NOT see A's mark on its own row.
        // Reddens if BOTH sites are reverted to Local, which re-merges the two accounts onto one shared mark.
        Assert.False((await RosterRow(_httpB, Sid)).Transcribing,
            "B must not see A's transcribing mark for the same session id");

        // B ending the mark on the same id through the route clears only B's (nonexistent) mark - A's stands.
        // Reddens under the both-reverted-to-Local bundle: B's End would then wipe the one shared mark A set.
        Assert.Equal(HttpStatusCode.OK, (await SetTranscribingAsync(_httpB, Sid, false)).StatusCode);
        Assert.True((await RosterRow(_httpA, Sid)).Transcribing,
            "B's clear on the same session id must not wipe A's transcribing mark");

        // A can still clear its own.
        Assert.Equal(HttpStatusCode.OK, (await SetTranscribingAsync(_httpA, Sid, false)).StatusCode);
        Assert.False((await RosterRow(_httpA, Sid)).Transcribing, "A can clear its own mark");
    }

    // ===== canary 2: the dictation phase, through the real roster dictationStatusFor callback ===========

    [Fact]
    public async Task A_hosted_tenants_pending_dictation_paints_its_OWN_roster_row_through_the_production_callback()
    {
        // A durable PENDING dictation in A's OWN dictation partition (base/tenants/<A>), plus a live progress
        // mark set through the real transcribing route, so the honest phase is "Uploading from phone".
        var uploadId = Guid.NewGuid().ToString("N");
        DictationStore(_tenantA).MarkPending(uploadId, Sid);
        Assert.Equal(HttpStatusCode.OK, (await SetTranscribingAsync(_httpA, Sid, true)).StatusCode);

        // A's roster row, read through the GatewayHost dictationStatusFor callback - the one that passes
        // _dictationUploads.ForTenant(caller) - reports the phase from A's own partition. Revert that callback
        // to the Local/base handle and this reddens: the base projection never descends into base/tenants/<A>,
        // so IsSessionLocked is false, undelivered is false, and DictationPhase.For collapses to null.
        Assert.Equal(DictationPhase.Uploading, (await RosterRow(_httpA, Sid)).DictationStatus);

        // Isolation half: B holds no pending for this id, so its own row carries no dictation status. A's
        // pending is not B's to read, even on the same session id.
        Assert.Null((await RosterRow(_httpB, Sid)).DictationStatus);
    }

    // ===== canary 3: the voice-turn retention sweep, through the real background timer =================

    [Fact]
    public async Task The_voice_turn_retention_timer_sweeps_each_tenants_aged_upload_in_its_OWN_partition()
    {
        // Both accounts are live (their Directors pushed sessions), so the per-tenant sweep iterates them. This
        // is the positive control for the wait below: if this were empty the sweep would run zero passes and the
        // aged uploads would linger for a reason that has nothing to do with the line under test.
        var known = _gateway.PushedSessions.KnownTenants();
        Assert.Contains(_tenantA, known);
        Assert.Contains(_tenantB, known);

        var storeA = VoiceTurnStore(_tenantA);
        var storeB = VoiceTurnStore(_tenantB);

        // An AGED voice-turn upload staged in each tenant's OWN partition (last-activity backdated well past the
        // 4-hour retention bound), plus a FRESH one that must survive - the age-selective positive control that
        // stops this passing on a blanket wipe.
        var agedA = StageAged(storeA);
        var agedB = StageAged(storeB);
        var freshA = Guid.NewGuid().ToString(); storeA.Register(freshA);
        var freshB = Guid.NewGuid().ToString(); storeB.Register(freshB);

        // Let the REAL production timer run. Revert it to the base handle (_voiceTurnUploads.SweepAbandoned)
        // and this never comes true: the base sweep does not descend into base/tenants/<id>, so neither aged
        // upload is ever removed and this times out RED.
        Assert.True(await PollAsync(() => !storeA.Exists(agedA) && !storeB.Exists(agedB), TimeSpan.FromSeconds(20)),
            "the production per-tenant voice-turn timer must sweep each tenant's aged upload from its OWN partition");

        Assert.True(storeA.Exists(freshA), "the sweep must not remove A's fresh upload (it is age-selective, not a wipe)");
        Assert.True(storeB.Exists(freshB), "the sweep must not remove B's fresh upload (it is age-selective, not a wipe)");
    }

    // ===== helpers =====================================================================================

    private static VoiceUploadStore DictationStore(TenantId tenant)
        => new VoiceUploadStore(CcStorage.DictationUploads(), TenantId.Local).ForTenant(tenant);

    private static VoiceUploadStore VoiceTurnStore(TenantId tenant)
        => new VoiceUploadStore(CcStorage.VoiceTurnUploads(), TenantId.Local).ForTenant(tenant);

    /// <summary>Stage an upload whose last-activity signal is backdated past the 4-hour retention bound, so
    /// the production sweep (which compares directory write time to now minus the max age) treats it as aged
    /// without any real waiting.</summary>
    private static string StageAged(VoiceUploadStore store)
    {
        var id = Guid.NewGuid().ToString();
        store.Register(id);
        var dir = Path.Combine(store.Root, Guid.Parse(id).ToString("N"));
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - TimeSpan.FromHours(5));
        return id;
    }

    private static Task<HttpResponseMessage> SetTranscribingAsync(HttpClient http, string sid, bool transcribing)
        => http.PostAsJsonAsync($"/sessions/{sid}/transcribing", new { transcribing });

    private static async Task<SessionDto> RosterRow(HttpClient http, string sid)
    {
        var resp = await http.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sessions = await resp.Content.ReadFromJsonAsync<List<SessionDto>>();
        Assert.NotNull(sessions);
        var row = sessions!.FirstOrDefault(s => string.Equals(s.SessionId, sid, StringComparison.Ordinal));
        Assert.True(row is not null, $"this tenant's roster must list session {sid}");
        return row!;
    }

    private static async Task<bool> PollAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static SessionDto Sample(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "Idle",
        Status = "Running",
        StatusColor = "blue",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

    private HttpClient NewClient(string deviceKey)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return http;
    }

}
