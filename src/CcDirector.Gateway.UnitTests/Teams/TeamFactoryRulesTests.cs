using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// FACTORIES WORK INSIDE A TEAM, FOR ITS OWNER (Team implementation 2.1, devthrottle_internal#2792). Before this piece
/// no rule named a factory route, so the gate refused every one of them in a team's tenant - the Owner's own factory
/// included. These run <see cref="TeamEndpointGate.Check"/> over a real, throwaway, migrated database with a team of
/// four, one member per role.
///
/// The walk over the HOSTED Gateway's mapped route table - every factory route, as every role - is in the Gateway
/// suite: <c>EveryFactoryRouteHasATeamRuleTests</c>. These pin each family by name.
/// </summary>
public sealed class TeamFactoryRulesTests : IDisposable
{
    private const string Owner = "sub-factory-owner";
    private const string Manager = "sub-factory-manager";
    private const string Developer = "sub-factory-developer";
    private const string Collaborator = "sub-factory-collaborator";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly DeviceRegistry _devices;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly TeamEndpointGate _gate;
    private readonly TenantId _teamTenant;

    public TeamFactoryRulesTests()
    {
        _db = _harness.Open();
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"));
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _gate = new TeamEndpointGate(new TeamAccess(_teams), _teams, _tenants,
            new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices, hosted: false));
        var team = _teams.CreateTeam(Owner, "Erik Laursen").Team!.TeamId;
        _teamTenant = new TenantId(team);
        Assert.True(_teams.AddMember(team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(team, Collaborator, TeamRole.Collaborator).IsDone);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    /// <summary>A request in the team's tenant - a key bound to the team, the way a person's browser or phone works in
    /// the team, or their Director runs its factories - by <paramref name="subject"/>.</summary>
    private TeamGateVerdict InTeam(string method, string pattern, string subject, TeamOwnership ownership = TeamOwnership.Callers) =>
        _gate.Check(method, pattern, _ => null, _teamTenant, () => subject, _ => ownership);

    /// <summary>One route of every family the factory pages and the factory inbox use: open, run, talk, the inbox, the
    /// records, memory, definition and briefs, reports, schedules, and creating a factory.</summary>
    public static IEnumerable<object[]> OneRoutePerFamily() => new[]
    {
        // Open: the list, the floor, the seats, the drawing, the activity record.
        new object[] { "GET", "/gateway/factories" },
        new object[] { "GET", "/gateway/factories/{factory}/floor" },
        new object[] { "GET", "/gateway/factories/{factory}/seats/{seat}" },
        new object[] { "GET", "/gateway/factory-agents/activity" },
        new object[] { "GET", "/gateway/factory/activity" },
        // Run: pause, resume, archive, start a seat, mark a failure handled.
        new object[] { "POST", "/gateway/factory-agents/factories/{factory}/pause" },
        new object[] { "POST", "/gateway/factory-agents/factories/{factory}/agents/{agent}/resume" },
        new object[] { "POST", "/gateway/factories/{factory}/archive" },
        new object[] { "POST", "/gateway/factory/seats/{seat}/start" },
        new object[] { "POST", "/gateway/factories/{factory}/failures/{id:guid}/handled" },
        // Talk: the boss chat, and the Factory Manager.
        new object[] { "POST", "/gateway/factory-agents/factories/{factory}/seats/{seat}/talk" },
        new object[] { "POST", "/gateway/factory-agents/factory-manager/talk" },
        // The factory inbox, from the owner's side.
        new object[] { "GET", "/gateway/factory/inbox" },
        new object[] { "POST", "/gateway/factory/{factory}/inbox" },
        new object[] { "POST", "/gateway/factory/{factory}/inbox/voice" },
        new object[] { "POST", "/gateway/factory/{factory}/inbox/dealt-with" },
        // Records, memory, definition, briefs.
        new object[] { "GET", "/gateway/factories/{factory}/records/journal" },
        new object[] { "POST", "/factory-records/{factory}/journal" },
        new object[] { "PUT", "/factory-memory/notes/{name}" },
        new object[] { "GET", "/gateway/factories/{factory}/definition" },
        new object[] { "PUT", "/factory-definitions/{factory}/draft" },
        new object[] { "GET", "/factory-definition-schemas" },
        new object[] { "POST", "/factory-briefs/{factory}/{seat}/publish" },
        new object[] { "GET", "/gateway/factory-emails/{id}" },
        new object[] { "PUT", "/gateway/factory-manager" },
        // Reports and schedules.
        new object[] { "GET", "/gateway/factory-agents/reports" },
        new object[] { "POST", "/gateway/factory-agents/reports" },
        new object[] { "GET", "/triggers" },
        new object[] { "POST", "/triggers/{id}/pause" },
        new object[] { "GET", "/cron/problems" },
        new object[] { "POST", "/cron/runs/{runId}/resolve" },
        // Creating a factory.
        new object[] { "POST", "/gateway/factories/new" },
        new object[] { "POST", "/factory-definitions/create" },
        new object[] { "POST", "/factory-definitions/import" },
    };

    /// <summary>What a Director and a factory's sessions call while they run the factory: each is the calling key's own.</summary>
    public static IEnumerable<object[]> TheRunningFactorysOwnRoutes() => new[]
    {
        new object[] { "GET", "/directors/{directorId}/triggers" },
        new object[] { "POST", "/directors/{directorId}/triggers/{id}/checks" },
        new object[] { "GET", "/directors/{directorId}/factory-checks/{factory}/{version:int}" },
        new object[] { "GET", "/directors/{directorId}/factory-check-inputs/{factory}/{version:int}" },
        new object[] { "GET", "/sessions/{sid}/factory-inbox" },
        new object[] { "POST", "/sessions/{sid}/factory-inbox/{messageId}/decide" },
        new object[] { "POST", "/sessions/{sid}/factories/{factory}/inbox" },
    };

    public static IEnumerable<object[]> EveryFactoryRoute() => OneRoutePerFamily().Concat(TheRunningFactorysOwnRoutes());

    [Theory]
    [MemberData(nameof(EveryFactoryRoute))]
    public void Find_AFactoryRoute_StatesOpenRunAndTalkToTeamFactories(string method, string pattern)
    {
        Assert.Equal(TeamAction.OpenRunAndTalkToTeamFactories, TeamEndpointRules.Find(method, pattern)?.Action);
    }

    [Theory]
    [MemberData(nameof(OneRoutePerFamily))]
    public void Check_TheOwnerWorkingInTheTeam_EveryFamilyOfFactoryRoute_IsAllowed(string method, string pattern)
    {
        var verdict = InTeam(method, pattern, Owner);

        Assert.True(verdict.Outcome == TeamGateOutcome.Allowed, $"{method} {pattern}: {verdict.Outcome} {verdict.Message}");
        Assert.Equal(TeamAction.OpenRunAndTalkToTeamFactories, verdict.Action);
        Assert.Equal(TeamRole.Owner, verdict.Role);
    }

    [Theory]
    [MemberData(nameof(TheRunningFactorysOwnRoutes))]
    public void Check_TheOwnersOwnDirectorOrSession_RunningTheTeamsFactory_IsAllowed(string method, string pattern)
    {
        // A Director's key and a session key in the team name the Owner when the Director is the Owner's
        // (TeamCallerOwnership.PersonOf), and touch the Owner's own Director or session.
        var verdict = InTeam(method, pattern, Owner, TeamOwnership.Callers);

        Assert.Equal(TeamGateOutcome.Allowed, verdict.Outcome);
    }

    [Theory]
    [MemberData(nameof(TheRunningFactorysOwnRoutes))]
    public void Check_TheOwner_AnotherMembersDirectorOrSession_IsRefusedAsWatchingIt(string method, string pattern)
    {
        var verdict = InTeam(method, pattern, Owner, TeamOwnership.SomeoneElses);

        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamAction.JoinOrWatchSomeoneElsesSession, verdict.Action);
    }

