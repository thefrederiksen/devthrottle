using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A TEAM DIRECTOR'S OWN ROUTES (devthrottle_internal#2311, live proof F1 and F2), at the level of the rules, the one
/// ownership resolver and the checks the three body-carrying endpoints ask. Over a real database, a hosted device
/// registry, a real Director registry, session store, session key rows and number allocator: Alice and Bob (Developers)
/// each have a Director registered by its own team key, each with a session whose key row their Director wrote; the
/// Owner, a Manager and Carol (a Collaborator) are members too. The over-the-wire tests are
/// <c>HostedTeamDirectorRoutesTests</c> in the parked suite.
/// </summary>
public sealed class TeamDirectorRoutesTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Alice = "sub-alice";
    private const string Bob = "sub-bob";
    private const string Carol = "sub-carol";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly DirectorRegistry _directors;
    private readonly PushedSessionStore _sessions = new();
    private readonly SessionKeyRegistry _keys;
    private readonly FleetSessionNumberAllocator _numbers = new();
    private readonly TeamCallerOwnership _ownership;
    private readonly TeamEndpointGate _gate;
    private readonly TenantId _tenant;
    private readonly DeviceCredentialIdentity _aliceKey;
    private readonly DeviceCredentialIdentity _bobKey;

    public TeamDirectorRoutesTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _directors = new DirectorRegistry(_harness.LegacyPath("instances"));
        _keys = new SessionKeyRegistry(_db, isHosted: true);
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices);
        _ownership = new TeamCallerOwnership(_directors, _sessions, _devices, new CcDirector.Gateway.History.SessionTurnStore(_db), boundary, _keys,
            numberDirector: _numbers.DirectorFor);
        _gate = new TeamEndpointGate(new TeamAccess(_teams), _teams, _tenants, boundary, _ownership);

        var team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _tenant = new TenantId(team);
        Assert.True(_teams.AddMember(team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(team, Alice, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(team, Bob, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(team, Carol, TeamRole.Collaborator).IsDone);

        _aliceKey = DirectorWithSession(Alice, "director-alice", "session-alice");
        _bobKey = DirectorWithSession(Bob, "director-bob", "session-bob");
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    private DeviceCredentialIdentity DirectorWithSession(string subject, string directorId, string sessionId)
    {
        var deviceId = _tenant.Value + "|" + directorId;
        var key = _devices.RegisterForTenant(_tenant, subject, deviceId, "M").DeviceKey;
        var identity = _devices.ResolveCredential(key).Identity!;
        _directors.RegisterFromStream(directorId, "M", "u", "1.0", 1, DateTime.UtcNow, _tenant, directorId, "device:" + deviceId);
        _sessions.RegisterConnection(_tenant, directorId, "conn-" + directorId);
        Assert.True(_sessions.ApplySnapshot(_tenant, directorId, "conn-" + directorId, 1, new[] { new SessionDto { SessionId = sessionId } }));
        Assert.True(_keys.Register(_tenant, directorId, sessionId, "hash-" + sessionId, DateTime.UtcNow.AddHours(1)));
        return identity;
    }

    private TeamCaller CallerOf(string? person, DeviceCredentialIdentity? key) => _ownership.CallerIn(_tenant, person, key, null);

    private static Func<string, string?> Values(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    // ---- the rules ---------------------------------------------------------------------------------------------------

    /// <summary>Every route the live proof found refused (evidence/refused-routes.txt), and the rule it now states.</summary>
    public static IEnumerable<object[]> LiveProofRoutes() => new[]
    {
        new object[] { "GET", "/sessions", TeamAction.RunSessionsOnOwnComputers, TeamTarget.ListCutToCallersOwn },
        new object[] { "GET", "/account/status", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        new object[] { "GET", "/gateway/session-colours", TeamAction.RunSessionsOnOwnComputers, TeamTarget.Team },
        new object[] { "GET", "/gateway/snooze-presets", TeamAction.RunSessionsOnOwnComputers, TeamTarget.Team },
        new object[] { "GET", "/gateway/injected-text", TeamAction.RunSessionsOnOwnComputers, TeamTarget.Team },
        new object[] { "GET", "/gateway/workspaces", TeamAction.RunSessionsOnOwnComputers, TeamTarget.ListCutToCallersOwn },
        new object[] { "POST", "/gateway/director-errors", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        new object[] { "POST", "/activity-events/batch", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        new object[] { "POST", "/session-numbers/allocate", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        new object[] { "DELETE", "/session-numbers/{sessionId}", TeamAction.RunSessionsOnOwnComputers, TeamTarget.CallersOwn },
        new object[] { "POST", "/gateway/skills/placement", TeamAction.UseSharedSkillsAndWorkflows, TeamTarget.CallersOwn },
        new object[] { "GET", "/gateway/skills/placement", TeamAction.RunSessionsOnOwnComputers, TeamTarget.ListCutToCallersOwn },
    };

    [Theory]
    [MemberData(nameof(LiveProofRoutes))]
    public void Find_EveryRouteTheLiveProofFoundRefused_StatesItsRule(string method, string pattern, TeamAction action, TeamTarget target)
    {
        var rule = TeamEndpointRules.Find(method, pattern);
        Assert.NotNull(rule);
        Assert.Equal((action, target), (rule!.Action, rule.Target));
    }

    [Theory]
    [InlineData("PUT", "/gateway/snooze-presets")]
    [InlineData("PUT", "/gateway/injected-text")]
    [InlineData("GET", "/gateway/workspaces/{id}")]
    [InlineData("PUT", "/gateway/workspaces/{id}")]
    [InlineData("GET", "/gateway/director-errors")]
    [InlineData("GET", "/activity-events")]
    public void Find_TheOwnerDecisionsLeftOpen_StayUndeclared_AndAreRefusedEvenToTheOwner(string method, string pattern)
    {
        Assert.Null(TeamEndpointRules.Find(method, pattern));
        var verdict = _gate.Check(method, pattern, _ => null, _tenant, () => Owner, _ => TeamOwnership.Callers);
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamEndpointGate.UndeclaredRefusal, verdict.Message);
    }

    // ---- the gate ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Owner)]
    [InlineData(Manager)]
    [InlineData(Alice)]
    public void Check_AListCutToTheCallersOwn_IsAllowedToEveryRoleThatRunsSessions_AndNamesTheCaller(string subject)
    {
        foreach (var pattern in new[] { "/sessions", "/gateway/workspaces", "/gateway/skills/placement" })
        {
            // Whose is never asked of a cut list: the endpoint keeps only the caller's own.
            var verdict = _gate.Check("GET", pattern, _ => null, _tenant, () => subject,
                _ => throw new InvalidOperationException("ownership is not asked of a list cut to the caller's own"));
            Assert.Equal(TeamGateOutcome.Allowed, verdict.Outcome);
            Assert.Equal(subject, verdict.Caller);
            Assert.Equal(_tenant.Value, verdict.TeamId);
        }
    }

    [Fact]
    public void Check_EveryNewRule_RefusesACollaborator_AndSomeoneWhoIsNotAMember()
    {
        foreach (var row in LiveProofRoutes())
        {
            var (method, pattern) = ((string)row[0], (string)row[1]);
            Assert.Equal(TeamGateOutcome.Refused, _gate.Check(method, pattern, _ => null, _tenant, () => Carol, _ => TeamOwnership.Callers).Outcome);
            Assert.Equal(TeamGateOutcome.Refused, _gate.Check(method, pattern, _ => null, _tenant, () => "sub-stranger", _ => TeamOwnership.Callers).Outcome);
        }
    }

    [Fact]
    public void Check_ARefusedRequest_NamesNoCaller()
    {
        var verdict = _gate.Check("GET", "/sessions", _ => null, _tenant, () => Carol, _ => TeamOwnership.Callers);
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Null(verdict.Caller);
        Assert.Null(verdict.TeamId);
    }

    // ---- whose (the one resolver) ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/account/status")]
    [InlineData("/gateway/director-errors")]
    [InlineData("/session-numbers/allocate")]
    [InlineData("/gateway/skills/placement")]
    [InlineData("/activity-events/batch")]
    public void Whose_ARouteAboutTheCallingKey_IsTheCallersOwn(string pattern)
    {
        Assert.Equal(TeamOwnership.Callers, _ownership.Whose(_tenant, Alice, pattern, Values(), "POST"));
    }

    [Fact]
    public void Whose_FreeingANumber_IsTheCallersOnlyForTheirOwnDirectorsNumber()
    {
        _numbers.Allocate(_tenant, "session-alice", "director-alice");
        _numbers.Allocate(_tenant, "session-bob", "director-bob");

        Assert.Equal(TeamOwnership.Callers, _ownership.Whose(_tenant, Alice, "/session-numbers/{sessionId}", Values(("sessionId", "session-alice")), "DELETE"));
        Assert.Equal(TeamOwnership.SomeoneElses, _ownership.Whose(_tenant, Alice, "/session-numbers/{sessionId}", Values(("sessionId", "session-bob")), "DELETE"));
        Assert.Equal(TeamOwnership.Unknown, _ownership.Whose(_tenant, Alice, "/session-numbers/{sessionId}", Values(("sessionId", "never-numbered")), "DELETE"));

        // And the gate turns Bob's into watching his session, which no role may.
        var verdict = _gate.Check("DELETE", "/session-numbers/{sessionId}", Values(("sessionId", "session-bob")), _tenant, () => Alice,
            rule => _ownership.Whose(_tenant, Alice, "/session-numbers/{sessionId}", Values(("sessionId", "session-bob")), "DELETE"));
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamAction.JoinOrWatchSomeoneElsesSession, verdict.Action);
    }

    // ---- the caller and the checks -----------------------------------------------------------------------------------

    [Fact]
    public void CallerIn_ADeviceKey_IsItsEnrolledDirector_ASessionKey_IsItsDirector_AnotherTenantsKey_IsNobodys()
    {
        var alice = CallerOf(Alice, _aliceKey);
        Assert.Equal("director-alice", alice.KeyDirector);
        Assert.True(alice.OwnsDirector("director-alice"));
        Assert.False(alice.OwnsDirector("director-bob"));
        Assert.True(alice.IsKeyDirector("director-alice"));
        Assert.False(alice.IsKeyDirector("director-bob"));

        var bySession = _ownership.CallerIn(_tenant, Bob, null, new SessionCredentialIdentity(Guid.NewGuid(), _tenant, "director-bob"));
        Assert.Equal("director-bob", bySession.KeyDirector);

        var elsewhere = _ownership.CallerIn(_tenant, Alice, _aliceKey with { TenantId = Guid.NewGuid().ToString() }, null);
        Assert.Null(elsewhere.KeyDirector);
        Assert.Equal(TeamSessionClaim.NoPerson, elsewhere.ClaimOfSession("session-alice"));

        var nobody = CallerOf(null, _aliceKey);
        Assert.False(nobody.OwnsDirector("director-alice"));
    }

    [Fact]
    public void RefuseNumber_TheCallersOwnSession_OrOneNothingRecordsYet_IsHandedOut()
    {
        Assert.Null(TeamCallerChecks.RefuseNumber(CallerOf(Alice, _aliceKey), "session-alice", numberedFor: null));
        Assert.Null(TeamCallerChecks.RefuseNumber(CallerOf(Alice, _aliceKey), "a-new-session", numberedFor: null));
        Assert.Null(TeamCallerChecks.RefuseNumber(CallerOf(Alice, _aliceKey), "session-alice", numberedFor: "director-alice"));
    }

    [Fact]
    public void RefuseNumber_AnotherPersonsSession_OrNumber_OrNoCaller_IsRefused()
    {
        Assert.Equal(TeamCallerChecks.AnotherPersonsSessionRefusal,
            TeamCallerChecks.RefuseNumber(CallerOf(Alice, _aliceKey), "session-bob", numberedFor: null));
        Assert.Equal(TeamCallerChecks.AnotherPersonsSessionRefusal,
            TeamCallerChecks.RefuseNumber(CallerOf(Alice, _aliceKey), "a-new-session", numberedFor: "director-bob"));
        Assert.Equal(TeamCallerChecks.NoKeyDirectorRefusal,
            TeamCallerChecks.RefuseNumber(CallerOf(null, _aliceKey), "session-alice", numberedFor: null));
        Assert.Equal(TeamCallerChecks.NoKeyDirectorRefusal,
            TeamCallerChecks.RefuseNumber(CallerOf(Alice, null), "session-alice", numberedFor: null));
    }

    [Fact]
    public void RefuseActivityBatch_OwnDirectorAndOwnSessions_IsWritten_AnythingElseRefusesTheWholeBatch()
    {
        var alice = CallerOf(Alice, _aliceKey);
        Assert.Null(TeamCallerChecks.RefuseActivityBatch(alice, new[] { ("director-alice", "session-alice"), ("director-alice", "a-new-session") }));

        Assert.Equal(TeamCallerChecks.NotThisDirectorRefusal,
            TeamCallerChecks.RefuseActivityBatch(alice, new[] { ("director-alice", "session-alice"), ("director-bob", "session-bob") }));
        Assert.Equal(TeamCallerChecks.AnotherPersonsSessionRefusal,
            TeamCallerChecks.RefuseActivityBatch(alice, new[] { ("director-alice", "session-alice"), ("director-alice", "session-bob") }));
        Assert.Equal(TeamCallerChecks.NoKeyDirectorRefusal,
            TeamCallerChecks.RefuseActivityBatch(CallerOf(null, _aliceKey), new[] { ("director-alice", "session-alice") }));
    }

    [Fact]
    public void RefusePlacement_NeedsACallerAndAKeyDirector()
    {
        Assert.Null(TeamCallerChecks.RefusePlacement(CallerOf(Alice, _aliceKey)));
        Assert.Equal(TeamCallerChecks.NoKeyDirectorRefusal, TeamCallerChecks.RefusePlacement(CallerOf(Alice, null)));
        Assert.Equal(TeamCallerChecks.NoKeyDirectorRefusal, TeamCallerChecks.RefusePlacement(CallerOf(null, _bobKey)));
    }
}
