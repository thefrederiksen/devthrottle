using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Teams;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// #3552 review S2-F15: the host's wiring of the two round 4 question fixes, END TO END. A REAL hosted
/// <see cref="GatewayHost"/> with Teams released, and two members' Directors in one team over the REAL tunnel, the
/// colleague's Director listing the owner's session id. The unit tests in <c>TeamDirectorTunnelTests</c> build the
/// watcher and the display observer themselves; this one uses the host's own, so it watches the two lines that hand
/// them the rule (<c>mayReceive:</c> on the display push, <c>acceptsReport:</c> on the turn-end watcher) and the team
/// branch of <see cref="GatewayHost.IsTeamSessionOfDirector"/> they both ask.
///
/// It asserts a PRESENCE on the owner's side - the owner's Director IS sent the folded display state, and the owner's
/// stop DOES end a turn carrying the owner's Director id (the Wingman reads that Director's screen) - and the absence
/// on the colleague's side. Remove either wire and the colleague's Director is reached; make the team branch answer no
/// for the session's own Director and the owner is starved. Each of the three goes red here.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamColleagueSessionWiringTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string OwnerDirector = "director-owner";
    private const string ColleagueDirector = "director-colleague";
    private readonly string _teamOwner = "sub-cw-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _bob = "sub-cw-bob-" + Guid.NewGuid().ToString("N");
    private readonly string _alice = "sub-cw-alice-" + Guid.NewGuid().ToString("N");
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-cw-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<DirectorCommand> _seenByOwner = new();
    private readonly ConcurrentQueue<DirectorCommand> _seenByColleague = new();
    private GatewayHost _gateway = null!;
    private FakeTunnelDirector _owner = null!;
    private FakeTunnelDirector _colleague = null!;
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

        // Bob owns the session; Alice is his colleague in the same team, and her Director lists his session's id.
        var teamId = _gateway.TeamRegistry.CreateTeam(_teamOwner, "A").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(teamId, _bob, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(teamId, _alice, TeamRole.Developer).IsDone);
        HostedTeamBill.Start(_gateway, teamId, seats: 5);
        _team = new TenantId(teamId);

        _owner = await FakeTunnelDirector.StartAsync(_gateway, TeamKey(teamId, _bob, OwnerDirector), OwnerDirector,
            dispatch: cmd => { _seenByOwner.Enqueue(cmd); return Ok(); });
        _colleague = await FakeTunnelDirector.StartAsync(_gateway, TeamKey(teamId, _alice, ColleagueDirector), ColleagueDirector,
            dispatch: cmd => { _seenByColleague.Enqueue(cmd); return Ok(); });

        // The record that makes the session Bob's: its stored conversation, written by his Director. Both rosters list
        // the id, so without the record it would be nobody's for now.
        _gateway.SeedStoredConversationForTest(_team, OwnerDirector, _sessionId, ("User", "ask"), ("Assistant", "answered"));
        await _owner.PushSnapshotAsync(Row(_sessionId, "Working"));
        await _colleague.PushSnapshotAsync(Row(_sessionId, "WaitingForInput"));
        _gateway.VoiceService!.Mark(_team, _sessionId);
    }

    public async Task DisposeAsync()
    {
        await _owner.DisposeAsync();
        await _colleague.DisposeAsync();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task TheFoldedState_IsSentToTheSessionsOwnDirector_AndNotToAColleagueWhoListsItsId()
    {
        _gateway.SweepDisplayState();

        // PRESENCE: the owner's Director is sent its session's folded state.
        Assert.NotNull(await WaitFor(_seenByOwner, "set-display-state"));
        // ABSENCE, only meaningful beside the presence above: the colleague's Director is not, given the same time again.
        Assert.Null(await WaitFor(_seenByColleague, "set-display-state", seconds: 3));
    }

    [Fact]
    public async Task AStop_OfTheSessionsOwnDirector_EndsTheTurn_AndAColleaguesReportOfTheSameId_DoesNot()
    {
        var watcher = _gateway.TurnEndWatcherForTest!;

        watcher.Observe(_team, _sessionId, "Working", OwnerDirector);
        // The colleague's Director reports the owner's session as waiting: not taken, so no turn ends and her screen is
        // never read for his session.
        watcher.Observe(_team, _sessionId, "WaitingForInput", ColleagueDirector);
        // The owner's own stop ends the turn, carrying his Director's id: the Wingman reads HIS screen.
        watcher.Observe(_team, _sessionId, "WaitingForInput", OwnerDirector);

        Assert.NotNull(await WaitFor(_seenByOwner, "screen-grid"));
        Assert.Null(await WaitFor(_seenByColleague, "screen-grid", seconds: 3));
    }

    /// <summary>
    /// After #3551 the session history row of a team session carries its person, from the Director that pushed it. A
    /// colleague's Director that lists another person's session id must not write that row: not take it over under its
    /// own Director, not end it by dropping the id, and not stamp it with its own person by listing the id first - which
    /// would also keep the real person's first prompt off it, since the first-prompt line goes only to a row of the
    /// prompt's own person. The recorder takes a session's row only from the Director the one team rule says it is.
    /// </summary>
    [Fact]
    public async Task ASessionsHistoryRow_IsWrittenOnlyByItsOwnDirector_NeverByAColleagueWhoListsItsId()
    {
        // Set up above: Bob's Director listed the session, then Alice's listed the same id. PRESENCE: the row is Bob's.
        var row = HistoryRow(_sessionId);
        Assert.NotNull(row);
        Assert.Equal(OwnerDirector, row!.DirectorId);
        Assert.Equal(_bob, row.PersonSubject);

        // Alice's Director removes the id, then drops it from its roster: neither ends Bob's row.
        await _colleague.RemoveSessionAsync(_sessionId);
        row = HistoryRow(_sessionId);
        Assert.Null(row!.EndedAtUtc);
        await _colleague.PushSnapshotAsync();
        row = HistoryRow(_sessionId);
        Assert.Equal(OwnerDirector, row!.DirectorId);
        Assert.Null(row.EndedAtUtc);

        // A second session of Bob's, whose id Alice's Director lists FIRST: the row is still stamped with Bob.
        var second = Guid.NewGuid().ToString();
        _gateway.SeedStoredConversationForTest(_team, OwnerDirector, second, ("User", "ask"), ("Assistant", "answered"));
        await _colleague.PushSnapshotAsync(Row(second, "Working"));
        Assert.Null(HistoryRow(second));
        await _owner.PushSnapshotAsync(Row(_sessionId, "Working"), Row(second, "Working"));
        row = HistoryRow(second);
        Assert.NotNull(row);
        Assert.Equal(OwnerDirector, row!.DirectorId);
        Assert.Equal(_bob, row.PersonSubject);
    }

    /// <summary>The session's history row as its own tenant reads it, or null.</summary>
    private CcDirector.Gateway.Data.Entities.SessionHistoryEntity? HistoryRow(string sessionId)
    {
        using var ctx = _gateway.GatewayDatabaseForTests.CreateContext(_team);
        return ctx.SessionHistory.AsNoTracking().SingleOrDefault(e => e.SessionId == sessionId);
    }

    private string TeamKey(string team, string subject, string directorId) =>
        _gateway.Devices.RegisterForTenant(new TenantId(team), subject,
            Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(team, subject, directorId), "M-" + directorId).DeviceKey;

    /// <summary>Poll for a verb on this session rather than sleeping a fixed time: the sends are asynchronous.</summary>
    private async Task<DirectorCommand?> WaitFor(ConcurrentQueue<DirectorCommand> seen, string verb, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var hit = seen.FirstOrDefault(c => c.Verb == verb && c.SessionId == _sessionId);
            if (hit is not null) return hit;
            await Task.Delay(50);
        }
        return null;
    }

    private static DirectorCommandResult Ok() => FakeTunnelDirector.Ok(new { ok = true });

    private static SessionDto Row(string sid, string state) => new()
    {
        SessionId = sid,
        Agent = "claude",
        RepoPath = "/repo",
        ActivityState = state,
        Status = "Running",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };
}
