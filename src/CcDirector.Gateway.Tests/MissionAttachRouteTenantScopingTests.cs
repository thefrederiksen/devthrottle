using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #2387: <c>POST /sessions/{sid}/mission</c> - attaching a session that ALREADY EXISTS to a Mission -
/// scoped to the CALLER's own tenant, proven over the REAL mapped route, the REAL auth middleware and the
/// REAL tunnel, with two separately-enrolled accounts.
///
/// WHY THIS ROUTE GETS ITS OWN PARTITION FILE. Missions are tenant-scoped and that was hard won:
/// devthrottle_internal issue #1039 fixed a live leak where <c>GET /missions</c> served every account's list
/// to every account, and a mission NAME is free text a person typed - customer names, project names, people's
/// names. The read side is now gated. This route is the first WRITE that takes a mission id from the caller
/// and applies it, so it is exactly the shape that would put the leak back: a caller who could attach a
/// session to another account's mission would be writing that account's identifier onto its own session, and
/// reading the name back out of its own roster. It follows <c>GET /missions/{mid}</c> line for line - resolve
/// the caller's tenant, refuse when unbound, resolve the mission INSIDE that tenant - and these cases hold it
/// to that.
///
/// HOW THESE CASES ARE WRITTEN, which matters more than the count:
///
///  - Every refusal is paired with a PERMITTED request in the same test, in the same Gateway. A route that
///    refused everybody would satisfy every refusal assertion here while being entirely broken, so a refusal
///    on its own proves nothing at all.
///  - A refusal is asserted at TWO levels: the status code, and the fact that the owning Director was never
///    sent an <c>attach-mission</c> command over the tunnel. A status code alone cannot tell "refused" from
///    "attached, then reported an error" - and it is the WRITE reaching the Director that would be the breach.
///  - <see cref="The_attach_detector_itself_detects_an_attach_when_there_is_one"/> is the SELF-TEST. Every
///    refusal below rests on "no attach-mission reached that Director". That assertion is worth exactly as
///    much as its ability to come out TRUE, so one case points the same recorder at an attach that genuinely
///    happens and requires a positive. If it ever goes green-by-absence, nothing else in this file means
///    anything.
///
/// Revert-prove: drop the <c>ResolveReadTenant</c> gate (or resolve the mission with <c>TenantId.Local</c>)
/// in the route and <see cref="Another_tenants_mission_cannot_be_attached_to_your_own_session"/> goes RED -
/// A's attach to B's mission stops being a 400 and reaches A's Director carrying B's mission name.
/// </summary>
public sealed class MissionAttachRouteTenantScopingTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string SessA = "11111111-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string SessB = "22222222-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dirA = null!;
    private FakeTunnelDirector _dirB = null!;

    // Every attach-mission PAYLOAD each Director was sent over the tunnel. Recording the payload rather than
    // just the verb is deliberate: the breach this file guards is a mission id/name from another account
    // ARRIVING at a Director, so the evidence has to be able to show what was carried, not only that
    // something was.
    private readonly ConcurrentQueue<SetMissionRequest> _attachesOnA = new();
    private readonly ConcurrentQueue<SetMissionRequest> _attachesOnB = new();

    private string _keyA = "";
    private string _keyB = "";
    private string _keyUnbound = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-2387-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            missionsPath: Path.Combine(_instancesDir, "missions.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // Two accounts enrolled through the product's own hosted enrollment, each atomically bound to its own
        // canonically minted tenant, plus one registered-but-unbound key for the deny-by-default path.
        _keyA = HostedTestEnrollment.Enroll(_gateway, "sub-alice", "alice@example.com", "dev-a", "MA").DeviceKey;
        _keyB = HostedTestEnrollment.Enroll(_gateway, "sub-bob", "bob@example.com", "dev-b", "MB").DeviceKey;
        _keyUnbound = _gateway.Devices.Register("dev-x", "MX").DeviceKey;

        // Each Director authenticates with its OWN device key, so the tunnel Hello binds its tenant and its
        // pushed session lands in that tenant's partition. Each answers attach-mission with the session it was
        // told to attach - the shape the real Director core returns.
        _dirA = await FakeTunnelDirector.StartAsync(_gateway, _keyA, "dir-a", "MA",
            dispatch: Recorder(_attachesOnA, SessA));
        _dirB = await FakeTunnelDirector.StartAsync(_gateway, _keyB, "dir-b", "MB",
            dispatch: Recorder(_attachesOnB, SessB));
        await _dirA.PushSnapshotAsync(Sample(SessA));
        await _dirB.PushSnapshotAsync(Sample(SessB));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dirA.DisposeAsync();
        await _dirB.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task The_attach_detector_itself_detects_an_attach_when_there_is_one()
    {
        // THE SELF-TEST. Every refusal in this file is "no attach-mission was recorded on that Director".
        // Point the same recorder at an attach that genuinely happens and require a POSITIVE, otherwise a
        // recorder that never records anything would certify the whole file.
        var mission = await CreateMission(_keyA, "A mission the caller definitely owns");

        var resp = await Attach(SessA, _keyA, mission.MissionId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var recorded = Assert.Single(_attachesOnA);
        Assert.Equal(mission.MissionId, recorded.MissionId);

        // And the Gateway resolved the NAME before sending it, so the Director stamps what the Gateway
        // authorized instead of consulting its own (different, per-machine) mission store.
        Assert.Equal("A mission the caller definitely owns", recorded.MissionName);
    }

    [Fact]
    public async Task Another_tenants_mission_cannot_be_attached_to_your_own_session()
    {
        // THE CASE THIS FILE EXISTS FOR. B creates a mission whose NAME is the kind of free text #1039 was
        // about. A - holding its own perfectly valid device key, and naming a session it genuinely owns -
        // tries to attach that session to B's mission.
        var bMission = await CreateMission(_keyB, "Payments cutover for Northwind");

        var refused = await Attach(SessA, _keyA, bMission.MissionId);

        // The refusal: an id from another account is simply not a mission, and the error says so without
        // confirming that anybody else has it.
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Northwind", body);

        // And the write never happened: A's OWN Director was never sent the attach, so B's mission name
        // never travelled to A's machine and cannot be read back out of A's roster.
        Assert.Empty(_attachesOnA);

        // THE PERMITTED HALF, in the same Gateway and the same breath. A can attach that same session to a
        // mission of its OWN - so the refusal above is about the mission's owner, not a route that refuses
        // every attach.
        var aMission = await CreateMission(_keyA, "A's own mission");
        var allowed = await Attach(SessA, _keyA, aMission.MissionId);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(aMission.MissionId, Assert.Single(_attachesOnA).MissionId);
    }

    [Fact]
    public async Task Another_tenants_session_cannot_be_attached_to_your_own_mission()
    {
        // The other direction, and it is a different gate: the mission is the caller's, the SESSION is not.
        // A mission id is only half of an attachment - being able to name any session in the fleet would let
        // one account rearrange another account's work even without ever seeing a foreign mission.
        var aMission = await CreateMission(_keyA, "A's mission");

        var refused = await Attach(SessB, _keyA, aMission.MissionId);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Empty(_attachesOnB);   // B's Director never saw a command

        // The permitted half: B's OWN key reaches that same session, so the refusal is the tenant gate and
        // not a session that is unreachable for everybody.
        var bMission = await CreateMission(_keyB, "B's mission");
        var allowed = await Attach(SessB, _keyB, bMission.MissionId);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(bMission.MissionId, Assert.Single(_attachesOnB).MissionId);
    }

    [Fact]
    public async Task A_device_key_with_no_bound_tenant_attaches_nothing()
    {
        // Deny-by-default. A tenant-unbound hosted credential must never be served the Local partition, and
        // must reach no Director - not on the attach leg and not on the detach leg.
        var aMission = await CreateMission(_keyA, "A's mission");

        var attach = await Attach(SessA, _keyUnbound, aMission.MissionId);
        Assert.True(attach.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"an unbound device key was answered {(int)attach.StatusCode}, which is neither a refusal nor a denial");

        var detach = await Attach(SessA, _keyUnbound, null);
        Assert.True(detach.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"an unbound device key was answered {(int)detach.StatusCode} on the detach leg");

        Assert.Empty(_attachesOnA);
        Assert.Empty(_attachesOnB);

        // The permitted half: the same two calls with a BOUND key both land, so the denials above are about
        // the credential and not about the route being inert.
        Assert.Equal(HttpStatusCode.OK, (await Attach(SessA, _keyA, aMission.MissionId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Attach(SessA, _keyA, null)).StatusCode);
        Assert.Equal(2, _attachesOnA.Count);
    }

    [Fact]
    public async Task Detaching_carries_no_mission_and_needs_none()
    {
        // Detach is the null-mission call. It resolves nothing, so it cannot leak anything - but it must
        // still reach the Director as a real command, or "detach" would be a no-op that reports success.
        var resp = await Attach(SessA, _keyA, null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var recorded = Assert.Single(_attachesOnA);
        Assert.Null(recorded.MissionId);
        Assert.Null(recorded.MissionName);
    }

    [Fact]
    public async Task An_unknown_mission_id_and_another_tenants_mission_id_are_answered_alike()
    {
        // The two must be indistinguishable or the route becomes an existence oracle: attach a session to
        // every id you can guess, and the ones that answer differently are the ones somebody else owns.
        var bMission = await CreateMission(_keyB, "B's mission");

        var foreign = await Attach(SessA, _keyA, bMission.MissionId);
        var nonexistent = await Attach(SessA, _keyA, Guid.NewGuid());

        Assert.Equal(nonexistent.StatusCode, foreign.StatusCode);
        // Same status AND the same wording apart from the id echoed back, so the shape of the answer does
        // not distinguish them either.
        var foreignBody = (await foreign.Content.ReadAsStringAsync()).Replace(bMission.MissionId.ToString(), "<id>");
        var nonexistentBody = await nonexistent.Content.ReadAsStringAsync();
        Assert.Equal(foreignBody, ReplaceGuid(nonexistentBody));
        Assert.Empty(_attachesOnA);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    /// <summary>
    /// A fake Director that RECORDS every attach-mission payload it is sent and answers with the session it
    /// was told to attach. Anything else is a loud failure rather than a shrug: a verb this file did not
    /// expect arriving at a Director is a finding, not noise.
    /// </summary>
    private static Func<DirectorCommand, DirectorCommandResult> Recorder(
        ConcurrentQueue<SetMissionRequest> log, string sessionId) => cmd =>
    {
        if (cmd.Verb != "attach-mission")
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");

        var payload = JsonSerializer.Deserialize<SetMissionRequest>(cmd.PayloadJson ?? "", JsonOpts)
                      ?? new SetMissionRequest();
        log.Enqueue(payload);
        return FakeTunnelDirector.Ok(new SessionDto
        {
            SessionId = sessionId,
            MissionId = payload.MissionId,
            MissionName = payload.MissionName,
        });
    };

    private static string ReplaceGuid(string body)
    {
        // Swap whatever GUID the body echoes for the same placeholder the foreign-id body got, so the two
        // are compared on their WORDING and not on the id that necessarily differs.
        return System.Text.RegularExpressions.Regex.Replace(
            body, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "<id>");
    }

    private Task<HttpResponseMessage> Attach(string sid, string deviceKey, Guid? missionId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"sessions/{sid}/mission")
        {
            Content = JsonContent.Create(new SetMissionRequest { MissionId = missionId }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(req);
    }

    private async Task<MissionDto> CreateMission(string deviceKey, string name)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "missions")
        {
            Content = JsonContent.Create(new NewMissionRequest { MissionName = name }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<MissionDto>();
        Assert.NotNull(dto);
        return dto!;
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
    };
}

/// <summary>
/// Issue #2387, the review finding: THE WORKFLOW SEAT MOVES WITH THE MISSION.
///
/// A Mission is not only a record - it is also a RUN of the built-in "mission" workflow, and a
/// mission-scoped spawn seats the session on that run and records it in the run's participant ledger. The
/// seat is what pins the CONDUCT the agent was told to follow. The first cut of attach changed only
/// MissionId and MissionName, so a session moved from Mission A to Mission B was DISPLAYED under B while it
/// was still GOVERNED by A - taking its conduct from a mission it had left, and still sitting in A's
/// participant ledger as active. Detach had the same shape with no mission shown at all. That is not a
/// cosmetic inconsistency: the thing this feature exists to make visible would have been actively
/// misleading in exactly the case it was built for.
///
/// These cases drive the REAL route against a REAL run store, so they pin the DECISION (which is the
/// Gateway's, because only it knows whether a run belongs to a mission) and its consequence at the
/// Director and in the ledger. The rule and its one exception:
///  * a seat that IS a run of the mission being left follows the mission;
///  * a seat the caller chose independently is PRESERVED - it was never the mission's to take.
///
/// Revert-prove: delete the seat block from the attach route (leave only the mission fields on the payload)
/// and <see cref="Moving_a_mission_scoped_session_moves_its_workflow_seat"/> goes RED - the Director is sent
/// MoveSeat=false and keeps Mission A's run while its mission reads B, which is the defect verbatim.
/// </summary>
public sealed class MissionAttachSeatMoveTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string Sess = "33333333-cccc-4ccc-8ccc-cccccccccccc";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private FakeTunnelDirector _dir = null!;

    private readonly ConcurrentQueue<SetMissionRequest> _attaches = new();

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-2387-seat-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        // Self-host shape (one owner). The tenant partition of this route is proven next door in
        // MissionAttachRouteTenantScopingTests; what is under test here is the seat, and a single owner is
        // the cleanest place to see it.

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            missionsPath: Path.Combine(_instancesDir, "missions.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = false });
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        _dir = await FakeTunnelDirector.StartAsync(_gateway, Token, "dir-seat", "MS", dispatch: cmd =>
        {
            if (cmd.Verb != "attach-mission")
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {cmd.Verb}");
            var payload = JsonSerializer.Deserialize<SetMissionRequest>(cmd.PayloadJson ?? "", JsonOpts)
                          ?? new SetMissionRequest();
            _attaches.Enqueue(payload);
            return FakeTunnelDirector.Ok(new SessionDto
            {
                SessionId = Sess,
                MissionId = payload.MissionId,
                MissionName = payload.MissionName,
                WorkflowRunId = payload.MoveSeat ? payload.WorkflowRunId : null,
            });
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _dir.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Moving_a_mission_scoped_session_moves_its_workflow_seat()
    {
        var (missionA, runA) = await CreateMissionWithRun("Mission A");
        var (missionB, runB) = await CreateMissionWithRun("Mission B");

        // A session exactly as a mission-scoped spawn leaves it: in Mission A, seated on A's run.
        await PushSession(missionA, runA);

        var resp = await Attach(missionB);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var sent = Assert.Single(_attaches);
        Assert.Equal(missionB, sent.MissionId);
        // THE FINDING, at the decision point: the Gateway told the Director to move the seat, and named
        // Mission B's run - not Mission A's, which is what the session would otherwise have kept.
        Assert.True(sent.MoveSeat, "the Gateway did not tell the Director to move the seat, so the session "
                                   + "would stay governed by the mission it just left");
        Assert.Equal(runB, sent.WorkflowRunId);
        Assert.Equal("mission", sent.WorkflowId);
        Assert.NotNull(sent.WorkflowVersion);   // the PINNED version travels too, never a moving head
    }

    [Fact]
    public async Task Moving_a_session_updates_the_participant_ledger_on_both_runs()
    {
        var (missionA, runA) = await CreateMissionWithRun("Mission A");
        var (missionB, runB) = await CreateMissionWithRun("Mission B");
        await PushSession(missionA, runA);

        // The control, BEFORE the move: the session really is an active participant of A's run. Without
        // this the "no longer active on A" assertion below would also pass if it had never been on A.
        await JoinRun(runA, Sess);
        Assert.True(await IsActiveParticipant(runA, Sess), "the session was never on run A, so nothing below means anything");

        Assert.Equal(HttpStatusCode.OK, (await Attach(missionB)).StatusCode);

        // Left A, joined B. Leaving is RECORDED rather than erased - that the session was in A is true and
        // stays true - so the assertion is about being ACTIVE there, not about being absent from the list.
        Assert.False(await IsActiveParticipant(runA, Sess), "the session is still an active participant of the run it left");
        Assert.True(await IsActiveParticipant(runB, Sess), "the session did not join the run of the mission it moved to");
    }

    [Fact]
    public async Task A_seat_the_caller_chose_independently_is_preserved()
    {
        // The exception to the rule. A run that is not this mission's run was never the mission's to take -
        // a session deliberately seated on some other workflow keeps that seat when its mission changes.
        var (missionA, _) = await CreateMissionWithRun("Mission A");
        var (missionB, runB) = await CreateMissionWithRun("Mission B");
        var independent = await CreateStandaloneRun();

        await PushSession(missionA, independent);
        await JoinRun(independent, Sess);

        Assert.Equal(HttpStatusCode.OK, (await Attach(missionB)).StatusCode);

        var sent = Assert.Single(_attaches);
        Assert.Equal(missionB, sent.MissionId);   // the mission still moves
        Assert.False(sent.MoveSeat, "a run the caller chose independently was taken over by a mission move");
        Assert.Null(sent.WorkflowRunId);

        // And the ledger is left alone in BOTH directions: still active on the run it chose, and not
        // quietly enrolled in the destination mission's run it was never seated on.
        Assert.True(await IsActiveParticipant(independent, Sess),
            "the session was removed from a run that was never the mission's to take");
        Assert.False(await IsActiveParticipant(runB, Sess),
            "the session joined a run it was never seated on");
    }

    [Fact]
    public async Task Detaching_clears_the_missions_seat_with_it()
    {
        // The semantics question the review asked to be settled rather than implied: a session that has LEFT
        // a mission cannot still be governed by that mission's workflow run, so the seat goes with it. The
        // alternative - refusing to detach while a seat exists - would make detach impossible for exactly
        // the sessions that most need it, since every mission-spawned session is seated.
        var (missionA, runA) = await CreateMissionWithRun("Mission A");
        await PushSession(missionA, runA);
        await JoinRun(runA, Sess);

        Assert.Equal(HttpStatusCode.OK, (await Attach(null)).StatusCode);

        var sent = Assert.Single(_attaches);
        Assert.Null(sent.MissionId);
        Assert.True(sent.MoveSeat, "detach left the seat behind, so the session is governed by a mission it is not in");
        Assert.Null(sent.WorkflowRunId);   // cleared, not moved somewhere else
        Assert.False(await IsActiveParticipant(runA, Sess));
    }

    [Fact]
    public async Task An_unseated_session_simply_gains_the_destination_missions_seat()
    {
        // Nothing to preserve, so nothing is preserved. This is the case that would otherwise leave a
        // session attached to a mission and governed by nothing.
        var (missionB, runB) = await CreateMissionWithRun("Mission B");
        await PushSession(missionId: null, runId: null);

        Assert.Equal(HttpStatusCode.OK, (await Attach(missionB)).StatusCode);

        var sent = Assert.Single(_attaches);
        Assert.True(sent.MoveSeat);
        Assert.Equal(runB, sent.WorkflowRunId);
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private async Task<(Guid MissionId, Guid RunId)> CreateMissionWithRun(string name)
    {
        var resp = await _http.PostAsJsonAsync("missions", new NewMissionRequest { MissionName = name });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<MissionDto>();
        Assert.NotNull(dto);
        // A mission IS a run of the mission workflow. If this is ever null the premise of these tests is
        // gone, so fail here rather than let every case below quietly assert about nothing.
        Assert.True(dto!.WorkflowRunId.HasValue,
            "the Gateway created a mission with no workflow run, so there is no seat for a move to carry");
        return (dto.MissionId, dto.WorkflowRunId!.Value);
    }

    private async Task<Guid> CreateStandaloneRun()
    {
        var resp = await _http.PostAsJsonAsync("gateway/workflow-runs",
            new NewWorkflowRunRequest { WorkflowId = "standalone", Name = "a run of the caller's own" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var run = await resp.Content.ReadFromJsonAsync<WorkflowRunDto>();
        Assert.NotNull(run);
        return run!.Id;
    }

    private Task PushSession(Guid? missionId, Guid? runId) => _dir.PushSnapshotAsync(new SessionDto
    {
        SessionId = Sess,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "Working",
        Status = "Running",
        StatusColor = "blue",
        CreatedAt = DateTime.UtcNow,
        MissionId = missionId,
        WorkflowRunId = runId,
    });

    private Task<HttpResponseMessage> Attach(Guid? missionId) =>
        _http.PostAsJsonAsync($"sessions/{Sess}/mission", new SetMissionRequest { MissionId = missionId });

    private async Task JoinRun(Guid runId, string sessionId)
    {
        var resp = await _http.PatchAsJsonAsync($"gateway/workflow-runs/{runId}", new PatchWorkflowRunRequest
        {
            AddParticipants = new List<WorkflowRunParticipantDto> { new() { SessionId = sessionId } },
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private async Task<bool> IsActiveParticipant(Guid runId, string sessionId)
    {
        var run = await _http.GetFromJsonAsync<WorkflowRunDto>($"gateway/workflow-runs/{runId}", JsonOpts);
        Assert.NotNull(run);
        return run!.Participants.Any(p => p.SessionId == sessionId && p.LeftUtc is null);
    }
}
