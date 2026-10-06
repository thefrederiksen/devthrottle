using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Teams;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// devthrottle_internal#2311, #3552 review: the Fleet Manager lessons. A REAL hosted <see cref="GatewayHost"/> with Teams
/// released and two members' Directors in one team over the REAL tunnel. Alice's session is the marked Fleet Manager and
/// Bob's Director lists its id - the roster accepts any id from any Director in a team. The lessons go to Alice's Director,
/// and never to Bob's: not on Bob's push of the id, not when a lesson changes, not when Bob's Director reconnects.
///
/// Two shapes, because the two host lines that pick a Director are watched by different moments:
/// the first, both Directors list the id and Alice's Director wrote its stored conversation, watches the push
/// (<c>isSessionOfDirector:</c> on the observer); the second, only Bob's Director lists the id and Alice's Director holds
/// its key row, watches <c>FleetManagerDirectorOf</c>, the Director a lessons change and a reconnect stamp - with the old
/// "whoever lists it" answer that is Bob's, every time, rather than by the order of a dictionary.
///
/// And the personal tenant, where nothing changes: the one Director that lists the marked session is sent the lessons.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamFleetManagerLessonsTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string AliceDirector = "director-alice";
    private const string BobDirector = "director-bob";
    private readonly string _teamOwner = "sub-fml-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _alice = "sub-fml-alice-" + Guid.NewGuid().ToString("N");
    private readonly string _bob = "sub-fml-bob-" + Guid.NewGuid().ToString("N");
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-fml-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<DirectorCommand> _seenByAlice = new();
    private readonly ConcurrentQueue<DirectorCommand> _seenByBob = new();
    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _aliceDirector = null!;
    private FakeTunnelDirector _bobDirector = null!;
    private string _bobKey = null!;
    private TenantId _team;
    private string? _priorHosted;
    private string? _priorRoot;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instancesDir);

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true, teamsReleased: true);
        await _gateway.StartAsync();
        HostedTeamBill.CreateTable(_gateway);

        var teamId = _gateway.TeamRegistry.CreateTeam(_teamOwner, "A").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(teamId, _alice, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(teamId, _bob, TeamRole.Developer).IsDone);
        HostedTeamBill.Start(_gateway, teamId, seats: 5);
        _team = new TenantId(teamId);

        _aliceDirector = await FakeTunnelDirector.StartAsync(_gateway, TeamKey(teamId, _alice, AliceDirector), AliceDirector,
            dispatch: cmd => { _seenByAlice.Enqueue(cmd); return Ok(); });
        _bobKey = TeamKey(teamId, _bob, BobDirector);
        _bobDirector = await FakeTunnelDirector.StartAsync(_gateway, _bobKey, BobDirector,
            dispatch: cmd => { _seenByBob.Enqueue(cmd); return Ok(); });

        // The team's mark and one confirmed lesson. No route can write either in a team today (the gate refuses every
        // Fleet Manager route), so they are seeded straight into the stores the observer reads.
        _gateway.TenantSettingsResolver.SetFleetManagerSessionId(_team, _sessionId, DateTime.UtcNow);
        _gateway.FleetPreferencesForTests.AddLesson(_team, "lesson one", "mistake one", _alice, confirmed: true, DateTime.UtcNow);
    }

    public async Task DisposeAsync()
    {
        await _aliceDirector.DisposeAsync();
        await _bobDirector.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task Observe_BothDirectorsListAlicesMarkedSession_OnlyAlicesDirectorIsSentTheLessons_OnAPushALessonsChangeAndAReconnect()
    {
        // The record that makes the session Alice's: its stored conversation, written by her Director.
        _gateway.SeedStoredConversationForTest(_team, AliceDirector, _sessionId, ("User", "ask"), ("Assistant", "answered"));

        // PRESENCE: Alice's Director lists the session and is sent the lessons.
        await _aliceDirector.PushSnapshotAsync(Row(_sessionId));
        Assert.NotNull(await WaitForLessons(_seenByAlice, "lesson one"));

        // Bob's Director lists the same id, then pushes it as a change: the push path. Bob's is never sent the lessons.
        await _bobDirector.PushSnapshotAsync(Row(_sessionId));
        await _bobDirector.PushDeltaAsync(Row(_sessionId));
        Assert.Null(await WaitForLessons(_seenByBob, null, seconds: 3));

        // A lesson is kept: Alice's Director is sent the new block, Bob's nothing.
        _gateway.FleetPreferencesForTests.AddLesson(_team, "lesson two", "mistake two", _alice, confirmed: true, DateTime.UtcNow);
        await LessonsChangedOnARoute();
        Assert.NotNull(await WaitForLessons(_seenByAlice, "lesson two"));
        Assert.Null(await WaitForLessons(_seenByBob, null, seconds: 3));

        // Bob's Director reconnects and lists the id again: still nothing for Bob.
        await ReconnectBob();
        await _bobDirector.PushDeltaAsync(Row(_sessionId));
        Assert.Null(await WaitForLessons(_seenByBob, null, seconds: 3));
    }

    [Fact]
    public async Task DirectorOf_OnlyBobsDirectorListsAlicesKeyedMarkedSession_TheLessonsGoToAlicesDirector_NeverBobs()
    {
        // The record that makes the session Alice's: its key row, registered by her Director, which does not list it.
        await _aliceDirector.RegisterSessionKeyAsync(_sessionId, GatewaySessionKey.Mint(), DateTime.UtcNow.AddHours(1));
        await _aliceDirector.PushSnapshotAsync();

        // Bob's Director is the only one listing the id: its new connection stamps the marked session's own Director.
        await _bobDirector.PushSnapshotAsync(Row(_sessionId));
        Assert.NotNull(await WaitForLessons(_seenByAlice, "lesson one"));
        Assert.Null(await WaitForLessons(_seenByBob, null, seconds: 3));

        // A lesson is kept: Alice's Director is sent the new block, Bob's nothing.
        _gateway.FleetPreferencesForTests.AddLesson(_team, "lesson two", "mistake two", _alice, confirmed: true, DateTime.UtcNow);
        await LessonsChangedOnARoute();
        Assert.NotNull(await WaitForLessons(_seenByAlice, "lesson two"));
        Assert.Null(await WaitForLessons(_seenByBob, null, seconds: 3));

        // Bob's Director reconnects, still the only one listing the id: nothing for Bob.
        await ReconnectBob();
        Assert.Null(await WaitForLessons(_seenByBob, null, seconds: 3));
    }

    [Fact]
    public async Task Observe_APersonalTenant_TheOneDirectorListingTheMarkedSession_IsSentTheLessons()
    {
        var runId = Guid.NewGuid().ToString("N");
        var device = HostedTestEnrollment.Enroll(_gateway, "sub-fml-personal-" + runId, $"fml-{runId}@example.com",
            "dev-fml-" + runId, "MFML");
        Assert.False(_gateway.TeamMemberEntitlement.IsTeam(device.Tenant), "The personal tenant must not be a team.");
        var sessionId = Guid.NewGuid().ToString();
        _gateway.TenantSettingsResolver.SetFleetManagerSessionId(device.Tenant, sessionId, DateTime.UtcNow);
        _gateway.FleetPreferencesForTests.AddLesson(device.Tenant, "personal lesson", "personal mistake", "someone",
            confirmed: true, DateTime.UtcNow);
        var seen = new ConcurrentQueue<DirectorCommand>();
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, device.DeviceKey, "director-personal-" + runId,
            "MFML", dispatch: cmd => { seen.Enqueue(cmd); return Ok(); });

        await director.PushSnapshotAsync(Row(sessionId));

        Assert.NotNull(await WaitForLessons(seen, "personal lesson", sessionId: sessionId));
    }

    /// <summary>A lesson route calls <c>LessonsChanged</c> inside the caller's tenant scope, which the team rule's
    /// stored-record read needs; the test calls it the same way.</summary>
    private async Task LessonsChangedOnARoute()
    {
        using var scope = _gateway.TenantBoundaryForTests.EnterScope(_team);
        await _gateway.FleetManagerLessonsStamp.LessonsChanged(_team);
    }

    private async Task ReconnectBob()
    {
        await _bobDirector.DisposeAsync();
        _bobDirector = await FakeTunnelDirector.StartAsync(_gateway, _bobKey, BobDirector,
            dispatch: cmd => { _seenByBob.Enqueue(cmd); return Ok(); });
        await _bobDirector.PushSnapshotAsync(Row(_sessionId));
    }

    private string TeamKey(string team, string subject, string directorId) =>
        _gateway.Devices.RegisterForTenant(new TenantId(team), subject,
            Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(team, subject, directorId), "M-" + directorId).DeviceKey;

    /// <summary>Poll for a lessons command on the session whose block contains <paramref name="containing"/> (any
    /// lessons command when null), rather than sleeping a fixed time: the sends are asynchronous.</summary>
    private async Task<DirectorCommand?> WaitForLessons(ConcurrentQueue<DirectorCommand> seen, string? containing,
        int seconds = 10, string? sessionId = null)
    {
        var sid = sessionId ?? _sessionId;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var hit = seen.FirstOrDefault(c => c.Verb == FleetManagerLessonsObserver.Verb && c.SessionId == sid
                && (containing is null || (Lessons(c) ?? "").Contains(containing, StringComparison.Ordinal)));
            if (hit is not null) return hit;
            await Task.Delay(50);
        }
        return null;
    }

    private static string? Lessons(DirectorCommand command) =>
        JsonSerializer.Deserialize<SetFleetManagerLessonsRequest>(command.PayloadJson!,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Lessons;

    private static DirectorCommandResult Ok() => FakeTunnelDirector.Ok(new { ok = true });

    private static SessionDto Row(string sid) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };
}
