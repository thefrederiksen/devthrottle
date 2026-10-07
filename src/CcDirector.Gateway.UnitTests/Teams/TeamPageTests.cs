using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The Team page (screen S1, devthrottle_internal#2303) over a real, throwaway, fully migrated Gateway database: the
/// page's model for each role, change a member's role, remove a member, and the seat count a paid-seat change records
/// on the team's own bill (Teams v1, the team bill without Stripe) - exactly when the paid-seat count moved, and only
/// then. Nothing leaves the process. The clock is injected; nothing sleeps.
/// </summary>
public sealed class TeamPageTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Manager2 = "sub-manager-2";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string Stranger = "sub-stranger";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private DateTime _now = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _team;

    public TeamPageTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants, () => _now,
            readTeamBill: _ => new TeamBill(true, true, EntitlementRegistry.StatusActive, 4));

        _tenants.MintOrLookupBySubject(Owner, "soren@acme.example");
        _tenants.MintOrLookupBySubject(Manager, "priya@acme.example");
        _tenants.MintOrLookupBySubject(Manager2, "pat@acme.example");
        _tenants.MintOrLookupBySubject(Developer, "rob@acme.example");
        _tenants.MintOrLookupBySubject(Collaborator, "mike@client.example");
        _tenants.MintOrLookupBySubject(Stranger, "stranger@else.example");
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _teams.AddMember(_team, Manager, TeamRole.Manager);
        _teams.AddMember(_team, Manager2, TeamRole.Manager);
        _teams.AddMember(_team, Developer, TeamRole.Developer);
        _teams.AddMember(_team, Collaborator, TeamRole.Collaborator);
        // The team's plan, at its four paid seats: the Owner, two Managers and the Developer.
        TeamBillSeed.Active(_db, _team, seats: 4);
    }

    public void Dispose() => _harness.Dispose();

    // ---- The page, per role ---------------------------------------------------------------------------------------

    [Fact]
    public void DescribeTeamPage_Owner_EveryoneButThemselvesHasADropdownAndRemove()
    {
        var page = PageFor(Owner);

        Assert.Equal("Owner", page.YourRole);
        Assert.True(page.CanInvite);
        var me = Row(page, Owner);
        Assert.True(me.IsYou);
        Assert.False(me.CanChangeRole);
        Assert.Empty(me.RoleChoices);
        Assert.False(me.CanRemove);
        Assert.Null(me.RemoveWarning);
        foreach (var other in new[] { Manager, Manager2, Developer, Collaborator })
        {
            var row = Row(page, other);
            Assert.True(row.CanChangeRole, other);
            Assert.Equal(new[] { "Manager", "Developer", "Collaborator" }, row.RoleChoices);
            Assert.True(row.CanRemove, other);
            Assert.False(string.IsNullOrWhiteSpace(row.RemoveWarning));
        }
    }

    [Fact]
    public void DescribeTeamPage_Manager_RoleIsPlainText_AndRemovesOnlyDevelopersAndCollaborators()
    {
        var page = PageFor(Manager);

        Assert.Equal("Manager", page.YourRole);
        Assert.True(page.CanInvite);
        Assert.All(page.Members, m => Assert.False(m.CanChangeRole));
        Assert.All(page.Members, m => Assert.Empty(m.RoleChoices));
        Assert.False(Row(page, Owner).CanRemove);
        Assert.False(Row(page, Manager).CanRemove);
        Assert.False(Row(page, Manager2).CanRemove);
        Assert.True(Row(page, Developer).CanRemove);
        Assert.True(Row(page, Collaborator).CanRemove);
    }

    [Fact]
    public void DescribeTeamPage_Developer_TheListAndNothingToClick()
    {
        var invited = _teams.CreateInvitation(_team, Owner, "anna@acme.example", TeamRole.Developer);
        Assert.Equal(TeamInvitationOutcome.Done, invited.Outcome);

        var page = PageFor(Developer);

        Assert.Equal("Developer", page.YourRole);
        Assert.False(page.CanInvite);
        Assert.Equal(5, page.Members.Count);
        Assert.All(page.Members, m => Assert.False(m.CanChangeRole));
        Assert.All(page.Members, m => Assert.False(m.CanRemove));
        Assert.Empty(page.Invitations);
        // Someone who cannot see invitations is not told how many are waiting.
        Assert.Equal("4 paid seats, 1 Collaborator (no charge)", page.Summary);
    }

    [Fact]
    public void DescribeTeamPage_Collaborator_HasNoTeamPage_InTheRoleTablesWords()
    {
        var result = _teams.DescribeTeamPage(_team, Collaborator);

        Assert.Equal(TeamPageOutcome.Forbidden, result.Outcome);
        Assert.Null(result.Page);
        Assert.Equal(TeamAccessDecision.RoleRefusal(TeamRole.Collaborator, TeamPermissions.Row(TeamAction.SeeTeamPage)), result.Refusal);
    }

    [Fact]
    public void DescribeTeamPage_NotAMemberOrNoSuchTeam_IsNotFound()
    {
        Assert.Equal(TeamPageOutcome.NotFound, _teams.DescribeTeamPage(_team, Stranger).Outcome);
        Assert.Equal(TeamPageOutcome.NotFound, _teams.DescribeTeamPage(Guid.NewGuid().ToString(), Owner).Outcome);
        Assert.Equal(TeamPageOutcome.NotFound, _teams.DescribeTeamPage("", Owner).Outcome);
    }

    [Fact]
    public void DescribeTeamPage_SeatsAndWaitingInvitations_AreCountedAndLabelled()
    {
        var paid = _teams.CreateInvitation(_team, Manager, "anna@acme.example", TeamRole.Developer).Invitation!;
        var free = _teams.CreateInvitation(_team, Owner, "james@client.example", TeamRole.Collaborator).Invitation!;
        var cancelled = _teams.CreateInvitation(_team, Owner, "gone@acme.example", TeamRole.Developer).Invitation!;
        _teams.CancelInvitation(_team, cancelled.Id, Owner);

        var page = PageFor(Owner);

        Assert.Equal("4 paid seats, 1 Collaborator (no charge), 2 invitations waiting", page.Summary);
        Assert.Equal("Paid", Row(page, Developer).Seat);
        Assert.Equal("Paid", Row(page, Owner).Seat);
        Assert.Equal("No charge", Row(page, Collaborator).Seat);
        Assert.Equal(2, page.Invitations.Count);
        var anna = page.Invitations.Single(i => i.Id == paid.Id);
        Assert.Equal("Paid when accepted", anna.Seat);
        Assert.Equal("priya@acme.example", anna.InvitedBy);
        Assert.Equal(_now.AddDays(7), anna.ExpiresAtUtc);
        Assert.True(anna.CanResend);
        Assert.True(anna.CanCancel);
        Assert.Equal("No charge", page.Invitations.Single(i => i.Id == free.Id).Seat);
    }

    [Fact]
    public void DescribeTeamPage_ManagerAndAManagerInvitation_MayNeitherResendNorCancelIt()
    {
        var manager = _teams.CreateInvitation(_team, Owner, "boss@acme.example", TeamRole.Manager).Invitation!;

        var row = PageFor(Manager).Invitations.Single(i => i.Id == manager.Id);

        Assert.False(row.CanResend);
        Assert.False(row.CanCancel);
    }

    [Fact]
    public void DescribeTeamPage_AnExpiredInvitation_IsListedWithResendAndCancel_AndIsNotCountedAsWaiting()
    {
        var lapsed = _teams.CreateInvitation(_team, Owner, "wrong.address@acme.example", TeamRole.Developer).Invitation!;
        _now = _now.AddDays(8);
        var live = _teams.CreateInvitation(_team, Owner, "anna@acme.example", TeamRole.Developer).Invitation!;

        var page = PageFor(Owner);

        var expired = page.Invitations.Single(i => i.Id == lapsed.Id);
        Assert.Equal(TeamInvitationStates.Expired, expired.State);
        Assert.True(expired.CanResend);
        Assert.True(expired.CanCancel);
        Assert.Equal(TeamInvitationStates.Sent, page.Invitations.Single(i => i.Id == live.Id).State);
        // Waiting means waiting on someone: the lapsed one is listed but not counted (review F1).
        Assert.Equal("4 paid seats, 1 Collaborator (no charge), 1 invitation waiting", page.Summary);
    }

    [Fact]
    public void CancelInvitation_AnExpiredInvitation_ClearsItFromThePage_WithoutSendingAnything()
    {
        var lapsed = _teams.CreateInvitation(_team, Owner, "wrong.address@acme.example", TeamRole.Developer).Invitation!;
        _now = _now.AddDays(8);

        Assert.Equal(TeamInvitationOutcome.Done, _teams.CancelInvitation(_team, lapsed.Id, Owner).Outcome);

        var page = PageFor(Owner);
        Assert.Empty(page.Invitations);
        Assert.Equal("4 paid seats, 1 Collaborator (no charge), 0 invitations waiting", page.Summary);
    }

    // ---- Change a role --------------------------------------------------------------------------------------------

    [Fact]
    public void ChangeMemberRole_Owner_ChangesItOnTheServer()
    {
        var result = _teams.ChangeMemberRole(_team, Owner, IdOf(Developer), TeamRole.Manager);

        Assert.Equal(TeamMemberChangeOutcome.Done, result.Outcome);
        Assert.Equal(TeamRole.Manager, _teams.RoleOf(_team, Developer));
    }

    [Fact]
    public void ChangeMemberRole_Manager_IsRefusedInTheRoleTablesWords_AndNothingChanges()
    {
        var result = _teams.ChangeMemberRole(_team, Manager, IdOf(Developer), TeamRole.Collaborator);

        Assert.Equal(TeamMemberChangeOutcome.Forbidden, result.Outcome);
        Assert.Equal(TeamAccessDecision.RoleRefusal(TeamRole.Manager, TeamPermissions.Row(TeamAction.MakeManagersAndChangeRoles)), result.Refusal);
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(_team, Developer));
    }

    [Theory]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void ChangeMemberRole_DeveloperOrCollaborator_IsRefused(string caller)
    {
        var result = _teams.ChangeMemberRole(_team, caller, IdOf(Manager), TeamRole.Developer);

        Assert.Equal(TeamMemberChangeOutcome.Forbidden, result.Outcome);
        Assert.Equal(TeamRole.Manager, _teams.RoleOf(_team, Manager));
    }

    [Fact]
    public void ChangeMemberRole_NotAMember_IsNotFound()
    {
        Assert.Equal(TeamMemberChangeOutcome.NotFound, _teams.ChangeMemberRole(_team, Stranger, IdOf(Developer), TeamRole.Manager).Outcome);
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(_team, Developer));
    }

    [Fact]
    public void ChangeMemberRole_TheOwnersRoleOrMakingAnOwner_IsRefusedByTheTeamsOwnRules()
    {
        var demote = _teams.ChangeMemberRole(_team, Owner, IdOf(Owner), TeamRole.Manager);
        Assert.Equal(TeamMemberChangeOutcome.Refused, demote.Outcome);
        Assert.Equal(TeamRefusals.DemoteOwner, demote.Refusal);

        var promote = _teams.ChangeMemberRole(_team, Owner, IdOf(Developer), TeamRole.Owner);
        Assert.Equal(TeamMemberChangeOutcome.Refused, promote.Outcome);
        Assert.Equal(TeamRefusals.SecondOwner, promote.Refusal);
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(_team, Developer));
    }

    [Fact]
    public void ChangeMemberRole_AnIdThatIsNoMemberOfThisTeam_IsNotFound()
    {
        var other = _teams.CreateTeam(Developer, "Other").Team!.TeamId;

        Assert.Equal(TeamMemberChangeOutcome.NotFound, _teams.ChangeMemberRole(_team, Owner, "nobody", TeamRole.Manager).Outcome);
        // A member id from another team names nobody here, even for the same person.
        Assert.Equal(TeamMemberChangeOutcome.NotFound,
            _teams.ChangeMemberRole(_team, Owner, TeamMemberIds.For(other, Developer), TeamRole.Manager).Outcome);
    }

    // ---- Remove ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Manager)]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void RemoveTeamMember_Owner_RemovesAnyoneButThemselves(string target)
    {
        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.RemoveTeamMember(_team, Owner, IdOf(target)).Outcome);
        Assert.Null(_teams.RoleOf(_team, target));
    }

    [Fact]
    public void RemoveTeamMember_TheOwnerRemovingThemselves_IsRefused()
    {
        var result = _teams.RemoveTeamMember(_team, Owner, IdOf(Owner));

        Assert.Equal(TeamMemberChangeOutcome.Refused, result.Outcome);
        Assert.Equal(TeamRefusals.OwnerRemovesSelf, result.Refusal);
        Assert.Equal(TeamRole.Owner, _teams.RoleOf(_team, Owner));
    }

    [Theory]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void RemoveTeamMember_Manager_RemovesADeveloperOrACollaborator(string target)
    {
        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.RemoveTeamMember(_team, Manager, IdOf(target)).Outcome);
        Assert.Null(_teams.RoleOf(_team, target));
    }

    [Fact]
    public void RemoveTeamMember_ManagerRemovingAManagerOrTheOwner_IsRefused()
    {
        var manager = _teams.RemoveTeamMember(_team, Manager, IdOf(Manager2));
        Assert.Equal(TeamMemberChangeOutcome.Forbidden, manager.Outcome);
        Assert.Equal(TeamRefusals.OnlyOwnerRemovesManager, manager.Refusal);

        var self = _teams.RemoveTeamMember(_team, Manager, IdOf(Manager));
        Assert.Equal(TeamRefusals.OnlyOwnerRemovesManager, self.Refusal);

        var owner = _teams.RemoveTeamMember(_team, Manager, IdOf(Owner));
        Assert.Equal(TeamMemberChangeOutcome.Refused, owner.Outcome);
        Assert.Equal(TeamRefusals.RemoveOwner, owner.Refusal);

        Assert.Equal(TeamRole.Manager, _teams.RoleOf(_team, Manager2));
        Assert.Equal(TeamRole.Owner, _teams.RoleOf(_team, Owner));
    }

    [Theory]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void RemoveTeamMember_DeveloperOrCollaborator_IsRefusedEveryRemoval(string caller)
    {
        foreach (var target in new[] { Manager, Developer, Collaborator })
        {
            var result = _teams.RemoveTeamMember(_team, caller, IdOf(target));
            Assert.Equal(TeamMemberChangeOutcome.Forbidden, result.Outcome);
            Assert.Equal(TeamAccessDecision.RoleRefusal(RoleOf(caller), TeamPermissions.Row(TeamPermissions.ActionToAddOrRemove(RoleOf(target)))),
                result.Refusal);
        }
        Assert.Equal(5, _teams.ListMembers(_team, Owner).Members.Count);
    }

    [Fact]
    public void RemoveTeamMember_NotAMember_IsNotFound()
    {
        Assert.Equal(TeamMemberChangeOutcome.NotFound, _teams.RemoveTeamMember(_team, Stranger, IdOf(Developer)).Outcome);
        Assert.Equal(TeamMemberChangeOutcome.NotFound, _teams.RemoveTeamMember(_team, Owner, "nobody").Outcome);
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(_team, Developer));
    }

    // ---- The seat count on the bill: recorded for a paid-seat change, and only then -------------------------------

    [Theory]
    [InlineData(Developer, TeamRole.Collaborator, 3)]
    [InlineData(Collaborator, TeamRole.Developer, 5)]
    [InlineData(Collaborator, TeamRole.Manager, 5)]
    [InlineData(Manager, TeamRole.Collaborator, 3)]
    public void ChangeMemberRole_APaidSeatChange_RecordsTheNewCountOnTheBill(string member, TeamRole newRole, int seats)
    {
        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.ChangeMemberRole(_team, Owner, IdOf(member), newRole).Outcome);

        Assert.Equal(seats, BilledSeats());
    }

    [Theory]
    [InlineData(Developer, TeamRole.Manager)]
    [InlineData(Manager, TeamRole.Developer)]
    [InlineData(Developer, TeamRole.Developer)]
    public void ChangeMemberRole_PaidToPaid_LeavesTheBillAlone(string member, TeamRole newRole)
    {
        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.ChangeMemberRole(_team, Owner, IdOf(member), newRole).Outcome);

        Assert.Equal(4, BilledSeats());
    }

    [Fact]
    public void RemoveTeamMember_APaidMember_LowersTheCount_ACollaboratorDoesNot()
    {
        _teams.RemoveTeamMember(_team, Owner, IdOf(Collaborator));
        Assert.Equal(4, BilledSeats());

        _teams.RemoveTeamMember(_team, Owner, IdOf(Developer));
        Assert.Equal(3, BilledSeats());
    }

    [Fact]
    public void RefusedChanges_NeverTouchTheBill()
    {
        _teams.ChangeMemberRole(_team, Manager, IdOf(Developer), TeamRole.Collaborator);
        _teams.RemoveTeamMember(_team, Developer, IdOf(Collaborator));
        _teams.RemoveTeamMember(_team, Owner, IdOf(Owner));

        Assert.Equal(4, BilledSeats());
    }

    // ---- The routes' answers, as a client receives them -----------------------------------------------------------

    [Fact]
    public async Task TeamPage_Route_CarriesEveryVerdictTheCockpitRenders()
    {
        var (status, body) = await RenderAsync(TeamEndpoints.TeamPage(_teams, Owner, _team));

        Assert.Equal(200, status);
        Assert.Equal("Acme", body.GetProperty("teamName").GetString());
        Assert.Equal("Owner", body.GetProperty("yourRole").GetString());
        Assert.True(body.GetProperty("canInvite").GetBoolean());
        var rob = body.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("email").GetString() == "rob@acme.example");
        Assert.Equal(IdOf(Developer), rob.GetProperty("memberId").GetString());
        Assert.Equal("Paid", rob.GetProperty("seat").GetString());
        Assert.True(rob.GetProperty("canChangeRole").GetBoolean());
        Assert.Equal(3, rob.GetProperty("roleChoices").GetArrayLength());
        Assert.True(rob.GetProperty("canRemove").GetBoolean());
        // The account subject is the key and is never sent.
        Assert.DoesNotContain(Developer, body.GetRawText());
    }

    [Fact]
    public async Task TeamPage_Route_CollaboratorIs403_StrangerIs404()
    {
        var (collaborator, refusal) = await RenderAsync(TeamEndpoints.TeamPage(_teams, Collaborator, _team));
        Assert.Equal(403, collaborator);
        Assert.Contains("Collaborator", refusal.GetProperty("error").GetString());

        var (stranger, _) = await RenderAsync(TeamEndpoints.TeamPage(_teams, Stranger, _team));
        Assert.Equal(404, stranger);
    }

    [Fact]
    public async Task ChangeRole_Route_BadRoleIs400_ManagerIs403_OwnerIs200AndTheServerHoldsIt()
    {
        var (bad, _) = await RenderAsync(TeamEndpoints.ChangeRole(_teams, Owner, _team, IdOf(Developer), new TeamEndpoints.ChangeRoleRequest("Boss")));
        Assert.Equal(400, bad);

        var (manager, _) = await RenderAsync(TeamEndpoints.ChangeRole(_teams, Manager, _team, IdOf(Developer), new TeamEndpoints.ChangeRoleRequest("Collaborator")));
        Assert.Equal(403, manager);

        var (owner, _) = await RenderAsync(TeamEndpoints.ChangeRole(_teams, Owner, _team, IdOf(Developer), new TeamEndpoints.ChangeRoleRequest("collaborator")));
        Assert.Equal(200, owner);
        Assert.Equal(TeamRole.Collaborator, _teams.RoleOf(_team, Developer));
    }

    [Fact]
    public async Task RemoveMember_Route_OwnerRemovingThemselvesIs409_ManagerRemovingAManagerIs403()
    {
        var (self, body) = await RenderAsync(TeamEndpoints.RemoveMember(_teams, Owner, _team, IdOf(Owner)));
        Assert.Equal(409, self);
        Assert.Equal(TeamRefusals.OwnerRemovesSelf, body.GetProperty("error").GetString());

        var (manager, _) = await RenderAsync(TeamEndpoints.RemoveMember(_teams, Manager, _team, IdOf(Manager2)));
        Assert.Equal(403, manager);

        var (ok, _) = await RenderAsync(TeamEndpoints.RemoveMember(_teams, Manager, _team, IdOf(Developer)));
        Assert.Equal(200, ok);
    }

    // ---- The pieces ------------------------------------------------------------------------------------------------

    [Fact]
    public void TeamMemberIds_For_IsStablePerTeam_DiffersAcrossTeams_AndHidesTheSubject()
    {
        var id = TeamMemberIds.For("team-a", "sub-x");

        Assert.Equal(id, TeamMemberIds.For("team-a", "sub-x"));
        Assert.NotEqual(id, TeamMemberIds.For("team-b", "sub-x"));
        Assert.NotEqual(id, TeamMemberIds.For("team-a", "sub-y"));
        Assert.DoesNotContain("sub-x", id);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }

    [Theory]
    [InlineData("", "sub-x")]
    [InlineData("team-a", " ")]
    public void TeamMemberIds_For_ABlankPart_Throws(string team, string subject)
    {
        Assert.Throws<ArgumentException>(() => TeamMemberIds.For(team, subject));
    }

    [Theory]
    [InlineData(1, 0, null, "1 paid seat, 0 Collaborators (no charge)")]
    [InlineData(3, 2, 1, "3 paid seats, 2 Collaborators (no charge), 1 invitation waiting")]
    [InlineData(2, 1, 0, "2 paid seats, 1 Collaborator (no charge), 0 invitations waiting")]
    public void Summary_CountsInWords(int paid, int free, int? waiting, string expected)
    {
        Assert.Equal(expected, TeamRegistry.Summary(paid, free, waiting));
    }

    [Fact]
    public void Rules_TheTeamPageRoutesAreDeclared()
    {
        Assert.Equal(TeamAction.SeeTeamPage, TeamEndpointRules.Find("GET", "/teams/{teamId}/page")!.Action);
        Assert.Equal(TeamAction.MakeManagersAndChangeRoles, TeamEndpointRules.Find("PUT", "/teams/{teamId}/members/{memberId}/role")!.Action);
        Assert.Equal(TeamAction.InviteOrRemoveDevelopersAndCollaborators, TeamEndpointRules.Find("DELETE", "/teams/{teamId}/members/{memberId}")!.Action);
        Assert.Equal(TeamFrom.RouteTeamId, TeamEndpointRules.Find("DELETE", "/teams/{teamId}/members/{memberId}")!.TeamFrom);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private TeamPage PageFor(string caller)
    {
        var result = _teams.DescribeTeamPage(_team, caller);
        Assert.Equal(TeamPageOutcome.Found, result.Outcome);
        return result.Page!;
    }

    private TeamPageMember Row(TeamPage page, string subject) =>
        page.Members.Single(m => m.MemberId == IdOf(subject));

    private string IdOf(string subject) => TeamMemberIds.For(_team, subject);

    private TeamRole RoleOf(string subject) => _teams.RoleOf(_team, subject)!.Value;

    private int BilledSeats()
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.TeamBills.AsNoTracking().Single(b => b.TeamId == _team).Seats;
    }

    private static async Task<(int Status, JsonElement Body)> RenderAsync(IResult result)
    {
        var provider = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = provider };
        using var ms = new MemoryStream();
        ctx.Response.Body = ms;
        await result.ExecuteAsync(ctx);
        ms.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ms);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }
}
