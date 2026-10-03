using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The gate every endpoint that acts in a team passes through (devthrottle_internal#2302), over a real, throwaway,
/// fully migrated Gateway database with a real team of four - one member per role - and a stranger.
///
/// <see cref="TeamEndpointGate.Check"/> is the server's decision for one request. Its inputs are what the request's
/// credential and route say; the per-cell tests below supply the caller and whether the request touches the caller's
/// own things, because on today's Gateway nothing supplies them inside a team's tenant (devthrottle_internal#2311
/// does - see the class comment on the gate). The middleware tests at the end run <see cref="TeamEndpointGate.RunAsync"/>
/// with exactly what production supplies, and show it refuses.
/// </summary>
public sealed class TeamEndpointGateTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string Stranger = "sub-stranger";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly DeviceRegistry _devices;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly TeamEndpointGate _gate;
    private readonly string _team;
    private readonly TenantId _teamTenant;

    public TeamEndpointGateTests()
    {
        _db = _harness.Open();
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"));
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _gate = new TeamEndpointGate(new TeamAccess(_teams), _teams, _tenants,
            new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices));
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _teamTenant = new TenantId(_team);
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    private static string SubjectFor(TeamRole role) => role switch
    {
        TeamRole.Owner => Owner,
        TeamRole.Manager => Manager,
        TeamRole.Developer => Developer,
        _ => Collaborator,
    };

    /// <summary>A request inside the team's tenant, by <paramref name="subject"/>, touching what
    /// <paramref name="ownership"/> says.</summary>
    private TeamGateVerdict InTeam(string method, string pattern, string? subject, TeamOwnership ownership) =>
        _gate.Check(method, pattern, _ => null, _teamTenant, () => subject, _ => ownership);

    /// <summary>A request from a personal account to a route that names the team.</summary>
    private TeamGateVerdict ByRoute(string method, string pattern, string? subject, string? teamId) =>
        _gate.Check(method, pattern, name => name == "teamId" ? teamId : null,
            _tenants.MintOrLookupBySubject(subject ?? Stranger, null), () => subject, _ => TeamOwnership.Unknown);

    private TeamGateVerdict Call(RoleTableSpec.SpecRow row, TeamRole role) =>
        row.Pattern!.StartsWith("/teams/{teamId}", StringComparison.Ordinal)
            ? ByRoute(row.Method!, row.Pattern, SubjectFor(role), _team)
            : InTeam(row.Method!, row.Pattern, SubjectFor(role), row.Ownership);

    // ---- Test 1 of #2302: one test per cell, at the endpoint the row is called through today ---------------------

    [Theory]
    [MemberData(nameof(RoleTableSpec.EveryCellWithAnEndpointToday), MemberType = typeof(RoleTableSpec))]
    public void Check_EveryCellWithAnEndpointToday_TheServerGivesTheCellsAnswer(TeamAction action, TeamRole role)
    {
        var row = RoleTableSpec.Row(action);

        var verdict = Call(row, role);

        // A "no" cell is refused by the server. An "only their own" cell is refused on an endpoint that answers for the
        // whole team (the Fleet Map list, until devthrottle_internal#2312 can cut it to the caller's own Directors).
        var expected = RoleTableSpec.Cell(action, role) == TeamGrant.Yes ? TeamGateOutcome.Allowed : TeamGateOutcome.Refused;
        Assert.Equal(expected, verdict.Outcome);
        Assert.Equal(action, verdict.Action);
        Assert.Equal(role, verdict.Role);
        Assert.Equal(expected == TeamGateOutcome.Refused, !string.IsNullOrEmpty(verdict.Message));
    }

    [Theory]
    [MemberData(nameof(RoleTableSpec.EveryCellWaitingOnALaterIssue), MemberType = typeof(RoleTableSpec))]
    public void Decide_EveryCellWhoseEndpointWaitsOnALaterIssue_ThePolicyGivesTheCellsAnswer(TeamAction action, TeamRole role)
    {
        Assert.NotNull(RoleTableSpec.Row(action).WaitsOn);
        var decision = new TeamAccess(_teams).Decide(_team, SubjectFor(role), action);
        Assert.Equal(RoleTableSpec.Cell(action, role) != TeamGrant.No, decision.Allowed);
    }

    // ---- Test 2 of #2302: a Collaborator calling any session, computer, Mentor or skills endpoint is refused -------

    [Theory]
    [InlineData(TeamOwnership.Callers)]
    [InlineData(TeamOwnership.SomeoneElses)]
    [InlineData(TeamOwnership.Unknown)]
    public void Check_ACollaborator_EverySessionComputerMentorAndSkillsRule_IsRefused(TeamOwnership ownership)
    {
        foreach (var rule in TeamEndpointRules.All.Where(r => r.TeamFrom == TeamFrom.RequestTenant))
        {
            var method = rule.Methods == TeamMethods.Write ? "POST" : "GET";
            var verdict = InTeam(method, rule.Prefix, Collaborator, ownership);
            Assert.True(verdict.Outcome == TeamGateOutcome.Refused, $"{method} {rule.Prefix} was not refused for a Collaborator");
        }
    }

    // ---- Test 3 of #2302: no role reads another person's live session or full transcript --------------------------

    [Theory]
    [InlineData(TeamRole.Owner)]
    [InlineData(TeamRole.Manager)]
    [InlineData(TeamRole.Developer)]
    [InlineData(TeamRole.Collaborator)]
    public void Check_AnyRole_AnotherPersonsLiveSessionOrTranscript_IsRefused(TeamRole role)
    {
        foreach (var pattern in new[] { "/sessions/{sid}/stream", "/sessions/{sid}/buffer", "/sessions/{sid}/history", "/history/sessions/{sessionId}", "/history/sessions" })
        {
            var verdict = InTeam("GET", pattern, SubjectFor(role), TeamOwnership.SomeoneElses);
            Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
            Assert.Equal(TeamAction.JoinOrWatchSomeoneElsesSession, verdict.Action);
        }

        // And joining it - typing into it - is the same refusal.
        Assert.Equal(TeamGateOutcome.Refused, InTeam("POST", "/sessions/{sid}/prompt", SubjectFor(role), TeamOwnership.SomeoneElses).Outcome);
    }

    // ---- Test 4 of #2302: a Manager reading another person's prompts is refused, except the Mentor's quotes --------

    [Fact]
    public void Check_AManager_AnotherPersonsPrompts_IsRefused_ButTheMentorQuotedPromptsAreGranted()
    {
        foreach (var pattern in new[] { "/prompts", "/prompts/export", "/transcription/turns" })
        {
            var verdict = InTeam("GET", pattern, Manager, TeamOwnership.SomeoneElses);
            Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
            Assert.Equal(TeamAction.ReadAnotherPersonsPrompts, verdict.Action);
        }

        // The one exception, a named action for the Mentor page (devthrottle_internal#2305), which builds its endpoint.
        Assert.True(new TeamAccess(_teams).Decide(_team, Manager, TeamAction.ReadPromptsQuotedOnMentorPage).Allowed);
    }

    // ---- Who is asking, and whose things ---------------------------------------------------------------------------

    [Fact]
    public void Check_NotAMember_IsRefusedEveryDeclaredRule_AndTheMembersRouteSaysThereIsNoSuchTeam()
    {
        foreach (var rule in TeamEndpointRules.All)
        {
            var method = rule.Methods == TeamMethods.Write ? "POST" : "GET";
            var verdict = rule.TeamFrom == TeamFrom.RouteTeamId
                ? ByRoute(method, rule.Prefix, Stranger, _team)
                : InTeam(method, rule.Prefix, Stranger, TeamOwnership.Callers);
            Assert.Equal(rule.TeamFrom == TeamFrom.RouteTeamId ? TeamGateOutcome.NoSuchTeam : TeamGateOutcome.Refused, verdict.Outcome);
        }
    }

    [Fact]
    public void Check_AnEndpointThatStatesNoAction_IsRefusedInATeam()
    {
        var verdict = InTeam("GET", "/missions", Owner, TeamOwnership.Callers);
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamEndpointGate.UndeclaredRefusal, verdict.Message);
        Assert.Null(verdict.Action);
    }

    [Fact]
    public void Check_NoEndpointReached_InATeam_IsRefused()
    {
        Assert.Equal(TeamGateOutcome.Refused, InTeam("GET", null!, Owner, TeamOwnership.Callers).Outcome);
    }

    [Fact]
    public void Check_TheCallerCannotBeIdentified_IsRefusedAndNeverGuessed()
    {
        var verdict = InTeam("GET", "/gateway/skills", null, TeamOwnership.Callers);
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamEndpointGate.CallerUnknownRefusal, verdict.Message);
    }

    [Fact]
    public void Check_WhetherItIsTheCallersOwnCannotBeShown_IsRefused()
    {
        var verdict = InTeam("POST", "/sessions/{sid}/prompt", Owner, TeamOwnership.Unknown);
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamEndpointGate.OwnershipUnknownRefusal, verdict.Message);
    }

    [Fact]
    public void Check_ARoleWithNoForTheAction_GetsTheRolesRefusal_NotTheOwnershipOne()
    {
        var verdict = InTeam("POST", "/sessions/{sid}/prompt", Collaborator, TeamOwnership.Unknown);
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamAccessDecision.RoleRefusal(TeamRole.Collaborator, TeamPermissions.Row(TeamAction.RunSessionsOnOwnComputers)), verdict.Message);
    }

    [Fact]
    public void Check_TheCallersOwnSession_IsAllowedForEveryRoleThatRunsSessions()
    {
        foreach (var role in new[] { TeamRole.Owner, TeamRole.Manager, TeamRole.Developer })
            Assert.Equal(TeamGateOutcome.Allowed, InTeam("GET", "/sessions/{sid}/stream", SubjectFor(role), TeamOwnership.Callers).Outcome);
    }

    [Fact]
    public void Check_APersonalAccountRequest_IsNotATeamRequest_AndNeverAsksWhoOrWhose()
    {
        var personal = _tenants.MintOrLookupBySubject(Owner, null);
        var verdict = _gate.Check("POST", "/sessions/{sid}/prompt", _ => null, personal,
            () => throw new InvalidOperationException("asked who"), _ => throw new InvalidOperationException("asked whose"));
        Assert.Equal(TeamGateOutcome.NotATeamRequest, verdict.Outcome);
        Assert.Same(TeamGateVerdict.NotATeamRequest, verdict);
    }

    [Fact]
    public void Check_NoTenant_IsNotATeamRequest()
    {
        Assert.Equal(TeamGateOutcome.NotATeamRequest,
            _gate.Check("GET", "/healthz", _ => null, null, () => null, _ => TeamOwnership.Unknown).Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Check_TheMembersRouteWithNoTeamId_SaysThereIsNoSuchTeam(string? teamId)
    {
        Assert.Equal(TeamGateOutcome.NoSuchTeam, ByRoute("GET", "/teams/{teamId}/members", Owner, teamId).Outcome);
    }

    [Fact]
    public void Check_TheMembersRouteForATeamThatDoesNotExist_SaysThereIsNoSuchTeam()
    {
        var verdict = ByRoute("GET", "/teams/{teamId}/members", Owner, Guid.NewGuid().ToString());
        Assert.Equal(TeamGateOutcome.NoSuchTeam, verdict.Outcome);
        Assert.Equal(TeamEndpoints.NoSuchTeamRefusal, verdict.Message);
    }

    [Fact]
    public void Check_ADevelopersOwnOnlyFleetMap_IsRefusedWithTheReason()
    {
        var verdict = InTeam("GET", "/directors", Developer, TeamOwnership.Unknown);
        Assert.Equal(TeamGateOutcome.Refused, verdict.Outcome);
        Assert.Equal(TeamEndpointGate.OwnOnlyRefusal(TeamRole.Developer, TeamAction.SeeFleetMap), verdict.Message);
        Assert.Contains("only for their own", verdict.Message);
    }

    [Fact]
    public void Check_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => _gate.Check(null!, "/x", _ => null, null, () => null, _ => TeamOwnership.Unknown));
        Assert.Throws<ArgumentNullException>(() => _gate.Check("GET", "/x", null!, null, () => null, _ => TeamOwnership.Unknown));
        Assert.Throws<ArgumentNullException>(() => _gate.Check("GET", "/x", _ => null, null, null!, _ => TeamOwnership.Unknown));
        Assert.Throws<ArgumentNullException>(() => _gate.Check("GET", "/x", _ => null, null, () => null, null!));
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var boundary = new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices);
        var access = new TeamAccess(_teams);
        Assert.Throws<ArgumentNullException>(() => new TeamEndpointGate(null!, _teams, _tenants, boundary));
        Assert.Throws<ArgumentNullException>(() => new TeamEndpointGate(access, null!, _tenants, boundary));
        Assert.Throws<ArgumentNullException>(() => new TeamEndpointGate(access, _teams, null!, boundary));
        Assert.Throws<ArgumentNullException>(() => new TeamEndpointGate(access, _teams, _tenants, null!));
    }

    // ---- The middleware, with exactly what production supplies ----------------------------------------------------

    /// <summary>A request as the auth middleware leaves it: the endpoint routing chose, and the authenticated device
    /// identity stashed with the tenant its key is bound to.</summary>
    private static DefaultHttpContext Request(string method, string pattern, string? boundTenant, string? teamIdRouteValue = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        ctx.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, EndpointMetadataCollection.Empty, pattern));
        if (teamIdRouteValue is not null)
            ctx.Request.RouteValues["teamId"] = teamIdRouteValue;
        if (boundTenant is not null)
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("device-1", boundTenant, "director", "active");
        return ctx;
    }

    private static JsonElement Body(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return JsonDocument.Parse(ctx.Response.Body).RootElement.Clone();
    }

    [Fact]
    public async Task RunAsync_AKeyBoundToATeamsTenant_IsRefused_BecauseTodayNothingSaysWhichMemberHoldsIt()
    {
        var ctx = Request("GET", "/gateway/skills", _team);
        var reached = false;

        await _gate.RunAsync(ctx, () => { reached = true; return Task.CompletedTask; });

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        var body = Body(ctx);
        Assert.Equal(TeamEndpointGate.RefusalCode, body.GetProperty("code").GetString());
        Assert.Equal(TeamEndpointGate.CallerUnknownRefusal, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunAsync_AnUndeclaredEndpointInATeamsTenant_IsRefusedAsUndeclared()
    {
        var ctx = Request("GET", "/missions", _team);
        await _gate.RunAsync(ctx, () => throw new InvalidOperationException("the endpoint ran"));
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal(TeamEndpointGate.UndeclaredRefusal, Body(ctx).GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunAsync_APersonalAccountsRequest_GoesOnUntouched()
    {
        var personal = _tenants.MintOrLookupBySubject(Developer, null);
        var ctx = Request("POST", "/sessions/{sid}/prompt", personal.Value);
        var reached = false;

        await _gate.RunAsync(ctx, () => { reached = true; return Task.CompletedTask; });

        Assert.True(reached);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task RunAsync_TheMembersRoute_AMemberGoesOn_AStrangerIsToldThereIsNoSuchTeam()
    {
        var member = Request("GET", "/teams/{teamId}/members", _tenants.MintOrLookupBySubject(Collaborator, null).Value, _team);
        var reached = false;
        await _gate.RunAsync(member, () => { reached = true; return Task.CompletedTask; });
        Assert.True(reached);

        var stranger = Request("GET", "/teams/{teamId}/members", _tenants.MintOrLookupBySubject(Stranger, null).Value, _team);
        await _gate.RunAsync(stranger, () => throw new InvalidOperationException("the endpoint ran"));
        Assert.Equal(StatusCodes.Status404NotFound, stranger.Response.StatusCode);
        Assert.Equal(TeamEndpoints.NoSuchTeamRefusal, Body(stranger).GetProperty("error").GetString());
    }

    [Fact]
    public async Task RunAsync_NoEndpointAndNoKey_GoesOn()
    {
        var ctx = new DefaultHttpContext();
        var reached = false;
        await _gate.RunAsync(ctx, () => { reached = true; return Task.CompletedTask; });
        Assert.True(reached);
    }

    [Fact]
    public async Task RunAsync_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _gate.RunAsync(null!, () => Task.CompletedTask));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _gate.RunAsync(new DefaultHttpContext(), null!));
    }
}
