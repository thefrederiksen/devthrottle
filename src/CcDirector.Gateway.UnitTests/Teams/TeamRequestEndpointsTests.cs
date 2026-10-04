using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The request routes' own rules (devthrottle_internal#2308): only a person's own phone or browser key is served, the
/// team gate states the right action for every request route, and each outcome is the right HTTP answer. The routes
/// over real HTTP are <c>HostedTeamRequestEndpointsTests</c> in the Gateway suite.
/// </summary>
public sealed class TeamRequestEndpointsTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DeviceRegistry _devices;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly TeamEndpointGate _gate;
    private readonly string _team;

    public TeamRequestEndpointsTests()
    {
        var db = _harness.Open();
        _devices = new DeviceRegistry(db, _harness.LegacyPath("devices.json"));
        _tenants = new TenantRegistry(db);
        _teams = new TeamRegistry(db, _tenants);
        _gate = new TeamEndpointGate(new TeamAccess(_teams), _teams, _tenants,
            new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices));
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    // ---- People only ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("browser")]
    [InlineData("phone")]
    [InlineData("Browser")]
    public void RequirePerson_APersonsOwnPhoneOrBrowserKey_IsServed(string deviceType)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("dev-1", "tenant-a", deviceType, "active");

        Assert.Null(TeamRequestEndpoints.RequirePerson(ctx));
    }

    [Theory]
    [InlineData("director")]
    [InlineData("gateway")]
    [InlineData("")]
    public void RequirePerson_ADirectorsOrGatewaysDeviceKey_IsRefused(string deviceType)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("dev-1", "tenant-a", deviceType, "active");

        AssertPersonOnly(TeamRequestEndpoints.RequirePerson(ctx));
    }

    [Fact]
    public void RequirePerson_ASessionKey_IsRefusedEvenBesideAPersonsDevice()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(Guid.NewGuid(), new TenantId("tenant-a"), "director-1");
        ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("dev-1", "tenant-a", "browser", "active");

        AssertPersonOnly(TeamRequestEndpoints.RequirePerson(ctx));
    }

    [Fact]
    public void RequirePerson_TheSharedMachineTokenOrNothing_IsRefused()
    {
        var token = new DefaultHttpContext();
        token.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "machine";
        AssertPersonOnly(TeamRequestEndpoints.RequirePerson(token));
        AssertPersonOnly(TeamRequestEndpoints.RequirePerson(new DefaultHttpContext()));
    }

    private static void AssertPersonOnly(IResult? result)
    {
        var json = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, json.StatusCode);
    }

    // ---- The gate states the right action for every request route ------------------------------------------------

    [Theory]
    [InlineData("POST", "/teams/{teamId}/requests", TeamAction.AnswerQuestionsSendRequestsReadReports)]
    [InlineData("GET", "/teams/{teamId}/requests/mine", TeamAction.AnswerQuestionsSendRequestsReadReports)]
    [InlineData("GET", "/teams/{teamId}/requests", TeamAction.ReadAndDecideTeamRequests)]
    [InlineData("POST", "/teams/{teamId}/requests/{requestId}/accept", TeamAction.ReadAndDecideTeamRequests)]
    [InlineData("POST", "/teams/{teamId}/requests/{requestId}/decline", TeamAction.ReadAndDecideTeamRequests)]
    [InlineData("POST", "/teams/{teamId}/requests/{requestId}/done", TeamAction.ReadAndDecideTeamRequests)]
    public void Find_EveryRequestRoute_StatesItsAction(string method, string pattern, TeamAction action)
    {
        var rule = TeamEndpointRules.Find(method, pattern);
        Assert.NotNull(rule);
        Assert.Equal((action, TeamFrom.RouteTeamId, TeamTarget.Team), (rule!.Action, rule.TeamFrom, rule.Target));
    }

    [Theory]
    [InlineData(Owner, TeamGateOutcome.Allowed)]
    [InlineData(Manager, TeamGateOutcome.Allowed)]
    [InlineData(Developer, TeamGateOutcome.Refused)]
    [InlineData(Collaborator, TeamGateOutcome.Refused)]
    public void Check_DecidingARequest_OnlyTheOwnerAndManagersPass(string caller, TeamGateOutcome expected)
    {
        foreach (var verb in new[] { "accept", "decline", "done" })
            Assert.Equal(expected, ByRoute("POST", "/teams/{teamId}/requests/{requestId}/" + verb, caller).Outcome);
    }

    [Theory]
    [InlineData(Owner)]
    [InlineData(Manager)]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void Check_SendingAndReadingOnesOwn_EveryMemberPasses(string caller)
    {
        Assert.Equal(TeamGateOutcome.Allowed, ByRoute("POST", "/teams/{teamId}/requests", caller).Outcome);
        Assert.Equal(TeamGateOutcome.Allowed, ByRoute("GET", "/teams/{teamId}/requests/mine", caller).Outcome);
    }

    [Fact]
    public void Check_ANonMember_IsNoSuchTeamOnEveryRequestRoute()
    {
        Assert.Equal(TeamGateOutcome.NoSuchTeam, ByRoute("POST", "/teams/{teamId}/requests", "sub-stranger").Outcome);
        Assert.Equal(TeamGateOutcome.NoSuchTeam, ByRoute("GET", "/teams/{teamId}/requests", "sub-stranger").Outcome);
    }

    private TeamGateVerdict ByRoute(string method, string pattern, string subject) =>
        _gate.Check(method, pattern, name => name == "teamId" ? _team : null,
            _tenants.MintOrLookupBySubject(subject, null), () => subject, _ => TeamOwnership.Unknown);

    // ---- Each outcome is the right HTTP answer --------------------------------------------------------------------

    [Theory]
    [InlineData(TeamRequestOutcome.NoSuchTeam, StatusCodes.Status404NotFound)]
    [InlineData(TeamRequestOutcome.NoSuchRequest, StatusCodes.Status404NotFound)]
    [InlineData(TeamRequestOutcome.Refused, StatusCodes.Status403Forbidden)]
    [InlineData(TeamRequestOutcome.Invalid, StatusCodes.Status400BadRequest)]
    [InlineData(TeamRequestOutcome.Conflict, StatusCodes.Status409Conflict)]
    public void Answer_EveryRefusal_IsItsStatus(TeamRequestOutcome outcome, int status)
    {
        var result = TeamRequestEndpoints.Answer(new TeamRequestResult(outcome, "why", null), "test", StatusCodes.Status200OK);
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

        var list = TeamRequestEndpoints.AnswerList(new TeamRequestListResult(outcome, "why", Array.Empty<TeamRequestView>()), "test");
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(list).StatusCode);
    }

    [Fact]
    public void Answer_Done_IsTheSuccessStatus()
    {
        var view = new TeamRequestView("r1", "x", "sent", "Sent", "You", true, DateTime.UtcNow, DateTime.UtcNow,
            Array.Empty<TeamRequestStep>(), false, false, false);
        var result = TeamRequestEndpoints.Answer(TeamRequestResult.Done(view), "test", StatusCodes.Status201Created);
        Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}
