using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// Rename, delete and leave a team (Teams v1 - the owner's 8 October ruling, "finish the first version"), over a real,
/// throwaway, fully migrated Gateway database: each change for every role and for a stranger, the seat count leaving
/// records on the team's bill, a left member's Director refused on its very next request, and a deleted team gone from
/// every read while nothing of it is erased. The clock is injected; nothing sleeps.
/// </summary>
public sealed class TeamManageTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string Stranger = "sub-stranger";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;
    private readonly DeviceRegistry _devices;
    private readonly List<TeamMembershipCommitted> _committed = new();
    private readonly DateTime _now = new(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _team;

    public TeamManageTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _teams = new TeamRegistry(_db, _tenants, () => _now);
        _devices = new DeviceRegistry(_db, _harness.LegacyPath("devices.json"), isHosted: true, teamsReleased: true);
        _tenants.MintOrLookupBySubject(Owner, "soren@acme.example");
        _tenants.MintOrLookupBySubject(Manager, "priya@acme.example");
        _tenants.MintOrLookupBySubject(Developer, "rob@acme.example");
        _tenants.MintOrLookupBySubject(Collaborator, "mike@client.example");
        _tenants.MintOrLookupBySubject(Stranger, "stranger@else.example");
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Manager, TeamRole.Manager).IsDone);
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.True(_teams.AddMember(_team, Collaborator, TeamRole.Collaborator).IsDone);
        // The team's plan, at its three paid seats: the Owner, the Manager and the Developer.
        TeamBillSeed.Active(_db, _team, seats: 3);
        _teams.MembershipCommitted += c => _committed.Add(c);
    }

    public void Dispose()
    {
        _devices.Dispose();
        _harness.Dispose();
    }

    // ---- Rename ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RenameTeam_Owner_RenamesIt_AndEveryMemberSeesTheNewNameAtOnce()
    {
        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.RenameTeam(_team, Owner, "  Acme Labs  ").Outcome);

        foreach (var member in new[] { Owner, Manager, Developer, Collaborator })
            Assert.Equal("Acme Labs", _teams.ListTeamsFor(member).Single().Name);
        Assert.Equal("Acme Labs", _teams.DescribeTeamPage(_team, Owner).Page!.TeamName);
    }

    [Theory]
    [InlineData(Manager)]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void RenameTeam_AnyoneButTheOwner_IsRefusedByTheRoleTable_AndNothingChanges(string caller)
    {
        var result = _teams.RenameTeam(_team, caller, "Taken over");

        Assert.Equal(TeamMemberChangeOutcome.Forbidden, result.Outcome);
        Assert.Contains("rename or delete the team", result.Refusal);
        Assert.Equal("Acme", _teams.ListTeamsFor(Owner).Single().Name);
    }

    [Fact]
    public void RenameTeam_AStranger_IsToldThereIsNoSuchTeam()
    {
        Assert.Equal(TeamMemberChangeOutcome.NotFound, _teams.RenameTeam(_team, Stranger, "Mine now").Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Line\nbreak")]
    public void RenameTeam_ANameTheCreateRuleRefuses_IsRefusedWithThatRulesSentence(string name)
    {
        var result = _teams.RenameTeam(_team, Owner, name);

        Assert.Equal(TeamMemberChangeOutcome.Refused, result.Outcome);
        Assert.Equal(TeamRegistry.NameRefusal(name), result.Refusal);
        Assert.Equal("Acme", _teams.ListTeamsFor(Owner).Single().Name);
    }

    [Fact]
    public void RenameTeam_TooLong_IsRefused_AtTheCreateRulesLimit()
    {
        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.RenameTeam(_team, Owner, new string('a', TeamRegistry.MaxNameLength)).Outcome);
        Assert.Equal(TeamMemberChangeOutcome.Refused, _teams.RenameTeam(_team, Owner, new string('a', TeamRegistry.MaxNameLength + 1)).Outcome);
    }

    // ---- Leave ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Manager)]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void LeaveTeam_AnyMemberButTheOwner_Leaves_ThroughTheOneMembershipPath(string caller)
    {
        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.LeaveTeam(_team, caller).Outcome);

        Assert.Empty(_teams.ListTeamsFor(caller));
        Assert.Null(_teams.RoleOf(_team, caller));
        var change = Assert.Single(_committed);
        Assert.Equal(TeamMembershipChange.MemberRemoved, change.Change);
        Assert.Equal(caller, change.AccountSubject);
        // So the revoker takes the "removed from the team" path for their Directors.
        Assert.Equal(TeamMemberAccessRevoker.RemovedReason, TeamMemberAccessRevoker.ReasonToCut(change));
    }

    [Fact]
    public void LeaveTeam_TheOwner_IsRefused_AndToldToDeleteTheTeamInstead()
    {
        var result = _teams.LeaveTeam(_team, Owner);

        Assert.Equal(TeamMemberChangeOutcome.Refused, result.Outcome);
        Assert.Equal(TeamManageRefusals.OwnerCannotLeave, result.Refusal);
        Assert.Equal(TeamRole.Owner, _teams.RoleOf(_team, Owner));
        Assert.Empty(_committed);
    }

    [Fact]
    public void LeaveTeam_AStranger_IsToldThereIsNoSuchTeam()
    {
        Assert.Equal(TeamMemberChangeOutcome.NotFound, _teams.LeaveTeam(_team, Stranger).Outcome);
    }

    [Fact]
    public void LeaveTeam_APaidMember_TakesTheirSeatOffTheBill_ACollaboratorLeavesTheBillAlone()
    {
        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.LeaveTeam(_team, Collaborator).Outcome);
        Assert.Equal(3, BillSeats());

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.LeaveTeam(_team, Developer).Outcome);
        Assert.Equal(2, BillSeats());
    }

    [Fact]
    public void LeaveTeam_TheLeftMembersDirector_IsRefusedOnItsVeryNextRequest_TheirOwnAccountKeyIsNot()
    {
        var teamKey = _devices.RegisterForTenant(new TenantId(_team), Developer, _team + "|dir-rob", "M-rob").DeviceKey;
        var own = _tenants.MintOrLookupBySubject(Developer, null);
        var ownKey = _devices.RegisterForTenant(own, Developer, own.Value + "|dir-rob-own", "M-rob-own").DeviceKey;
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(teamKey).Kind);

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.LeaveTeam(_team, Developer).Outcome);

        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(teamKey).Kind);
        // Their Personal work is untouched.
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(ownKey).Kind);
    }

    // ---- Delete ---------------------------------------------------------------------------------------------------

    [Fact]
    public void DeleteTeam_WhileOthersRemain_IsRefused_WithHowMany_AndNothingChanges()
    {
        var result = _teams.DeleteTeam(_team, Owner, "Acme");

        Assert.Equal(TeamMemberChangeOutcome.Refused, result.Outcome);
        Assert.Equal(TeamManageRefusals.OthersRemain(3), result.Refusal);
        Assert.Single(_teams.ListTeamsFor(Owner));
        Assert.Empty(_committed);
    }

    [Theory]
    [InlineData(Manager)]
    [InlineData(Developer)]
    [InlineData(Collaborator)]
    public void DeleteTeam_AnyoneButTheOwner_IsRefusedByTheRoleTable(string caller)
    {
        var result = _teams.DeleteTeam(_team, caller, "Acme");

        Assert.Equal(TeamMemberChangeOutcome.Forbidden, result.Outcome);
        Assert.Contains("rename or delete the team", result.Refusal);
    }

    [Fact]
    public void DeleteTeam_AStranger_IsToldThereIsNoSuchTeam()
    {
        Assert.Equal(TeamMemberChangeOutcome.NotFound, _teams.DeleteTeam(_team, Stranger, "Acme").Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("acme")]
    [InlineData("Acme Labs")]
    public void DeleteTeam_WithoutTheNameTypedExactly_IsRefused(string? typed)
    {
        OwnerAlone();

        var result = _teams.DeleteTeam(_team, Owner, typed);

        Assert.Equal(TeamMemberChangeOutcome.Refused, result.Outcome);
        Assert.Equal(TeamManageRefusals.TypeTheName("Acme"), result.Refusal);
        Assert.Single(_teams.ListTeamsFor(Owner));
    }

    [Fact]
    public void DeleteTeam_TheLastMember_DeletesIt_AndItIsGoneFromEveryRead()
    {
        OwnerAlone();

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.DeleteTeam(_team, Owner, " Acme ").Outcome);

        Assert.Empty(_teams.ListTeamsFor(Owner));
        Assert.Equal(TeamMembersOutcome.NotFound, _teams.ListMembers(_team, Owner).Outcome);
        Assert.Equal(TeamPageOutcome.NotFound, _teams.DescribeTeamPage(_team, Owner).Outcome);
        Assert.Null(_teams.RoleOf(_team, Owner));
        Assert.Equal(TeamMemberChangeOutcome.NotFound, _teams.RenameTeam(_team, Owner, "Back").Outcome);
        Assert.Equal(TeamMemberChangeOutcome.NotFound, _teams.DeleteTeam(_team, Owner, "Acme").Outcome);
        // Nobody can be added to it, and the background sweeps no longer walk it.
        Assert.False(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        Assert.DoesNotContain(new TenantId(_team), _tenants.AllTenantIds());
    }

    [Fact]
    public void DeleteTeam_ItsTenantIsStillATeams_SoAKeyBoundToItIsRefused_NeverTakenForAPersonalAccount()
    {
        OwnerAlone();
        var ownerKey = _devices.RegisterForTenant(new TenantId(_team), Owner, _team + "|dir-soren", "M-soren").DeviceKey;
        Assert.Equal(DeviceCredentialResolutionKind.Active, _devices.ResolveCredential(ownerKey).Kind);

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.DeleteTeam(_team, Owner, "Acme").Outcome);

        Assert.True(_teams.IsTeam(new TenantId(_team)));
        Assert.True(new TeamRegistry(_db, _tenants).IsTeam(new TenantId(_team)), "a fresh registry, reading the database, says the same");
        // The Owner's own Directors on the team take the removed path.
        Assert.Equal(DeviceCredentialResolutionKind.Revoked, _devices.ResolveCredential(ownerKey).Kind);
        var change = Assert.Single(_committed, c => c.AccountSubject == Owner);
        Assert.Equal(TeamMembershipChange.MemberRemoved, change.Change);
    }

    [Fact]
    public void DeleteTeam_EndsTheBill_AndCancelsTheWaitingInvitations()
    {
        Assert.Equal(TeamInvitationOutcome.Done, _teams.CreateInvitation(_team, Owner, "new.person@example.org", TeamRole.Developer).Outcome);
        OwnerAlone();

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.DeleteTeam(_team, Owner, "Acme").Outcome);

        using var ctx = _db.CreateUnscopedContext();
        var bill = ctx.TeamBills.Single(b => b.TeamId == _team);
        Assert.Equal(EntitlementRegistry.StatusCanceled, bill.Status);
        Assert.False(bill.AutoRenew);
        Assert.All(ctx.TeamInvitations.Where(i => i.TeamId == _team).ToList(),
            i => Assert.Equal(TeamInvitationStates.Cancelled, i.State));
    }

    [Fact]
    public void DeleteTeam_ErasesNothing_TheRowIsMarked_AndNamesWhoDeletedIt_ForAHandRestore()
    {
        OwnerAlone();

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.DeleteTeam(_team, Owner, "Acme").Outcome);

        using var ctx = _db.CreateUnscopedContext();
        Assert.False(ctx.Teams.Any(t => t.Id == _team));
        var row = ctx.Teams.IgnoreQueryFilters().Single(t => t.Id == _team);
        Assert.Equal("Acme", row.Name);
        Assert.Equal(_now, row.DeletedAtUtc);
        Assert.Equal(Owner, row.DeletedByAccountSubject);
        Assert.NotNull(ctx.TeamBills.SingleOrDefault(b => b.TeamId == _team));
    }

    [Fact]
    public void DeleteTeam_ATeamThatNeverStartedItsPlan_IsDeletedToo()
    {
        var solo = _teams.CreateTeam(Developer, "Solo").Team!.TeamId;

        Assert.Equal(TeamMemberChangeOutcome.Done, _teams.DeleteTeam(solo, Developer, "Solo").Outcome);

        Assert.Equal("Acme", Assert.Single(_teams.ListTeamsFor(Developer)).Name);
    }

    // ---- What the Team page offers --------------------------------------------------------------------------------

    [Fact]
    public void DescribeTeamPage_Owner_IsOfferedRename_AndDeleteOnlyOnceAlone_WithTheSentenceWhyNot()
    {
        var crowded = _teams.DescribeTeamPage(_team, Owner).Page!.Manage;
        Assert.True(crowded.CanRename);
        Assert.False(crowded.CanDelete);
        Assert.Equal(TeamManageRefusals.OthersRemain(3), crowded.DeleteBlocked);
        Assert.Equal(TeamManageRefusals.DeleteWarning("Acme"), crowded.DeleteWarning);
        Assert.False(crowded.CanLeave);
        Assert.Null(crowded.LeaveWarning);

        OwnerAlone();
        var alone = _teams.DescribeTeamPage(_team, Owner).Page!.Manage;
        Assert.True(alone.CanDelete);
        Assert.Null(alone.DeleteBlocked);
    }

    [Theory]
    [InlineData(Manager, true)]
    [InlineData(Developer, true)]
    public void DescribeTeamPage_AMember_IsOfferedLeave_NotRenameOrDelete(string caller, bool paid)
    {
        var manage = _teams.DescribeTeamPage(_team, caller).Page!.Manage;

        Assert.False(manage.CanRename);
        Assert.False(manage.CanDelete);
        Assert.Null(manage.DeleteBlocked);
        Assert.Null(manage.DeleteWarning);
        Assert.True(manage.CanLeave);
        Assert.Equal(TeamManageRefusals.LeaveWarning("Acme", paid), manage.LeaveWarning);
        Assert.Contains("Your own sessions and your Personal work are not touched", manage.LeaveWarning);
    }

    // ---- The routes, rendered as a client receives them -------------------------------------------------------------

    [Fact]
    public async Task Routes_AnswerEachOutcomeWithItsStatus()
    {
        Assert.Equal(400, (await RenderAsync(TeamEndpoints.RenameTeam(_teams, Owner, _team, "  "))).Status);
        Assert.Equal(403, (await RenderAsync(TeamEndpoints.RenameTeam(_teams, Developer, _team, "Mine"))).Status);
        Assert.Equal(404, (await RenderAsync(TeamEndpoints.RenameTeam(_teams, Stranger, _team, "Mine"))).Status);
        Assert.Equal(200, (await RenderAsync(TeamEndpoints.RenameTeam(_teams, Owner, _team, "Acme Labs"))).Status);

        Assert.Equal(409, (await RenderAsync(TeamEndpoints.AnswerChange(_teams.LeaveTeam(_team, Owner), "leave"))).Status);
        Assert.Equal(409, (await RenderAsync(TeamEndpoints.AnswerChange(_teams.DeleteTeam(_team, Owner, "Acme Labs"), "delete"))).Status);
    }

    [Fact]
    public async Task TeamPage_CarriesTheManageVerdicts_OnTheWire()
    {
        var (status, body) = await RenderAsync(TeamEndpoints.TeamPage(_teams, Developer, _team));

        Assert.Equal(200, status);
        var manage = body.GetProperty("manage");
        Assert.False(manage.GetProperty("canRename").GetBoolean());
        Assert.False(manage.GetProperty("canDelete").GetBoolean());
        Assert.True(manage.GetProperty("canLeave").GetBoolean());
        Assert.Equal(TeamManageRefusals.LeaveWarning("Acme", paidSeat: true), manage.GetProperty("leaveWarning").GetString());
        Assert.Equal(JsonValueKind.Null, manage.GetProperty("deleteBlocked").ValueKind);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private void OwnerAlone()
    {
        foreach (var member in new[] { Manager, Developer, Collaborator })
            Assert.True(_teams.RemoveMember(_team, member).IsDone);
        _committed.Clear();
    }

    private int BillSeats()
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.TeamBills.Single(b => b.TeamId == _team).Seats;
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
