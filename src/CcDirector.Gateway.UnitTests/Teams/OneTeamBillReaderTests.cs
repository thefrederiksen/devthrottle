using System;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// ONE READER, ONE SOURCE (decision D8; Teams v1, the team bill without Stripe). Paid features, inviting, resending and
/// accepting all read the team's bill through <see cref="EntitlementRegistry.ReadTeamBill"/>, and that reader reads the
/// Gateway's own <c>team_bills</c> table and nothing else: a row in the website's <c>team_entitlements</c> is no bill, and
/// the live-money rule that guards the website's rows does not apply to the Gateway's own. Each case runs every use of the
/// bill through the hosted shape of the registry (live money required for PERSONAL rows).
/// </summary>
public sealed class OneTeamBillReaderTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Developer = "sub-developer";
    private const string Newcomer = "sub-newcomer";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly EntitlementRegistry _entitlements;
    private readonly TeamRegistry _teams;
    private readonly string _team;

    public OneTeamBillReaderTests()
    {
        _db = _harness.Open();
        var tenants = new TenantRegistry(_db);
        // As the hosted Gateway builds it: live money required, and the registry's bill gate reading the SAME reader.
        _entitlements = new EntitlementRegistry(_db, requireLivemode: true);
        _teams = new TeamRegistry(_db, tenants, readTeamBill: _entitlements.ReadTeamBill);

        tenants.MintOrLookupBySubject(Owner, "owner@acme.example");
        tenants.MintOrLookupBySubject(Newcomer, "newcomer@acme.example");
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
    }

    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryUseOfTheBill_ReadsTheGatewaysOwnBill_ActiveGrants_EndedRefuses(bool active)
    {
        // Two invitations sent while the bill runs, so resend and accept have something to act on in both cases.
        TeamBillSeed.Active(_db, _team, seats: 2);
        var toResend = _teams.CreateInvitation(_team, Owner, "resend@acme.example", TeamRole.Developer);
        var toAccept = _teams.CreateInvitation(_team, Owner, "newcomer@acme.example", TeamRole.Developer);
        Assert.Equal(TeamInvitationOutcome.Done, toResend.Outcome);
        Assert.Equal(TeamInvitationOutcome.Done, toAccept.Outcome);
        if (!active)
            TeamBillSeed.Canceled(_db, _team, seats: 2);

        var bill = _entitlements.ReadTeamBill(_team);
        var paidFeatures = _entitlements.EvaluateTeam(_team, DateTime.UtcNow);
        var invite = _teams.CreateInvitation(_team, Owner, "another@acme.example", TeamRole.Developer);
        var resend = _teams.ResendInvitation(_team, toResend.Invitation!.Id, Owner);
        var accept = _teams.AcceptInvitation(toAccept.AcceptToken!, Newcomer);

        Assert.True(bill.Known);
        Assert.True(bill.HasBill);
        if (active)
        {
            Assert.Equal(EntitlementOutcome.Entitled, paidFeatures.Outcome);
            Assert.Equal(EntitlementRegistry.TierTeam, paidFeatures.Tier);
            Assert.Equal(TeamInvitationOutcome.Done, invite.Outcome);
            Assert.Equal(TeamInvitationOutcome.Done, resend.Outcome);
            Assert.Equal(TeamInvitationOutcome.Done, accept.Outcome);
            Assert.Equal(TeamRole.Developer, _teams.RoleOf(_team, Newcomer));
        }
        else
        {
            Assert.Equal(EntitlementOutcome.NotEntitled, paidFeatures.Outcome);
            Assert.Equal(TeamInvitationRefusals.BillCancelled, invite.Refusal);
            Assert.Equal(TeamInvitationRefusals.BillCancelled, resend.Refusal);
            Assert.Equal(TeamInvitationRefusals.BillStopped, accept.Refusal);
            Assert.Null(_teams.RoleOf(_team, Newcomer));
        }
    }

    [Fact]
    public void ReadTeamBill_AWebsiteRowAlone_IsNoBill_ThereIsNoFallback()
    {
        // The website's table, holding a live, active row for this team, and no Gateway bill: the reader does not look
        // at the website's row at all.
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS team_entitlements (" +
                "team_id TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, seats INTEGER NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, livemode INTEGER NULL, updated_at TEXT NULL)");
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO team_entitlements (team_id, status, seats, livemode) VALUES ({0}, 'active', 2, 1)", _team);
        }

        var bill = _entitlements.ReadTeamBill(_team);

        Assert.True(bill.Known);
        Assert.False(bill.HasBill);
        Assert.Equal(EntitlementOutcome.NotEntitled, _entitlements.EvaluateTeam(_team, DateTime.UtcNow).Outcome);
        Assert.Equal(TeamInvitationRefusals.BillNotStarted,
            _teams.CreateInvitation(_team, Owner, "another@acme.example", TeamRole.Developer).Refusal);
    }

    [Fact]
    public void ReadTeamBill_TheGatewaysOwnBill_NeedsNoLiveMoney_OnAHostedGateway()
    {
        // The hosted registry refuses a PERSONAL row that is not live money; the Gateway's own team bill has no such
        // flag, and it grants.
        TeamBillSeed.Active(_db, _team, seats: 2);

        var bill = _entitlements.ReadTeamBill(_team);

        Assert.True(bill.HasBill);
        Assert.Equal(EntitlementRegistry.StatusActive, bill.Status);
        Assert.Equal(2, bill.Seats);
        Assert.NotNull(bill.Fingerprint);
        Assert.Equal(EntitlementOutcome.Entitled, _entitlements.EvaluateTeam(_team, DateTime.UtcNow).Outcome);
    }

    [Fact]
    public void ReadTeamBill_AFailedRead_IsNotKnown_AndPaidFeaturesAreUnknown()
    {
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE team_bills");

        Assert.False(_entitlements.ReadTeamBill(_team).Known);
        Assert.Equal(EntitlementOutcome.Unknown, _entitlements.EvaluateTeam(_team, DateTime.UtcNow).Outcome);
        Assert.Throws<ArgumentException>(() => _entitlements.ReadTeamBill(" "));
    }
}
