using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The invitation routes (devthrottle_internal#2301) in the gate every team endpoint passes through
/// (devthrottle_internal#2302), over a real, throwaway, fully migrated Gateway database with one member per role and a
/// stranger: what each route declares, who the gate lets through, and the accept page's three calls, which are declared
/// as acting for the person's own account and never inside a team.
/// </summary>
public sealed class TeamInvitationGateTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string Stranger = "sub-stranger";

    private const string Invitations = "/teams/{teamId}/invitations";
    private const string Options = "/teams/{teamId}/invitations/options";
    private const string Resend = "/teams/{teamId}/invitations/{invitationId}/resend";
    private const string Cancel = "/teams/{teamId}/invitations/{invitationId}/cancel";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly DeviceRegistry _devices;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly TeamEndpointGate _gate;
    private readonly string _team;

    public TeamInvitationGateTests()
    {
        _db = _harness.Open();
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"));
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants);
        _gate = new TeamEndpointGate(new TeamAccess(_teams), _teams, _tenants,
            new HostedTenantBoundary(new AsyncLocalTenantContext(), _devices, hosted: false));
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

    /// <summary>A request from <paramref name="subject"/>'s own account to a route that names the team.</summary>
    private TeamGateVerdict ByRoute(string method, string pattern, string subject) =>
        _gate.Check(method, pattern, name => name == "teamId" ? _team : null,
            _tenants.MintOrLookupBySubject(subject, null), () => subject, _ => TeamOwnership.Unknown);

    [Theory]
    [InlineData(Owner)]
    [InlineData(Manager)]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void ReadingTheInvitations_IsOpenToEveryMember_LikeTheMemberList(string member)
    {
        // The form tells a Developer why they cannot invite; the list the registry gives them is empty.
        foreach (var pattern in new[] { Invitations, Options })
        {
            var verdict = ByRoute("GET", pattern, member);
            Assert.Equal(TeamGateOutcome.Allowed, verdict.Outcome);
            Assert.Equal(TeamAction.SeeMembersAndRoles, verdict.Action);
        }
    }

    [Theory]
    [InlineData(Owner, true)]
    [InlineData(Manager, true)]
    [InlineData(Developer, false)]
    [InlineData(Collaborator, false)]
    public void InvitingResendingAndCancelling_NeedTheRightToInvite(string member, bool allowed)
    {
        foreach (var pattern in new[] { Invitations, Resend, Cancel })
        {
            var verdict = ByRoute("POST", pattern, member);
            Assert.Equal(allowed ? TeamGateOutcome.Allowed : TeamGateOutcome.Refused, verdict.Outcome);
            Assert.Equal(TeamAction.InviteOrRemoveDevelopersAndCollaborators, verdict.Action);
        }
    }

    [Fact]
    public void EveryInvitationRoute_ForSomeoneNotInTheTeam_IsNoSuchTeam()
    {
        foreach (var (method, pattern) in new[] { ("GET", Invitations), ("GET", Options), ("POST", Invitations), ("POST", Resend), ("POST", Cancel) })
            Assert.Equal(TeamGateOutcome.NoSuchTeam, ByRoute(method, pattern, Stranger).Outcome);
    }

    [Fact]
    public void TheAcceptPagesCalls_AreDeclaredForTheOwnAccount_NotATeamRequestFromIt_AndRefusedFromInsideATeam()
    {
        Assert.Equal(3, TeamEndpointRules.OwnAccountOnly.Count);
        foreach (var pattern in TeamEndpointRules.OwnAccountOnly)
        {
            Assert.Null(TeamEndpointRules.Find("POST", pattern));
            Assert.False(TeamEndpointRules.NamesATeam(pattern));

            // From a person's own account - which is where a person holding a link is.
            var fromOwnAccount = _gate.Check("POST", pattern, _ => null, _tenants.MintOrLookupBySubject(Stranger, null),
                () => Stranger, _ => TeamOwnership.Unknown);
            Assert.Equal(TeamGateOutcome.NotATeamRequest, fromOwnAccount.Outcome);

            // From a key bound to a team's tenant: refused like any undeclared endpoint.
            var fromInsideATeam = _gate.Check("POST", pattern, _ => null, new TenantId(_team), () => Owner, _ => TeamOwnership.Callers);
            Assert.Equal(TeamGateOutcome.Refused, fromInsideATeam.Outcome);
            Assert.Equal(TeamEndpointGate.UndeclaredRefusal, fromInsideATeam.Message);
        }
    }

    [Fact]
    public void WhoMayInviteWhom_IsTheRoleTablesCellForAddingThatRole_AndNoSecondCopy()
    {
        foreach (var inviter in Enum.GetValues<TeamRole>())
        foreach (var invited in Enum.GetValues<TeamRole>())
        {
            var fromTable = invited != TeamRole.Owner
                && TeamPermissions.Grant(inviter, TeamPermissions.ActionToAddOrRemove(invited)) == TeamGrant.Yes;
            Assert.Equal(fromTable, TeamInvitationRules.MayInvite(inviter, invited));
            Assert.Equal(fromTable, TeamInvitationRules.InviteRefusal(inviter, invited) is null);
        }
    }
}
