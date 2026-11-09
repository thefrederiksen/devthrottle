using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// TEAMS COME WITH THE PRO ACCOUNT; CONTRIBUTORS ARE FREE SEATS (Team implementation piece 1.6,
/// devthrottle_internal#2790; owner decisions of 7 and 8 November 2026). The code still calls a contributor a
/// Collaborator until piece 1.7.
///
/// Everything runs over a real migrated database and the REAL plan read - <see cref="EntitlementRegistry"/> with live
/// money required and the trial ledger wired, exactly as the hosted Gateway builds it - with a fixed clock, no port and
/// no sleep. No team bill is ever written unless a test says so.
///
/// HOW THESE FAIL ON PURPOSE: put the bill gate back in front of a Collaborator and the no-bill rows go red; drop the
/// Owner's plan check and the not-Pro rows go red; count a Collaborator as a paid seat and the seat rows go red; drop
/// the bill gate for Manager and Developer and their rows go red; fold an unreadable plan into "not Pro" and the
/// Unavailable rows go red.
/// </summary>
public sealed class TeamsComeWithProTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FarFuture = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string ProOwner = "sub-pro-owner";
    private const string ProOwnerEmail = "pro-owner@acme.example";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly TeamRegistry _teams;

    public TeamsComeWithProTests()
    {
        _db = _harness.Open();
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS entitlements (" +
                "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
                "livemode INTEGER NULL, tier TEXT NULL)");
        }
        _tenants = new TenantRegistry(_db);
        var entitlements = new EntitlementRegistry(_db, requireLivemode: true, trials: new TrialRegistry(_db));
        _teams = new TeamRegistry(_db, _tenants, () => Now, ownerPlan: TeamOwnerPlan.Hosted(entitlements));

        Account(ProOwner, ProOwnerEmail);
        Plan(ProOwner, EntitlementRegistry.TierPro);
    }

    public void Dispose() => _harness.Dispose();

    // ================================================================= creating a team

    [Fact]
    public void CreateTeam_ProOwnerWithNoTeamBill_IsCreated()
    {
        var result = _teams.CreateTeam(ProOwner, "Erik Laursen");

        Assert.Null(result.Refusal);
        Assert.Equal(TeamRole.Owner, result.Team!.Role);
        Assert.False(HasTeamBill(result.Team.TeamId));
    }

    [Theory]
    [InlineData(EntitlementRegistry.TierPro, EntitlementRegistry.StatusActive, 30)]
    [InlineData(EntitlementRegistry.TierPro, EntitlementRegistry.StatusPastDue, 3)]
    [InlineData(EntitlementRegistry.TierHosted, EntitlementRegistry.StatusActive, 30)]
    [InlineData(null, EntitlementRegistry.StatusActive, 30)]
    public void CreateTeam_EveryPlanThatCarriesTheProScopes_IsCreated(string? tier, string status, int daysLeft)
    {
        Account("sub-x", "x@acme.example");
        Plan("sub-x", tier, status, Now.AddDays(daysLeft));

        Assert.NotNull(_teams.CreateTeam("sub-x", "Team").Team);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("remote")]
    [InlineData("pro-canceled")]
    [InlineData("pro-past-due-grace-ended")]
    [InlineData("pro-selfhost")]
    [InlineData("pro-test-mode")]
    public void CreateTeam_OwnerWithoutPro_IsRefusedWithAPlainSentence_AndNoTeamExists(string kind)
    {
        var subject = "sub-" + kind;
        Account(subject, kind + "@acme.example");
        switch (kind)
        {
            case "free": break;
            case "remote": Plan(subject, EntitlementRegistry.TierRemote); break;
            case "pro-canceled": Plan(subject, EntitlementRegistry.TierPro, EntitlementRegistry.StatusCanceled); break;
            case "pro-past-due-grace-ended": Plan(subject, EntitlementRegistry.TierPro, EntitlementRegistry.StatusPastDue, Now.AddDays(-1)); break;
            case "pro-selfhost": Plan(subject, EntitlementRegistry.TierProSelfHost); break;
            case "pro-test-mode": Plan(subject, EntitlementRegistry.TierPro, livemode: false); break;
        }

        var result = _teams.CreateTeam(subject, "Team");

        Assert.Null(result.Team);
        Assert.False(result.IsUnavailable);
        Assert.Equal(TeamRefusals.CreateNeedsPro, result.Refusal);
        Assert.Equal(0, CountTeams());
    }

    [Fact]
    public void CreateTeam_PlanUnreadable_IsUnavailableNotRefused_AndNoTeamExists()
    {
        PlanTableGone();

        var result = _teams.CreateTeam(ProOwner, "Team");

        Assert.Null(result.Team);
        Assert.True(result.IsUnavailable);
        Assert.Equal(TeamRefusals.CreatePlanUnreadable, result.Refusal);
        Assert.Equal(0, CountTeams());
    }

    [Fact]
    public void CreateTeamEndpoint_PlanUnreadable_Answers503_NotPro_Answers400()
    {
        Account("sub-free", "free@acme.example");
        var notPro = TeamEndpoints.CreateTeam(_teams, "sub-free", new TeamEndpoints.CreateTeamRequest("Team"));
        PlanTableGone();
        var unreadable = TeamEndpoints.CreateTeam(_teams, ProOwner, new TeamEndpoints.CreateTeamRequest("Team"));

        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)notPro).StatusCode);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ((IStatusCodeHttpResult)unreadable).StatusCode);
    }

    // ================================================================= adding a contributor with no team bill

    [Fact]
    public void InviteAndAccept_ProOwnerNoTeamBill_ContributorJoins()
    {
        var team = NewTeam();

        var sent = _teams.CreateInvitation(team, ProOwner, "anna@client.example", TeamRole.Collaborator);
        Assert.Equal(TeamInvitationOutcome.Done, sent.Outcome);
        Assert.False(sent.AddedAtOnce);
        // Anna signs up after the invitation was sent, then accepts it.
        Account("sub-anna", "anna@client.example");
        var accepted = _teams.AcceptInvitation(sent.AcceptToken!, "sub-anna");

        Assert.Equal(TeamInvitationOutcome.Done, accepted.Outcome);
        Assert.Equal(TeamRole.Collaborator, _teams.RoleOf(team, "sub-anna"));
        Assert.False(HasTeamBill(team));
    }

    [Fact]
    public void CreateInvitation_ProOwnerNoTeamBill_ExistingAccountIsAddedAtOnce()
    {
        var team = NewTeam();
        Account("sub-ben", "ben@client.example");

        var result = _teams.CreateInvitation(team, ProOwner, "ben@client.example", TeamRole.Collaborator);

        Assert.Equal(TeamInvitationOutcome.Done, result.Outcome);
        Assert.True(result.AddedAtOnce);
        Assert.Equal(TeamRole.Collaborator, _teams.RoleOf(team, "sub-ben"));
        Assert.False(HasTeamBill(team));
    }

    [Fact]
    public void ResendInvitation_ProOwnerNoTeamBill_IsSentAgain()
    {
        var team = NewTeam();
        var sent = _teams.CreateInvitation(team, ProOwner, "cara@client.example", TeamRole.Collaborator);

        var resent = _teams.ResendInvitation(team, sent.Invitation!.Id, ProOwner);

        Assert.Equal(TeamInvitationOutcome.Done, resent.Outcome);
    }

    [Fact]
    public void InviteOptions_ProOwnerNoTeamBill_CollaboratorCanBeInvited_FormIsNotBlocked()
    {
        var team = NewTeam();

        var options = _teams.InviteOptions(team, ProOwner)!;

        Assert.Null(options.Blocked);
        Assert.True(options.Roles.Single(r => r.Role == TeamRole.Collaborator).Allowed);
    }

    // ================================================================= a contributor is a free seat

    [Fact]
    public void AddingContributors_AtOnceAndByAccept_NeverCountsAPaidSeat()
    {
        var team = NewTeam();
        Account("sub-ben", "ben@client.example");
        _teams.CreateInvitation(team, ProOwner, "ben@client.example", TeamRole.Collaborator);
        var sent = _teams.CreateInvitation(team, ProOwner, "anna@client.example", TeamRole.Collaborator);
        Account("sub-anna", "anna@client.example");
        _teams.AcceptInvitation(sent.AcceptToken!, "sub-anna");

        using var ctx = _db.CreateUnscopedContext();
        Assert.Equal(3, ctx.TeamMembers.Count(m => m.TeamId == team));
        Assert.Equal(1, TeamBillStore.PaidSeats(ctx, team));
        Assert.False(TeamSeatRoles.IsPaidSeat(TeamSeatRoles.Collaborator));
    }

    // ================================================================= the Owner is not on Pro

    [Fact]
    public void CreateInvitation_OwnerNoLongerOnPro_IsRefusedWithAPlainSentence_AndNobodyIsAdded()
    {
        var team = NewTeam();
        Plan(ProOwner, EntitlementRegistry.TierPro, EntitlementRegistry.StatusCanceled);
        Account("sub-ben", "ben@client.example");

        var result = _teams.CreateInvitation(team, ProOwner, "ben@client.example", TeamRole.Collaborator);

        Assert.Equal(TeamInvitationOutcome.Refused, result.Outcome);
        Assert.Equal(TeamInvitationRefusals.OwnerNotPro, result.Refusal);
        Assert.Null(_teams.RoleOf(team, "sub-ben"));
    }

    [Fact]
    public void AcceptInvitation_OwnerLeftProAfterSending_IsRefusedWithAPlainSentence()
    {
        var team = NewTeam();
        var sent = _teams.CreateInvitation(team, ProOwner, "anna@client.example", TeamRole.Collaborator);
        Account("sub-anna", "anna@client.example");
        Plan(ProOwner, EntitlementRegistry.TierFree);

        var accepted = _teams.AcceptInvitation(sent.AcceptToken!, "sub-anna");

        Assert.Equal(TeamInvitationOutcome.Refused, accepted.Outcome);
        Assert.Equal(TeamInvitationRefusals.OwnerNotProOnAccept, accepted.Refusal);
        Assert.Null(_teams.RoleOf(team, "sub-anna"));
    }

    [Fact]
    public void ResendInvitation_OwnerNoLongerOnPro_IsRefused()
    {
        var team = NewTeam();
        var sent = _teams.CreateInvitation(team, ProOwner, "cara@client.example", TeamRole.Collaborator);
        Plan(ProOwner, EntitlementRegistry.TierRemote);

        var resent = _teams.ResendInvitation(team, sent.Invitation!.Id, ProOwner);

        Assert.Equal(TeamInvitationRefusals.OwnerNotPro, resent.Refusal);
    }

    [Fact]
    public void InviteOptions_OwnerNoLongerOnPro_BlocksTheFormWithThePlanReason()
    {
        var team = NewTeam();
        Plan(ProOwner, EntitlementRegistry.TierPro, EntitlementRegistry.StatusCanceled);

        Assert.Equal(TeamInvitationRefusals.OwnerNotPro, _teams.InviteOptions(team, ProOwner)!.Blocked);
    }

    [Fact]
    public void CreateInvitation_OwnerPlanUnreadable_IsUnavailable_NotRefused()
    {
        var team = NewTeam();
        PlanTableGone();

        var result = _teams.CreateInvitation(team, ProOwner, "dan@client.example", TeamRole.Collaborator);

        Assert.Equal(TeamInvitationOutcome.Unavailable, result.Outcome);
        Assert.Equal(TeamInvitationRefusals.OwnerPlanUnreadable, result.Refusal);
    }

    // ================================================================= Manager and Developer still meet the bill gate

    [Theory]
    [InlineData(TeamRole.Manager)]
    [InlineData(TeamRole.Developer)]
    public void CreateInvitation_PaidSeatProOwnerNoTeamBill_IsStillRefusedByTheBillGate(TeamRole role)
    {
        var team = NewTeam();

        var result = _teams.CreateInvitation(team, ProOwner, "paid@acme.example", role);

        Assert.Equal(TeamInvitationOutcome.Refused, result.Outcome);
        Assert.Equal(TeamInvitationRefusals.BillNotStarted, result.Refusal);
    }

    [Theory]
    [InlineData(TeamRole.Manager)]
    [InlineData(TeamRole.Developer)]
    public void CreateInvitation_PaidSeatProOwnerRunningTeamBill_IsSent(TeamRole role)
    {
        var team = NewTeam();
        TeamBillSeed.Active(_db, team, seats: 1);

        Assert.Equal(TeamInvitationOutcome.Done, _teams.CreateInvitation(team, ProOwner, "paid@acme.example", role).Outcome);
    }

    [Fact]
    public void CreateInvitation_PaidSeatRunningTeamBillButOwnerNotPro_IsRefusedForThePlan()
    {
        var team = NewTeam();
        TeamBillSeed.Active(_db, team, seats: 1);
        Plan(ProOwner, EntitlementRegistry.TierFree);

        Assert.Equal(TeamInvitationRefusals.OwnerNotPro,
            _teams.CreateInvitation(team, ProOwner, "paid@acme.example", TeamRole.Developer).Refusal);
    }

    // ================================================================= the scope table

    [Fact]
    public void EntitlementScopes_Teams_IsCarriedByProTeamHostedAndTheLegacyRow_AndNothingElse()
    {
        Assert.True(EntitlementScopes.Grants(EntitlementRegistry.TierPro, EntitlementScopes.Teams));
        Assert.True(EntitlementScopes.Grants(EntitlementRegistry.TierTeam, EntitlementScopes.Teams));
        Assert.True(EntitlementScopes.Grants(EntitlementRegistry.TierHosted, EntitlementScopes.Teams));
        Assert.True(EntitlementScopes.Grants(null, EntitlementScopes.Teams));
        Assert.False(EntitlementScopes.Grants(EntitlementRegistry.TierRemote, EntitlementScopes.Teams));
        Assert.False(EntitlementScopes.Grants(EntitlementRegistry.TierFree, EntitlementScopes.Teams));
        Assert.False(EntitlementScopes.Grants(EntitlementRegistry.TierProSelfHost, EntitlementScopes.Teams));
        Assert.False(EntitlementScopes.Grants("enterprise-gold", EntitlementScopes.Teams));
    }

    [Fact]
    public void TeamOwnerPlan_SelfHost_GrantsEveryOwner_WithoutReadingAPlan()
    {
        Assert.False(TeamOwnerPlan.SelfHost.IsGating);
        Assert.Equal(TeamOwnerPlanAnswer.Granted, TeamOwnerPlan.SelfHost.Read("sub-anyone", Now));
    }

    // ================================================================= helpers

    private void Account(string subject, string email) => _tenants.MintOrLookupBySubject(subject, email);

    /// <summary>Puts (or replaces) the account's own paid row.</summary>
    private void Plan(string subject, string? tier, string status = EntitlementRegistry.StatusActive, DateTime? periodEnd = null,
        bool livemode = true)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("DELETE FROM entitlements WHERE subject = {0}", subject);
        if (tier == EntitlementRegistry.TierFree)
            return;
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
            subject, status, periodEnd ?? FarFuture, livemode, tier);
    }

    /// <summary>The plan read fails from here on, as when the database cannot answer.</summary>
    private void PlanTableGone()
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("DROP TABLE entitlements");
    }

    private string NewTeam() => _teams.CreateTeam(ProOwner, "Acme").Team!.TeamId;

    private bool HasTeamBill(string team)
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.TeamBills.Any(b => b.TeamId == team);
    }

    private int CountTeams()
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.Teams.Count();
    }
}
