using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// ISSUE #2629: BOTH SPAWN DOORS MUST HAND THE DIRECTOR A FINISHED ANSWER.
///
/// A session can be started two ways - POST /machines/{machine}/sessions ("some Director on that computer")
/// and POST /directors/{id}/sessions ("this exact Director", the door an unqualified
/// <c>cc-devthrottle session spawn</c> uses). The machine door resolved a mission-scoped spawn against the
/// Gateway's mission store and stamped the resolved NAME onto the create request. The Director door
/// forwarded the request verbatim. So a create reached the Director carrying an id and no name, the
/// Director read that as an old caller naming a mission in its own local store, and answered
/// <c>unknown mission '&lt;id&gt;'. Create it first with POST /missions.</c> for a mission that was real,
/// active, and listed by <c>cc-devthrottle mission list</c> - blocking every new mission-scoped session on
/// the machine. The workflow SEAT was missing from that door too, and silently: a session inside a mission
/// with none of the conduct the mission pins and no membership row for governance to read.
///
/// WHY THE EXISTING TESTS DID NOT CATCH IT, which is the part worth keeping. FleetSpawnMissionAttachTests
/// pinned the DIRECTOR's contract and stayed green throughout - it was right about what the Director does
/// with a name, and blind to whether the caller sent one. MachineSpawnWorkflowScopeTests pinned the machine
/// door. Nobody asserted the property that actually matters: that EVERY door does this, not just the one
/// somebody thought of. So these tests drive BOTH doors through the same body and compare their output.
///
/// Be precise about the limit of that, because the obvious stronger claim is false: these tests name the two
/// routes that exist, so they cannot fail for a THIRD door somebody adds later. What guards that case is the
/// shared resolver being the only implementation - there is no second copy to reach for - not this file.
/// If a third spawn door is added, add it here.
///
/// The Director is a capture here, not a real one: what is under test is what LEAVES the Gateway.
/// </summary>
public sealed class DirectorSpawnMissionAndSeatTests : IDisposable
{
    private const string DirectorId = "dir-spawn-2629";
    private const string Machine = "SPAWN-PC";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly GatewayDbTestHarness _db = new();
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-spawn-2629-" + Guid.NewGuid().ToString("N"));

    private WebApplication? _app;
    private HttpClient? _http;
    private DirectorRegistry? _registry;

    /// <summary>The create request each door dispatched, or null when nothing was dispatched at all.</summary>
    private NewSessionRequest? _directorDoorSaw;
    private NewSessionRequest? _machineDoorSaw;

    /// <summary>What the captured Director answers with - a real Director echoes the seat it stamped.</summary>
    private SessionDto _reply = new() { SessionId = Guid.NewGuid().ToString() };

