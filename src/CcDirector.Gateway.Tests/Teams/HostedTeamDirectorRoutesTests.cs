using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Teams;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A TEAM DIRECTOR'S OWN ROUTES, OVER THE WIRE (devthrottle_internal#2311, live proof F1 and F2). A REAL hosted
/// <see cref="GatewayHost"/> with Teams released and a team of five: the Owner, a Manager, Alice and Bob (Developers) and
/// Carol (a Collaborator). The Owner, the Manager, Alice and Bob each have a Director connected over the REAL tunnel on a
/// team key, each with one session whose key row their own Director registered. Every request goes through the real
/// pipeline - authentication, the team gate, the endpoint - with a team device key, or with a session key the way a
/// session's cc-devthrottle sends one.
///
/// For every route the live proof found refused: the caller's own thing is served, another member's is refused (or left
/// out of a list), and a Collaborator is refused. Where the role table differs for the Owner and Manager it is asked of
/// them too. Bob's Director also lists ALICE's session id, which the roster accepts from any Director, so every cut is
/// shown against a colleague who is trying.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedTeamDirectorRoutesTests : IAsyncLifetime
{
    private const string Token = "test-token-team-routes";
    private const string OwnerDirector = "director-owner";
    private const string ManagerDirector = "director-manager";
    private const string AliceDirector = "director-alice";
    private const string BobDirector = "director-bob";
    private const string CarolDirector = "director-carol";

    private readonly string _owner = "sub-routes-owner-" + Guid.NewGuid().ToString("N");
    private readonly string _manager = "sub-routes-manager-" + Guid.NewGuid().ToString("N");
    private readonly string _alice = "sub-routes-alice-" + Guid.NewGuid().ToString("N");
    private readonly string _bob = "sub-routes-bob-" + Guid.NewGuid().ToString("N");
    private readonly string _carol = "sub-routes-carol-" + Guid.NewGuid().ToString("N");
    private readonly string _ownerSession = Guid.NewGuid().ToString();
    private readonly string _managerSession = Guid.NewGuid().ToString();
    private readonly string _aliceSession = Guid.NewGuid().ToString();
    private readonly string _bobSession = Guid.NewGuid().ToString();
    private readonly string _aliceSessionKey = GatewaySessionKey.Mint();
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-team-routes-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;
    private readonly List<FakeTunnelDirector> _directors = new();
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private TenantId _team;
    private string _ownerKey = "";
    private string _managerKey = "";
    private string _aliceKey = "";
    private string _bobKey = "";
    private string _carolKey = "";
    private string? _priorHosted;
    private string? _priorRoot;

    public HostedTeamDirectorRoutesTests(ITestOutputHelper output) => _out = output;

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
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/"), Timeout = TimeSpan.FromMinutes(2) };

        // Each person has a personal account with an email, so a team answer that leaked another person's identity would
        // have one to leak.
        foreach (var (subject, email) in new[] { (_owner, "owner@example.com"), (_manager, "manager@example.com"),
                     (_alice, "alice@example.com"), (_bob, "bob@example.com"), (_carol, "carol@example.com") })
            _gateway.TenantRegistry.MintOrLookupBySubject(subject, email);

        var teamId = _gateway.TeamRegistry.CreateTeam(_owner, "Routes").Team!.TeamId;
        Assert.True(_gateway.TeamRegistry.AddMember(teamId, _manager, TeamRole.Manager).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(teamId, _alice, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(teamId, _bob, TeamRole.Developer).IsDone);
        Assert.True(_gateway.TeamRegistry.AddMember(teamId, _carol, TeamRole.Collaborator).IsDone);
        HostedTeamBill.Start(_gateway, teamId, seats: 6);
        _team = new TenantId(teamId);

        _ownerKey = await DirectorWithSession(teamId, _owner, OwnerDirector, _ownerSession);
        _managerKey = await DirectorWithSession(teamId, _manager, ManagerDirector, _managerSession);
        _aliceKey = await DirectorWithSession(teamId, _alice, AliceDirector, _aliceSession, _aliceSessionKey);
        _bobKey = await DirectorWithSession(teamId, _bob, BobDirector, _bobSession, extraSessionIds: _aliceSession);
        // A Collaborator runs no sessions and has no Director; a key is minted for one anyway, so that the refusal below
        // is the role table's and not a missing credential's.
        _carolKey = TeamKey(teamId, _carol, CarolDirector);

        // Bob's Director lists Alice's session id too - asserted, so every cut below is shown against a colleague trying.
        Assert.Equal(2, _gateway.PushedSessions.DirectorsHoldingSession(_team, _aliceSession).Count);
    }

    public async Task DisposeAsync()
    {
        foreach (var director in _directors)
            await director.DisposeAsync();
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { /* best-effort */ }
    }

    // ---- F1: GET /sessions, the fleet check's own read -------------------------------------------------------------

    [Fact]
    public async Task F1_TheFleetCheck_OnADevelopersSessionKey_SucceedsWithOnlyTheirOwnSessions()
    {
        // The exact read cc-devthrottle's `session list` makes (tools/cc_shared/gateway.py), on the session key the
        // Director's fleet check hands it (FleetToolReachability).
        var (status, body) = await Send(HttpMethod.Get, "sessions?envelope=true", _aliceSessionKey);

        Assert.Equal(HttpStatusCode.OK, status);
        var sessions = SessionIds(body.GetProperty("sessions"));
        Assert.Equal(new[] { _aliceSession }, sessions);
        var directors = body.GetProperty("directors").EnumerateArray().Select(d => d.GetProperty("directorId").GetString()).ToArray();
        Assert.Equal(new[] { AliceDirector }, directors);
        Assert.DoesNotContain(_bobSession, body.GetRawText());
        Assert.DoesNotContain(BobDirector, body.GetRawText());
    }

    [Fact]
    public async Task GetSessions_ADevelopersDeviceKey_ServesTheirOwn_AndAColleagueListingTheirIdGetsNothingOfIt()
    {
        var (aliceStatus, aliceBody) = await Send(HttpMethod.Get, "sessions", _aliceKey);
        Assert.Equal(HttpStatusCode.OK, aliceStatus);
        Assert.Equal(new[] { _aliceSession }, SessionIds(aliceBody));

        // Bob's own Director lists Alice's session id. His roster still carries only his own session: the one holder rule
        // says Alice's Director holds it, so his row of it is not his.
        var (bobStatus, bobBody) = await Send(HttpMethod.Get, "sessions", _bobKey);
        Assert.Equal(HttpStatusCode.OK, bobStatus);
        Assert.Equal(new[] { _bobSession }, SessionIds(bobBody));
    }

    [Fact]
    public async Task GetSessions_TheOwnerAndAManager_AlsoGetOnlyTheirOwn_TheWholeTeamIsTheFleetMap()
    {
        var (ownerStatus, ownerBody) = await Send(HttpMethod.Get, "sessions", _ownerKey);
        Assert.Equal(HttpStatusCode.OK, ownerStatus);
        Assert.Equal(new[] { _ownerSession }, SessionIds(ownerBody));

        var (managerStatus, managerBody) = await Send(HttpMethod.Get, "sessions", _managerKey);
        Assert.Equal(HttpStatusCode.OK, managerStatus);
        Assert.Equal(new[] { _managerSession }, SessionIds(managerBody));
    }

    [Fact]
    public async Task GetSessions_ACollaborator_IsRefused()
    {
        var (status, _) = await Send(HttpMethod.Get, "sessions", _carolKey);
        AssertCollaboratorRefused(status, "GET", "/sessions");
    }

    // ---- GET /account/status ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AccountStatus_AMember_IsSignedIn_AndNamesNobodyElse_ACollaboratorIsRefused()
    {
        var (status, body) = await Send(HttpMethod.Get, "account/status", _aliceKey);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("signedIn").GetBoolean());
        // The team's tenant records no email, so the answer names no one - never the Owner who made the team.
        Assert.False(body.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String);
        Assert.DoesNotContain("@example.com", body.GetRawText());

        var (carol, _) = await Send(HttpMethod.Get, "account/status", _carolKey);
        AssertCollaboratorRefused(carol, "GET", "/account/status");
    }

    // ---- the team's settings, read only ----------------------------------------------------------------------------

    [Theory]
    [InlineData("gateway/session-colours")]
    [InlineData("gateway/snooze-presets")]
    [InlineData("gateway/injected-text")]
    public async Task TeamSettings_AMemberWhoRunsSessionsReadsThem_ACollaboratorIsRefused(string path)
    {
        foreach (var key in new[] { _ownerKey, _managerKey, _aliceKey })
        {
            var (status, _) = await Send(HttpMethod.Get, path, key);
            Assert.Equal(HttpStatusCode.OK, status);
        }
        var (carol, _) = await Send(HttpMethod.Get, path, _carolKey);
        AssertCollaboratorRefused(carol, "GET", "/" + path);
    }

    [Theory]
    [InlineData("gateway/snooze-presets", "{\"presets\":[5,10],\"defaultMinutes\":5}")]
    [InlineData("gateway/injected-text", "{\"use_yours\":true,\"yours\":\"changed by a member\"}")]
    public async Task TeamSettings_NoRoleMayChangeThem_NoRowOfTheTableSaysWhoMay(string path, string json)
    {
        foreach (var key in new[] { _ownerKey, _managerKey, _aliceKey })
        {
            var (status, body) = await Send(HttpMethod.Put, path, key, json);
            AssertRefused(status, body, TeamEndpointGate.UndeclaredRefusal);
        }
        var (carol, _) = await Send(HttpMethod.Put, path, _carolKey, json);
        AssertCollaboratorRefused(carol, "PUT", "/" + path);
    }

    // ---- GET /gateway/workspaces -----------------------------------------------------------------------------------

    [Fact]
    public async Task Workspaces_TheListIsCutToTheCallersOwnDirectors_ACollaboratorIsRefused()
    {
        using (_gateway.TenantBoundaryForTests.EnterScope(_team))
        {
            _gateway.WorkspacesForTest.Create(Captured("alice-drain", AliceDirector), DateTime.UtcNow);
            _gateway.WorkspacesForTest.Create(Captured("bob-drain", BobDirector), DateTime.UtcNow);
            _gateway.WorkspacesForTest.CreateAuthored(new WorkspaceDocument { Id = "by-hand", Name = "by hand" }, DateTime.UtcNow);
        }

        var (alice, aliceBody) = await Send(HttpMethod.Get, "gateway/workspaces", _aliceKey);
        Assert.Equal(HttpStatusCode.OK, alice);
        Assert.Equal(new[] { "alice-drain" }, WorkspaceIds(aliceBody));

        var (bob, bobBody) = await Send(HttpMethod.Get, "gateway/workspaces", _bobKey);
        Assert.Equal(HttpStatusCode.OK, bob);
        Assert.Equal(new[] { "bob-drain" }, WorkspaceIds(bobBody));

        // The Owner too gets only their own: a workspace is a person's sessions.
        var (owner, ownerBody) = await Send(HttpMethod.Get, "gateway/workspaces", _ownerKey);
        Assert.Equal(HttpStatusCode.OK, owner);
        Assert.Empty(WorkspaceIds(ownerBody));

        var (carol, _) = await Send(HttpMethod.Get, "gateway/workspaces", _carolKey);
        AssertCollaboratorRefused(carol, "GET", "/gateway/workspaces");
        // Reading one workspace stays undeclared, even the caller's own.
        var (one, oneBody) = await Send(HttpMethod.Get, "gateway/workspaces/alice-drain", _aliceKey);
        AssertRefused(one, oneBody, TeamEndpointGate.UndeclaredRefusal);
    }

    // ---- POST /gateway/director-errors -----------------------------------------------------------------------------

    [Fact]
    public async Task DirectorErrors_AMembersDirectorFilesItsOwn_ReadingThemBackStaysRefused_ACollaboratorIsRefused()
    {
        var batch = JsonSerializer.Serialize(new ErrorReportBatch(new[]
        {
            new ErrorReportItem(ErrorReportLimits.Director, "Test", "error", "a team Director's own error", null, null, 1,
                DateTime.UtcNow, DateTime.UtcNow, "1.0", "windows", "11", "x64", null),
        }));
        var (status, _) = await Send(HttpMethod.Post, "gateway/director-errors", _aliceKey, batch);
        Assert.Equal(HttpStatusCode.Accepted, status);

        var (read, readBody) = await Send(HttpMethod.Get, "gateway/director-errors", _aliceKey);
        AssertRefused(read, readBody, TeamEndpointGate.UndeclaredRefusal);

        var (carol, _) = await Send(HttpMethod.Post, "gateway/director-errors", _carolKey, batch);
        AssertCollaboratorRefused(carol, "POST", "/gateway/director-errors");
    }

    // ---- POST /activity-events/batch -------------------------------------------------------------------------------

    [Fact]
    public async Task ActivityEvents_TheCallersOwnDirectorAndSession_AreWritten()
    {
        var (status, body) = await Send(HttpMethod.Post, "activity-events/batch", _aliceKey, Events((AliceDirector, _aliceSession)));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body.GetProperty("written").GetInt32());
        // The PRESENCE the refused test's empty read is measured against: the same read finds this one.
        using (_gateway.TenantBoundaryForTests.EnterScope(_team))
            Assert.Single(_gateway.ActivityEventsForTest.Read(_aliceSession, ActivityEventTypes.TurnSubmitted));
    }

    [Fact]
    public async Task ActivityEvents_InAnotherDirectorsName_OrAboutAnotherPersonsSession_AreRefusedWhole()
    {
        // In Bob's Director's name: refused, and so is the event about Alice's own session riding with it.
        var (named, namedBody) = await Send(HttpMethod.Post, "activity-events/batch", _aliceKey,
            Events((AliceDirector, _aliceSession), (BobDirector, _bobSession)));
        AssertRefused(named, namedBody, TeamCallerChecks.NotThisDirectorRefusal);

        // In her own Director's name, about Bob's session (his key row names his Director): refused.
        var (session, sessionBody) = await Send(HttpMethod.Post, "activity-events/batch", _aliceKey, Events((AliceDirector, _bobSession)));
        AssertRefused(session, sessionBody, TeamCallerChecks.AnotherPersonsSessionRefusal);

        // Bob's Director lists Alice's session id, and still may not write about it.
        var (spoof, spoofBody) = await Send(HttpMethod.Post, "activity-events/batch", _bobKey, Events((BobDirector, _aliceSession)));
        AssertRefused(spoof, spoofBody, TeamCallerChecks.AnotherPersonsSessionRefusal);

        var (carol, _) = await Send(HttpMethod.Post, "activity-events/batch", _carolKey, Events((CarolDirector, _aliceSession)));
        AssertCollaboratorRefused(carol, "POST", "/activity-events/batch");

        // Nothing of any refused batch was written (the Gateway writes events of its own about these sessions, of other
        // types, so the read is of the type these batches carried).
        using (_gateway.TenantBoundaryForTests.EnterScope(_team))
        {
            Assert.Empty(_gateway.ActivityEventsForTest.Read(_bobSession, ActivityEventTypes.TurnSubmitted));
            Assert.Empty(_gateway.ActivityEventsForTest.Read(_aliceSession, ActivityEventTypes.TurnSubmitted));
        }
    }

    // ---- session numbers -------------------------------------------------------------------------------------------

    [Fact]
    public async Task SessionNumbers_AllocateIsHandedToTheCallingKeysOwnDirector_WhateverTheBodyNames()
    {
        // The body names Bob's Director; the number is handed to Alice's, the calling key's own.
        var (status, body) = await Send(HttpMethod.Post, "session-numbers/allocate", _aliceKey,
            JsonSerializer.Serialize(new { sessionId = _aliceSession, directorId = BobDirector }));
        Assert.Equal(HttpStatusCode.OK, status);
        var number = body.GetProperty("number").GetInt32();
        Assert.InRange(number, 100, 799);
        Assert.Equal(AliceDirector, _gateway.SessionNumbers.DirectorFor(_team, _aliceSession));
    }

    [Fact]
    public async Task SessionNumbers_AnotherPersonsSession_IsRefused_AndSoIsFreeingTheirNumber()
    {
        // Alice asks for a number for Bob's session (his key row names his Director): refused, nothing handed out.
        var (ask, askBody) = await Send(HttpMethod.Post, "session-numbers/allocate", _aliceKey,
            JsonSerializer.Serialize(new { sessionId = _bobSession, directorId = AliceDirector }));
        AssertRefused(ask, askBody, TeamCallerChecks.AnotherPersonsSessionRefusal);
        Assert.Null(_gateway.SessionNumbers.NumberFor(_team, _bobSession));

        // Bob numbers his own session; Alice may not free it, Bob may.
        var (bob, _) = await Send(HttpMethod.Post, "session-numbers/allocate", _bobKey,
            JsonSerializer.Serialize(new { sessionId = _bobSession, directorId = BobDirector }));
        Assert.Equal(HttpStatusCode.OK, bob);
        var bobsNumber = _gateway.SessionNumbers.NumberFor(_team, _bobSession);
        Assert.NotNull(bobsNumber);

        var (free, freeBody) = await Send(HttpMethod.Delete, $"session-numbers/{_bobSession}", _aliceKey);
        AssertRefused(free, freeBody);
        Assert.Equal(bobsNumber, _gateway.SessionNumbers.NumberFor(_team, _bobSession));

        var (own, _) = await Send(HttpMethod.Delete, $"session-numbers/{_bobSession}", _bobKey);
        Assert.Equal(HttpStatusCode.NoContent, own);
        Assert.Null(_gateway.SessionNumbers.NumberFor(_team, _bobSession));
    }

    [Fact]
    public async Task SessionNumbers_ANumberAlreadyHandedToAnotherPersonsDirector_IsNotHandedToACallerAgain()
    {
        // A session id nothing else records (no key row, nothing stored, no Director lists it), numbered by Bob first.
        var loose = Guid.NewGuid().ToString();
        var (bob, _) = await Send(HttpMethod.Post, "session-numbers/allocate", _bobKey, JsonSerializer.Serialize(new { sessionId = loose }));
        Assert.Equal(HttpStatusCode.OK, bob);

        // Alice asking for the same id would be told Bob's number: refused.
        var (alice, aliceBody) = await Send(HttpMethod.Post, "session-numbers/allocate", _aliceKey, JsonSerializer.Serialize(new { sessionId = loose }));
        AssertRefused(alice, aliceBody, TeamCallerChecks.AnotherPersonsSessionRefusal);
    }

    [Fact]
    public async Task SessionNumbers_ACollaborator_IsRefused()
    {
        var (ask, _) = await Send(HttpMethod.Post, "session-numbers/allocate", _carolKey,
            JsonSerializer.Serialize(new { sessionId = Guid.NewGuid().ToString() }));
        AssertCollaboratorRefused(ask, "POST", "/session-numbers/allocate");
        var (free, _) = await Send(HttpMethod.Delete, $"session-numbers/{_aliceSession}", _carolKey);
        AssertCollaboratorRefused(free, "DELETE", "/session-numbers/{sessionId}");
    }

    // ---- POST /gateway/skills/placement ----------------------------------------------------------------------------

    [Fact]
    public async Task SkillPlacement_IsFiledUnderTheCallingKeysOwnDirector_TheFleetViewIsTheirOwn_ACollaboratorIsRefused()
    {
        var push = JsonSerializer.Serialize(new SkillPlacementPushRequest
        {
            DirectorId = BobDirector,
            MachineName = "M-alice",
            Reports = new() { new SkillPlacementReportDto { AgentKind = "ClaudeCode", Held = 3, Reachable = 3, ObservedAtUtc = DateTime.UtcNow } },
        });
        var (status, _) = await Send(HttpMethod.Post, "gateway/skills/placement", _aliceKey, push);
        Assert.Equal(HttpStatusCode.OK, status);
        var rows = JsonSerializer.Serialize(_gateway.SkillPlacementForTest.ReadAll(_team));
        Assert.Contains(AliceDirector, rows);
        Assert.DoesNotContain(BobDirector, rows);

        // Bob's Director files its own report. The fleet view is each caller's own machines.
        var (bob, _) = await Send(HttpMethod.Post, "gateway/skills/placement", _bobKey, push.Replace("M-alice", "M-bob"));
        Assert.Equal(HttpStatusCode.OK, bob);
        var (fleet, fleetBody) = await Send(HttpMethod.Get, "gateway/skills/placement", _aliceKey);
        Assert.Equal(HttpStatusCode.OK, fleet);
        var directors = fleetBody.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("directorId").GetString()).Distinct().ToArray();
        Assert.Equal(new[] { AliceDirector }, directors);
        var (owner, ownerBody) = await Send(HttpMethod.Get, "gateway/skills/placement", _ownerKey);
        Assert.Equal(HttpStatusCode.OK, owner);
        Assert.Empty(ownerBody.GetProperty("rows").EnumerateArray());

        var (carol, _) = await Send(HttpMethod.Post, "gateway/skills/placement", _carolKey, push);
        AssertCollaboratorRefused(carol, "POST", "/gateway/skills/placement");
        var (carolRead, _) = await Send(HttpMethod.Get, "gateway/skills/placement", _carolKey);
        AssertCollaboratorRefused(carolRead, "GET", "/gateway/skills/placement");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private async Task<string> DirectorWithSession(string team, string subject, string directorId, string sessionId,
        string? sessionKey = null, params string[] extraSessionIds)
    {
        var key = TeamKey(team, subject, directorId);
        var director = await FakeTunnelDirector.StartAsync(_gateway, key, directorId);
        _directors.Add(director);
        await director.RegisterSessionKeyAsync(sessionId, sessionKey ?? GatewaySessionKey.Mint(), DateTime.UtcNow.AddHours(1));
        await director.PushSnapshotAsync(new[] { sessionId }.Concat(extraSessionIds).Select(Row).ToArray());
        return key;
    }

    private string TeamKey(string team, string subject, string directorId) =>
        _gateway.Devices.RegisterForTenant(new TenantId(team), subject,
            Api.HostedEnrollmentEndpoint.TeamScopedDeviceId(team, subject, directorId), "M-" + directorId).DeviceKey;

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

    private static WorkspaceDocument Captured(string id, string directorId) => new()
    {
        Id = id,
        Name = id,
        Origin = WorkspaceOrigins.Captured,
        Machine = "M-" + directorId,
        DirectorId = directorId,
        DirectorName = directorId,
        StartedAtUtc = DateTime.UtcNow,
    };

    private static string Events(params (string DirectorId, string SessionId)[] events) =>
        JsonSerializer.Serialize(new ActivityEventIngestRequest
        {
            Events = events.Select((e, i) => new ActivityEventRecord
            {
                EventId = Guid.NewGuid(),
                DirectorSequence = i + 1,
                OccurredUtc = DateTime.UtcNow,
                DirectorId = e.DirectorId,
                SessionId = e.SessionId,
                EventType = ActivityEventTypes.TurnSubmitted,
                Cause = ActivityCauses.OwnerSubmit,
            }).ToList(),
        });

    private static string[] SessionIds(JsonElement array) =>
        array.EnumerateArray().Select(s => s.GetProperty("sessionId").GetString()!).OrderBy(s => s, StringComparer.Ordinal).ToArray();

    private static string[] WorkspaceIds(JsonElement body) =>
        body.GetProperty("workspaces").EnumerateArray().Select(w => w.GetProperty("id").GetString()!).ToArray();

    /// <summary>
    /// A Collaborator is refused TWICE, and both are asserted. Over the wire the team key is not accepted at all
    /// (401): the device registry takes a team key only from a member whose role runs sessions. Behind that, the gate
    /// this host installed refuses the same request for a Collaborator whose key were somehow accepted - asked here with
    /// the request touching only the Collaborator's own, the most favourable case.
    /// </summary>
    private void AssertCollaboratorRefused(HttpStatusCode status, string method, string pattern)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        var verdict = _gateway.TeamGate.Check(method, pattern, _ => null, _team, () => _carol, _ => TeamOwnership.Callers);
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        // A declared route is refused by the role table's cell for a Collaborator; an undeclared one before that.
        if (verdict.Action is not null)
            Assert.Equal(TeamRole.Collaborator, verdict.Role);
    }

    private static void AssertRefused(HttpStatusCode status, JsonElement body, string? message = null)
    {
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(TeamEndpointGate.RefusalCode, body.GetProperty("code").GetString());
        if (message is not null)
            Assert.Equal(message, body.GetProperty("error").GetString());
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, string key, string? json = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (json is not null)
            req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        _out.WriteLine($"{method} {path} -> {(int)resp.StatusCode}: {(raw.Length > 400 ? raw[..400] + "..." : raw)}");
        return (resp.StatusCode, string.IsNullOrEmpty(raw) ? default : JsonDocument.Parse(raw).RootElement.Clone());
    }
}