    [Theory]
    [InlineData(TeamRole.Manager)]
    [InlineData(TeamRole.Developer)]
    [InlineData(TeamRole.Collaborator)]
    public void Check_EveryRoleButTheOwner_EveryFactoryRoute_IsRefused(TeamRole role)
    {
        var subject = role switch { TeamRole.Manager => Manager, TeamRole.Developer => Developer, _ => Collaborator };
        var served = new List<string>();
        foreach (var route in EveryFactoryRoute())
        {
            foreach (var ownership in new[] { TeamOwnership.Callers, TeamOwnership.SomeoneElses, TeamOwnership.Unknown })
            {
                var verdict = InTeam((string)route[0], (string)route[1], subject, ownership);
                if (verdict.Outcome != TeamGateOutcome.Refused)
                    served.Add($"{route[0]} {route[1]} ({ownership}) -> {verdict.Outcome}");
            }
        }

        Assert.True(served.Count == 0, $"Served to a {role}:\n" + string.Join("\n", served));
    }

    [Fact]
    public void Check_ACollaborator_OnAFactoryRoute_IsToldTheRoleTableRow()
    {
        var verdict = InTeam("GET", "/gateway/factories/{factory}/floor", Collaborator);

        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamAccessDecision.RoleRefusal(TeamRole.Collaborator, TeamPermissions.Row(TeamAction.OpenRunAndTalkToTeamFactories)),
            verdict.Message);
    }

    [Theory]
    [InlineData("GET", "/missions")]
    [InlineData("GET", "/gateway/inbox")]
    [InlineData("GET", "/gateway/settings")]
    [InlineData("POST", "/gateway/factoryx")]
    [InlineData("GET", "/factory-definitionsx")]
    public void Check_AnUndeclaredRouteInTheTeamsAccount_IsStillRefusedToTheOwner(string method, string pattern)
    {
        var verdict = InTeam(method, pattern, Owner);

        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamEndpointGate.UndeclaredRefusal, verdict.Message);
    }

    [Theory]
    [MemberData(nameof(EveryFactoryRoute))]
    public void Check_TheOwnersPersonalAccountFactories_AreNotATeamRequest(string method, string pattern)
    {
        // The owner's own factories live in the owner's personal account; the gate has nothing to say about them.
        var personal = _tenants.MintOrLookupBySubject(Owner, null);

        var verdict = _gate.Check(method, pattern, _ => null, personal, () => Owner, _ => TeamOwnership.Callers);

        Assert.Equal(TeamGateOutcome.NotATeamRequest, verdict.Outcome);
    }

    [Fact]
    public void FactoryRules_AreTheOwnersAloneInTheRoleTable_AndNoneIsPersonOnly()
    {
        Assert.NotEmpty(TeamEndpointRules.FactoryRules);
        Assert.Equal(TeamGrant.Yes, TeamPermissions.Grant(TeamRole.Owner, TeamAction.OpenRunAndTalkToTeamFactories));
        foreach (var role in new[] { TeamRole.Manager, TeamRole.Developer, TeamRole.Collaborator })
            Assert.Equal(TeamGrant.No, TeamPermissions.Grant(role, TeamAction.OpenRunAndTalkToTeamFactories));

        // A Director runs a team's factory with its own key, so no factory route a Director calls may be person-only. The
        // one factory row under the team's own address is the Owner sending a factory to a contributor (Team
        // implementation 4.1): a person's click, never a Director's, and person-only like everything under /teams (M1).
        var shareRoute = CcDirector.Gateway.Api.FactoryShareEndpoints.Pattern;
        Assert.Contains(TeamEndpointRules.FactoryRules, r => r.Prefix == shareRoute);
        Assert.True(TeamPersonOnlyRoutes.IsPersonOnly("POST", shareRoute));
        foreach (var rule in TeamEndpointRules.FactoryRules.Where(r => r.Prefix != shareRoute))
            Assert.False(TeamPersonOnlyRoutes.IsPersonOnly("GET", rule.Prefix), $"{rule.Prefix} is person-only");
    }
}
