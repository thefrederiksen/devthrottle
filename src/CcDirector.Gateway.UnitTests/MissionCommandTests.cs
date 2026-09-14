using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the Mission-attach command paths (fleet.html) through the shared
/// <see cref="SessionCommandExecutor"/>: the <c>attach-mission</c> verb stamps a session's MissionId +
/// cached MissionName (and detaches on a blank id), and a create-time <see cref="NewSessionRequest.MissionId"/>
/// attaches the new session at spawn. Mirrors the set-role tests in <see cref="SessionCommandExecutorTests"/>.
///
/// THE CONTRACT THESE PIN (issue #2629): the mission's ID AND NAME arrive together, because the Gateway -
/// the only store that holds missions - resolved the mission in the caller's own tenant before sending the
/// verb. The Director stamps what it was handed and owns no mission store of its own. An id with no name
/// was never resolved by anybody, and is REFUSED rather than looked up locally: the Director-local store
/// this code used to consult was a different, per-machine set that nothing writes any more, so consulting
/// it reported a real, active, listed mission as unknown and took mission-scoped spawning down.
/// </summary>
[Collection("DirectorRoot")]
public sealed class MissionCommandTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // OS shell used as a harmless RawCli agent so create tests exercise the REAL create path without an
    // installed coding-agent CLI (same approach as SessionCommandExecutorTests).
    private static string TestShellPath =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? "cmd.exe" : "/bin/sh";

    private static (SessionManager sm, Session session) NewSession()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var backend = new ExecuteActionTestBackend();
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
        return (sm, session);
    }

    // No mission store: the Director has none, and these tests would be lying if they handed it one.
    private static SessionCommandServices Services() => new();

    private static DirectorCommand AttachCommand(string sid, Guid? missionId, string? missionName = null) => new()
    {
        CommandId = "am1",
        Verb = "attach-mission",
        SessionId = sid,
        PayloadJson = JsonSerializer.Serialize(
            new SetMissionRequest { MissionId = missionId, MissionName = missionName }, Json),
    };

    /// <summary>An attach that also carries the Gateway's seat decision (issue #2387 review).</summary>
    private static DirectorCommand AttachWithSeat(
        string sid, Guid? missionId, string? missionName, bool moveSeat,
        Guid? runId = null, string? workflowId = null, int? version = null) => new()
    {
        CommandId = "am2",
        Verb = "attach-mission",
        SessionId = sid,
        PayloadJson = JsonSerializer.Serialize(new SetMissionRequest
        {
            MissionId = missionId,
            MissionName = missionName,
            MoveSeat = moveSeat,
            WorkflowRunId = runId,
            WorkflowId = workflowId,
            WorkflowVersion = version,
        }, Json),
    };

    private static DirectorCommand CreateCommand(NewSessionRequest req) => new()
    {
        CommandId = "c1",
        Verb = "create",
        SessionId = "",
        PayloadJson = JsonSerializer.Serialize(req, Json),
    };

    [Fact]
    public async Task AttachMission_StampsMissionIdAndCachesName_AndReturnsUpdatedSession()
    {
        var (sm, session) = NewSession();
        try
        {
            var missionId = Guid.NewGuid();

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), missionId, "Session Lifecycle"), Services());

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(missionId, session.MissionId);
            Assert.Equal("Session Lifecycle", session.MissionName); // cached from what the Gateway sent

            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(missionId, dto.MissionId);
            Assert.Equal("Session Lifecycle", dto.MissionName);
        }
        finally { sm.Dispose(); }
    }

    // Issue #2629: an id with NO name was never resolved by the Gateway, so there is nobody who can say
    // what it is. Refused, loudly, and with a message that names the real problem - the old behaviour was
    // to look it up in a stale per-machine store and report a real mission as "unknown".
    [Fact]
    public async Task AttachMission_IdWithNoName_IsRefused_AndSaysWhy()
    {
        var (sm, session) = NewSession();
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), Guid.NewGuid()), Services());

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Contains("without its name", result.Error);
            Assert.Contains("Gateway", result.Error);
            Assert.DoesNotContain("Create it first", result.Error); // the old lie: it already exists
            Assert.Null(session.MissionId);
            Assert.Null(session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_BlankMissionId_DetachesSession()
    {
        var (sm, session) = NewSession();
        try
        {
            var missionId = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), missionId, "Session Lifecycle"), Services());
            Assert.Equal(missionId, session.MissionId);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", AttachCommand(session.Id.ToString(), null), Services());

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.MissionId); // cleared -> detached
            Assert.Null(session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_MissingSession_ReturnsNotFound()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(Guid.NewGuid().ToString(), Guid.NewGuid(), "Session Lifecycle"), Services());

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_InvalidSessionId_ReturnsBadRequest()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand("not-a-guid", Guid.NewGuid(), "Session Lifecycle"), Services());

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
        finally { sm.Dispose(); }
    }

    // Issue #2387: the GATEWAY path on the attach verb, matching what create already does. A mission is a
    // FLEET record whose source of truth is the Gateway; when the Gateway has resolved it inside the caller's
    // own tenant and sends the NAME with the id, the Director stamps it directly. Proof of "no local lookup":
    // the Director is given NO mission store at all here, and the attach still succeeds with the carried
    // name. A local lookup would reject a mission that is real and owned, which is precisely the failure
    // #1548 fixed on one spawn door and #2629 hit again through the other.
    [Fact]
    public async Task AttachMission_WithMissionNamePresent_StampsDirectly_WithoutStoreLookup()
    {
        var (sm, session) = NewSession();
        try
        {
            var carriedId = Guid.NewGuid();

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), carriedId, "Gateway Native Mission"), Services());

            Assert.Equal(DirectorCommandStatus.Ok, result.Status); // NOT rejected: nothing local was consulted
            Assert.Equal(carriedId, session.MissionId);
            Assert.Equal("Gateway Native Mission", session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    // Issue #2387: ATTACHING IS A MOVE. A session that already carries a mission is re-pointed by the same
    // verb, and the old attachment is gone rather than accumulated. This is the settled rule, not an accident
    // of implementation: a mission's shape is discovered as it runs, so the first classification of a session
    // is always a guess, and a one-way attach would make every wrong guess permanent until the session died.
    [Fact]
    public async Task AttachMission_OnAnAlreadyAttachedSession_MovesItToTheNewMission()
    {
        var (sm, session) = NewSession();
        try
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), first, "First Mission"), Services());
            Assert.Equal(first, session.MissionId);   // the control: it really was on the first

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), second, "Second Mission"), Services());

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(second, session.MissionId);
            Assert.Equal("Second Mission", session.MissionName);  // the cached name moved with the id
        }
        finally { sm.Dispose(); }
    }

    // Issue #2387: a REFUSED attach leaves the session on the mission it already had. Without this, a
    // refusal would silently detach a correctly-attached session - a failure that looks like nothing
    // happened until somebody goes looking for the pod.
    [Fact]
    public async Task AttachMission_ARefusedAttach_LeavesAnExistingAttachmentIntact()
    {
        var (sm, session) = NewSession();
        try
        {
            var mission = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), mission, "Real Mission"), Services());

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), Guid.NewGuid()), Services()); // no name -> refused

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Equal(mission, session.MissionId);
            Assert.Equal("Real Mission", session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    // ===== The workflow SEAT moves with the mission (issue #2387, review finding) =====
    //
    // A Mission is also a RUN of the built-in "mission" workflow, and a mission-scoped spawn seats the
    // session on that run - which is what pins the conduct its preamble told it to follow. Moving only the
    // mission link left a session DISPLAYED under one mission and GOVERNED by the one it left, taking its
    // conduct from a mission it was no longer in. These cases pin the transition itself, so a later edit
    // cannot quietly go back to moving one without the other.

    [Fact]
    public async Task AttachMission_WhenTheGatewaySaysMoveTheSeat_MovesMissionAndSeatTogether()
    {
        var (sm, session) = NewSession();
        try
        {
            var services = Services();
            var missionA = Guid.NewGuid();
            var runA = Guid.NewGuid();
            var missionB = Guid.NewGuid();
            var runB = Guid.NewGuid();

            // Seated under A, exactly as a mission-scoped spawn leaves a session.
            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), missionA, "Mission A", moveSeat: true, runA, "mission", 8),
                services);
            Assert.Equal(missionA, session.MissionId);
            Assert.Equal(runA, session.WorkflowRunId);      // the control: it really was seated on A's run

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), missionB, "Mission B", moveSeat: true, runB, "mission", 9),
                services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(missionB, session.MissionId);
            // THE FINDING, asserted directly: the seat is B's, not A's. Before the fix this stayed runA
            // while the mission read B - shown under one mission, governed by the other.
            Assert.Equal(runB, session.WorkflowRunId);
            Assert.Equal(9, session.WorkflowVersion);       // and the PINNED version moved with it

            // The DTO the clients render carries the same pair, so no surface can disagree with the session.
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(missionB, dto.MissionId);
            Assert.Equal(runB, dto.WorkflowRunId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_WhenTheGatewaySaysLeaveTheSeat_MovesTheMissionOnly()
    {
        var (sm, session) = NewSession();
        try
        {
            var services = Services();
            var chosenRun = Guid.NewGuid();
            var missionA = Guid.NewGuid();
            var missionB = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), missionA, "Mission A", moveSeat: true, chosenRun, "release", 3),
                services);

            // The exception to the rule: a run the caller chose independently was never the mission's to
            // take, so a move must leave it exactly where it is - id, workflow and pinned version.
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), missionB, "Mission B", moveSeat: false),
                services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(missionB, session.MissionId);      // the mission moved
            Assert.Equal(chosenRun, session.WorkflowRunId); // the seat did not
            Assert.Equal("release", session.WorkflowId);
            Assert.Equal(3, session.WorkflowVersion);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_Detach_ClearsTheMissionsSeatWithIt()
    {
        var (sm, session) = NewSession();
        try
        {
            var services = Services();
            var mission = Guid.NewGuid();
            var run = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), mission, "Mission A", moveSeat: true, run, "mission", 8),
                services);
            Assert.Equal(run, session.WorkflowRunId);

            // Detach: a session that has LEFT a mission cannot still be governed by that mission's run.
            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), null, null, moveSeat: true),
                services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.MissionId);
            Assert.Null(session.WorkflowRunId);
            Assert.Null(session.WorkflowId);
            Assert.Null(session.WorkflowVersion);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_Detach_LeavesAnIndependentlyChosenSeatAlone()
    {
        var (sm, session) = NewSession();
        try
        {
            var services = Services();
            var chosenRun = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), Guid.NewGuid(), "Mission A", moveSeat: true, chosenRun, "release", 3),
                services);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), null, null, moveSeat: false),
                services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Null(session.MissionId);                 // detached from the mission
            Assert.Equal(chosenRun, session.WorkflowRunId); // but still on the run it was put on
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public async Task AttachMission_WithoutASeatDecision_NeverTouchesTheSeatAtAll()
    {
        // The compatibility case, and the one that keeps the two decisions separable: a payload that says
        // nothing about the seat must not clear it. Otherwise every caller that has not been taught about
        // seats would silently unseat the sessions it attaches.
        var (sm, session) = NewSession();
        try
        {
            var services = Services();
            var mission = Guid.NewGuid();
            var run = Guid.NewGuid();

            await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachWithSeat(session.Id.ToString(), Guid.NewGuid(), "Mission A", moveSeat: true, run, "mission", 8),
                services);

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A",
                AttachCommand(session.Id.ToString(), mission, "Mission B"), services);

            Assert.Equal(DirectorCommandStatus.Ok, result.Status);
            Assert.Equal(mission, session.MissionId);
            Assert.Equal(run, session.WorkflowRunId);   // untouched, because nothing asked for it to move
        }
        finally { sm.Dispose(); }
    }

    // The create path's GATEWAY contract: the request carries the mission id AND the name the Gateway
    // resolved, and the Director stamps the attachment directly. Proof that nothing local is consulted:
    // the Director is given no mission store at all, and the create still succeeds with the carried name.
    // A local lookup would have rejected a mission that is real, active and owned - the #2629 failure.
    [Fact]
    public async Task Create_WithMissionNamePresent_StampsDirectly_WithoutStoreLookup()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var carriedId = Guid.NewGuid();

            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "gateway-mission-create",
                MissionId = carriedId,
                MissionName = "Gateway Native Mission", // resolved+validated by the Gateway already
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command, Services());

            Assert.Equal(DirectorCommandStatus.Ok, result.Status); // NOT rejected: nothing local was consulted
            var dto = JsonSerializer.Deserialize<SessionDto>(result.BodyJson ?? "", Json);
            Assert.NotNull(dto);
            Assert.Equal(carriedId, dto.MissionId);
            Assert.Equal("Gateway Native Mission", dto.MissionName); // stamped from the request

            Assert.True(Guid.TryParse(dto.SessionId, out var sid));
            var session = sm.GetSession(sid);
            Assert.NotNull(session);
            Assert.Equal(carriedId, session.MissionId);
            Assert.Equal("Gateway Native Mission", session.MissionName);
        }
        finally { sm.Dispose(); }
    }

    // Issue #2629, at the create verb: a mission id with no name reached this Director because some caller
    // skipped the Gateway's resolution. It is REFUSED, before the session is created, and the message says
    // where missions actually live instead of telling the caller to create one that already exists.
    [Fact]
    public async Task Create_WithMissionIdButNoName_IsRefused_AndNoSessionIsCreated()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var before = sm.ListSessions().Count;

            var command = CreateCommand(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "RawCli",
                Command = TestShellPath,
                Name = "unresolved-mission",
                MissionId = Guid.NewGuid(),
                MissionName = null, // never resolved by the Gateway
            });

            var result = await SessionCommandExecutor.DispatchAsync(sm, "dir-A", command, Services());

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Contains("without its name", result.Error);
            Assert.Contains("Gateway", result.Error);
            Assert.DoesNotContain("Create it first", result.Error); // the old lie: it already exists
            Assert.Equal(before, sm.ListSessions().Count); // rejected before creation - no orphan
        }
        finally { sm.Dispose(); }
    }
}