    public void Dispose()
    {
        _http?.Dispose();
        if (_app is not null) _app.StopAsync().GetAwaiter().GetResult();
        _registry?.Dispose();
        _db.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>A resolver that always finds the same Director, so the machine door reaches its create.</summary>
    private sealed class AlwaysFound : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct)
            => Task.FromResult(new DirectorTargetResult(DirectorId, null));
    }

    private async Task<(MissionStore missions, WorkflowRunStore runs)> StartAsync()
    {
        Directory.CreateDirectory(_dir);
        var missions = new MissionStore(Path.Combine(_dir, "missions.json"), adoptUnattributedAs: TenantId.Local);
        var db = _db.Open();
        _ = new WorkflowStore(db);   // seeds the built-in workflows, so "mission" has a published version
        var runs = new WorkflowRunStore(db);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}");
        var app = builder.Build();

        _registry = new DirectorRegistry(Path.Combine(_dir, "instances"));
        _registry.RegisterFromStream(DirectorId, Machine, "test", "0.0.0-test", pid: 1,
            startedAt: DateTime.UtcNow, tenant: TenantId.Local);

        // The Director door: create rides the tunnel, so the capture IS the sendCommand hook.
        DirectorCommandRouter.SendDirectorCommandAsync send = (directorId, command, ct) =>
        {
            if (command.Verb == "create")
            {
                _directorDoorSaw = JsonSerializer.Deserialize<NewSessionRequest>(command.PayloadJson ?? "{}", Web);
                return Task.FromResult<DirectorCommandResult?>(
                    DirectorCommandResult.Success(JsonSerializer.Serialize(_reply, Web)));
            }
            return Task.FromResult<DirectorCommandResult?>(null);
        };

        // The machine door: create goes through the spawner, so the capture is its create hook.
        var spawner = new MachineSessionSpawner(new AlwaysFound(), (directorId, req, ct) =>
        {
            _machineDoorSaw = req;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, _reply, null));
        });

        // Self-host boundary: one tenant, resolved as Local, so both doors read the same mission store.
        var boundary = new HostedTenantBoundary(new SingleTenantContext(), new Pairing.DeviceRegistry(), hosted: false);

        GatewayEndpoints.Map(app, false, _registry, version: "test", token: "test-token",
            tenantBoundary: boundary, sendCommand: send, missions: missions, workflowRuns: runs, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown);
        MachineEndpoints.Map(app, false, new LauncherRegistry(), spawner, boundary: boundary,
            missions: missions, workflowRuns: runs, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown);

        await app.StartAsync();
        _app = app;
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{BoundPort.Of(app)}/") };
        return (missions, runs);
    }

    private Task<HttpResponseMessage> ThroughTheDirectorDoor(object body) =>
        _http!.PostAsJsonAsync($"directors/{DirectorId}/sessions", body);

    private Task<HttpResponseMessage> ThroughTheMachineDoor(object body) =>
        _http!.PostAsJsonAsync($"machines/{Machine}/sessions", body);

    /// <summary>
    /// THE REGRESSION. The Director door must stamp the mission's resolved NAME onto the create it
    /// dispatches. Without the name the Director cannot tell a Gateway-resolved mission from an
    /// unresolved one, and refuses the spawn.
    /// </summary>
    [Fact]
    public async Task The_director_door_stamps_the_resolved_mission_name_on_the_create()
    {
        var (missions, _) = await StartAsync();
        var mission = missions.Create(TenantId.Local, "OneAdvanced product enhancements");

        var response = await ThroughTheDirectorDoor(new
        {
            repoPath = @"C:\repo",
            agent = "ClaudeCode",
            missionId = mission.MissionId,   // the id ALONE, exactly as the command line sends it
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(_directorDoorSaw);
        Assert.Equal(mission.MissionId, _directorDoorSaw!.MissionId);
        // The whole defect in one assertion: this was null, and the Director refused the spawn.
        Assert.Equal("OneAdvanced product enhancements", _directorDoorSaw.MissionName);
    }

    /// <summary>
    /// The anti-drift assertion. Both doors are given the same body and must produce the same stamped
    /// create - the property that was false for four weeks while each door was individually "correct".
    /// </summary>
    [Fact]
    public async Task Both_doors_stamp_the_same_mission_name_for_the_same_request()
    {
        var (missions, _) = await StartAsync();
        var mission = missions.Create(TenantId.Local, "Same Mission Either Way");
        var body = new { repoPath = @"C:\repo", agent = "ClaudeCode", missionId = mission.MissionId };

        Assert.Equal(HttpStatusCode.Created, (await ThroughTheDirectorDoor(body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ThroughTheMachineDoor(body)).StatusCode);

        Assert.NotNull(_directorDoorSaw);
        Assert.NotNull(_machineDoorSaw);
        Assert.Equal(_machineDoorSaw!.MissionName, _directorDoorSaw!.MissionName);
        Assert.Equal("Same Mission Either Way", _directorDoorSaw.MissionName);
    }

    /// <summary>
    /// An unknown mission is refused BY THE GATEWAY, which is the only place that can know, and the create
    /// never leaves. Before this, the refusal came from the Director against a store that could not answer,
    /// and reached the caller as a 502 wrapping a message that told them to create a mission they already had.
    /// </summary>
    [Fact]
    public async Task An_unknown_mission_is_refused_at_the_gateway_and_no_create_is_dispatched()
    {
        await StartAsync();

        var response = await ThroughTheDirectorDoor(new
        {
            repoPath = @"C:\repo",
            agent = "ClaudeCode",
            missionId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);   // not a 502 from downstream
        Assert.Contains("unknown mission", await response.Content.ReadAsStringAsync());
        Assert.Null(_directorDoorSaw);                                   // nothing was dispatched
    }

    /// <summary>
    /// The quiet half of the same defect: a mission-scoped spawn through the Director door is SEATED on the
    /// mission's run, so the agent is pinned to the conduct its mission runs on. This door seated nothing.
    /// </summary>
    [Fact]
    public async Task The_director_door_seats_a_mission_spawn_on_the_missions_run()
    {
        var (missions, runs) = await StartAsync();
        var mission = missions.Create(TenantId.Local, "Seated Mission");
        var run = runs.Create("mission", "Seated Mission", missionId: mission.MissionId);
        // A real Director echoes the seat it stamped; the participant record depends on that proof.
        _reply = new SessionDto { SessionId = Guid.NewGuid().ToString(), WorkflowRunId = run.Id };

        var response = await ThroughTheDirectorDoor(new
        {
            repoPath = @"C:\repo",
            agent = "ClaudeCode",
            missionId = mission.MissionId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(_directorDoorSaw);
        Assert.Equal(run.Id, _directorDoorSaw!.WorkflowRunId);
        Assert.Equal("mission", _directorDoorSaw.WorkflowId);
        Assert.Equal(run.WorkflowVersion, _directorDoorSaw.WorkflowVersion);

        // And the membership row governance reads, which this door never wrote. Asserted FIELD BY FIELD,
        // not just "a row exists for this session": the machine on this door comes from the registered
        // Director rather than from a path segment, so a blank or a director id in that column would
        // otherwise pass unnoticed and governance would join effort to the wrong computer.
        var stored = runs.Get(run.Id);
        Assert.NotNull(stored);
        var participant = Assert.Single(stored!.Participants, p => p.SessionId == _reply.SessionId);
        Assert.Equal(Machine, participant.Machine);
        Assert.Equal("ClaudeCode", participant.AgentKind);
        Assert.Equal("", participant.Role);
    }

    /// <summary>
    /// An unknown workflow run is refused the same way through this door as through the other one - the
    /// resolver is shared, so the refusal is too.
    /// </summary>
    [Fact]
    public async Task An_unknown_workflow_run_is_refused_at_the_gateway_through_the_director_door()
    {
        await StartAsync();

        var response = await ThroughTheDirectorDoor(new
        {
            repoPath = @"C:\repo",
            agent = "ClaudeCode",
            workflowRunId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unknown workflow run", await response.Content.ReadAsStringAsync());
        Assert.Null(_directorDoorSaw);
    }

    /// <summary>
    /// The negative control: a spawn that names no mission and no run is untouched by any of this and still
    /// reaches the Director. Otherwise a "fix" that refused everything would look like a pass.
    /// </summary>
    [Fact]
    public async Task A_plain_spawn_is_unaffected_and_still_reaches_the_director()
    {
        await StartAsync();

        var response = await ThroughTheDirectorDoor(new { repoPath = @"C:\repo", agent = "ClaudeCode" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(_directorDoorSaw);
        Assert.Null(_directorDoorSaw!.MissionId);
        Assert.Null(_directorDoorSaw.MissionName);
        Assert.Null(_directorDoorSaw.WorkflowRunId);
    }
}
