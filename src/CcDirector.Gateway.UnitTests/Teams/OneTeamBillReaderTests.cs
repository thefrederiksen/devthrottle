using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Account;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// DECISION D8: ONE reader of "has this team a running bill" (Phase 1 review, finding 2; devthrottle_internal#2311
/// Gateway step 2). Paid features, inviting, resending, accepting and the seat convergence all read the team's bill
/// through <see cref="EntitlementRegistry.ReadTeamBill"/>, so on a hosted Gateway - which requires live money - a
/// test-mode row is no bill for EVERY one of them. Before this, paid features refused a test-mode row while the invite,
/// resend, accept and convergence reader never looked at <c>livemode</c>, so a test-mode subscription could open
/// inviting on production. Each case runs a live row and a test-mode row through every use.
/// </summary>
public sealed class OneTeamBillReaderTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Developer = "sub-developer";
    private const string Newcomer = "sub-newcomer";
    private const string Token = "service-token";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly EntitlementRegistry _entitlements;
    private readonly CountingWebsite _website = new();
    private readonly TeamSeatSync _seatSync;
    private readonly TeamRegistry _teams;
    private readonly string _team;

    public OneTeamBillReaderTests()
    {
        _db = _harness.Open();
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS team_entitlements (" +
                "team_id TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, seats INTEGER NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, livemode INTEGER NULL, updated_at TEXT NULL)");
        _tenants = new TenantRegistry(_db);
        // As the hosted Gateway builds it: live money required, and the registry's bill gate reading the SAME reader.
        _entitlements = new EntitlementRegistry(_db, requireLivemode: true);
        _seatSync = new TeamSeatSync(_entitlements, new TeamSeatSyncClient(new HttpClient(_website), "https://website.test"), () => Token);
        _teams = new TeamRegistry(_db, _tenants, seatSync: _seatSync, readTeamBill: _entitlements.ReadTeamBill);

        _tenants.MintOrLookupBySubject(Owner, "owner@acme.example");
        _tenants.MintOrLookupBySubject(Newcomer, "newcomer@acme.example");
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        Assert.True(_teams.AddMember(_team, Developer, TeamRole.Developer).IsDone);
        _teams.SeatSyncsSettled().GetAwaiter().GetResult();
    }

    public void Dispose() => _harness.Dispose();

    private sealed class CountingWebsite : HttpMessageHandler
    {
        public List<string> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"seats\":2}}") });
        }
    }

    private void SetBill(bool live)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("DELETE FROM team_entitlements WHERE team_id = {0}", _team);
        // One billed seat while the team has two paid members, so a convergence that counts this bill calls the sync.
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO team_entitlements (team_id, status, seats, livemode) VALUES ({0}, 'active', 1, {1})", _team, live);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EveryUseOfTheBill_ReadsTheOneReader_AndOnAHostedGateway_ATestModeRowIsNoBillForAllOfThem(bool live)
    {
        // Two invitations sent while the bill is live, so resend and accept have something to act on in both cases.
        SetBill(live: true);
        var toResend = _teams.CreateInvitation(_team, Owner, "resend@acme.example", TeamRole.Developer);
        var toAccept = _teams.CreateInvitation(_team, Owner, "newcomer@acme.example", TeamRole.Developer);
        Assert.Equal(TeamInvitationOutcome.Done, toResend.Outcome);
        Assert.Equal(TeamInvitationOutcome.Done, toAccept.Outcome);
        SetBill(live);
        _website.Calls.Clear();

        var bill = _entitlements.ReadTeamBill(_team);
        var paidFeatures = _entitlements.EvaluateTeam(_team, DateTime.UtcNow);
        var invite = _teams.CreateInvitation(_team, Owner, "another@acme.example", TeamRole.Developer);
        var resend = _teams.ResendInvitation(_team, toResend.Invitation!.Id, Owner);
        var accept = _teams.AcceptInvitation(toAccept.AcceptToken!, Newcomer);
        var (convergence, _) = await _seatSync.ConvergeAsync(_team, gatewayPaidMembers: 2);

        Assert.True(bill.Known);
        Assert.Equal(live, bill.HasBill);
        if (live)
        {
            Assert.Equal(EntitlementOutcome.Entitled, paidFeatures.Outcome);
            Assert.Equal(EntitlementRegistry.TierTeam, paidFeatures.Tier);
            Assert.Equal(TeamInvitationOutcome.Done, invite.Outcome);
            Assert.Equal(TeamInvitationOutcome.Done, resend.Outcome);
            Assert.Equal(TeamInvitationOutcome.Done, accept.Outcome);
            Assert.Equal(SeatSyncVerdict.CallSync, convergence);
        }
        else
        {
            Assert.Equal(EntitlementOutcome.NotEntitled, paidFeatures.Outcome);
            Assert.Equal(TeamInvitationRefusals.BillNotStarted, invite.Refusal);
            Assert.Equal(TeamInvitationRefusals.BillNotStarted, resend.Refusal);
            Assert.Equal(TeamInvitationRefusals.BillStopped, accept.Refusal);
            Assert.Null(_teams.RoleOf(_team, Newcomer));
            Assert.Equal(SeatSyncVerdict.NoBill, convergence);
            Assert.Empty(_website.Calls);
        }
    }

    [Fact]
    public void ReadTeamBill_OnASelfHostGateway_CountsATestModeRow_AsBefore()
    {
        // Live money is required only where the Gateway requires it (the hosted one); a self-host Gateway reads the row.
        SetBill(live: false);
        var selfHost = new EntitlementRegistry(_db, requireLivemode: false);

        Assert.True(selfHost.ReadTeamBill(_team).HasBill);
        Assert.Equal(EntitlementOutcome.Entitled, selfHost.EvaluateTeam(_team, DateTime.UtcNow).Outcome);
    }

    [Fact]
    public void ReadTeamBill_AFailedRead_IsNotKnown_AndPaidFeaturesAreUnknown()
    {
        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("DROP TABLE team_entitlements");

        Assert.False(_entitlements.ReadTeamBill(_team).Known);
        Assert.Equal(EntitlementOutcome.Unknown, _entitlements.EvaluateTeam(_team, DateTime.UtcNow).Outcome);
        Assert.Throws<ArgumentException>(() => _entitlements.ReadTeamBill(" "));
    }
}
