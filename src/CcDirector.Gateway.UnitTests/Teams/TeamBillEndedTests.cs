using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// A team whose bill has ENDED (Teams v1, owner ruling of 7 October): "Refuse new invitations and accepts, keep existing
/// members, tell the Owner - nobody is cut off, and nobody joins a team that does not pay."
///
/// The bill is the Gateway's own (the team bill without Stripe), and it ends here the way it ends in production: the
/// Owner starts the plan, cancels it, and the renewal pass at the period's end makes it canceled
/// (<see cref="TeamBillStore"/>). Only the statuses nothing writes any more are seeded straight into the table.
///
/// First the two halves of the ruling that were already on main, each proved by a test that fails if its check is
/// removed: the refusals (sending, resending, accepting) and the members who stay while the seat count stops being
/// recorded. Then what this change adds: the Owner is told once per ended bill - not once per refusal - and the Team page
/// tells the Owner and a Manager what the ended bill means and that the Owner renews the team plan.
///
/// Over a real, throwaway, fully migrated Gateway database; the website is a recording HTTP handler and nothing leaves
/// the process. Every address is on a reserved test domain.
/// </summary>
public sealed class TeamBillEndedTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Manager = "sub-manager";
    private const string Developer = "sub-developer";
    private const string Collaborator = "sub-collaborator";
    private const string Token = "test-gateway-service-token";

    private const string BillEndedUri = "https://website.test/api/v1/team-bill/ended-email";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly TenantRegistry _tenants;
    private readonly RecordingWebsite _website = new();
    private readonly TeamBillStore _bills;
    private readonly TeamBillEndedNotice _notice;
    private readonly TeamRegistry _teams;
    private DateTime _now = new(2026, 10, 7, 10, 0, 0, DateTimeKind.Utc);
    private readonly string _team;

    public TeamBillEndedTests()
    {
        _db = _harness.Open();
        _tenants = new TenantRegistry(_db);
        _bills = new TeamBillStore(_db, () => _now);
        _notice = new TeamBillEndedNotice(
            new TeamInvitationMailer(new TeamInvitationMailClient(new HttpClient(_website), "https://website.test"), () => Token));
        _teams = new TeamRegistry(_db, _tenants, () => _now, bills: _bills, billEndedNotice: _notice);

        _tenants.MintOrLookupBySubject(Owner, "owner@devthrottle-test.internal");
        _tenants.MintOrLookupBySubject(Manager, "manager@devthrottle-test.internal");
        _tenants.MintOrLookupBySubject(Developer, "developer@devthrottle-test.internal");
        _tenants.MintOrLookupBySubject(Collaborator, "collaborator@devthrottle-test.internal");
        _team = _teams.CreateTeam(Owner, "Acme").Team!.TeamId;
        _teams.AddMember(_team, Manager, TeamRole.Manager);
        _teams.AddMember(_team, Developer, TeamRole.Developer);
        _teams.AddMember(_team, Collaborator, TeamRole.Collaborator);
        Assert.Equal(TeamBillChangeOutcome.Done, _bills.Start(_team).Outcome);
    }

    public void Dispose() => _harness.Dispose();

    // ---- Already on main: the refusals -----------------------------------------------------------------------------

    [Fact]
    public async Task ResendInvitation_BillEndedSinceItWasSent_IsRefusedAsCancelled_AndNoNewLinkIsMinted()
    {
        AnHourBeforeThePeriodEnds();
        var sent = _teams.CreateInvitation(_team, Owner, "anna@devthrottle-test.internal", TeamRole.Developer);
        Assert.Equal(TeamInvitationOutcome.Done, sent.Outcome);
        EndBill();

        var resent = _teams.ResendInvitation(_team, sent.Invitation!.Id, Manager);

        Assert.Equal(TeamInvitationOutcome.Refused, resent.Outcome);
        Assert.Equal(TeamInvitationRefusals.BillCancelled, resent.Refusal);
        Assert.Null(resent.AcceptToken);
        // The first link still opens the invitation, which says nobody can join: nothing was renewed.
        _tenants.MintOrLookupBySubject("sub-anna", "anna@devthrottle-test.internal");
        Assert.Equal(TeamInvitationRefusals.BillStopped, _teams.OpenInvitation(sent.AcceptToken!, "sub-anna").Invitation!.Refusal);
        await _notice.Settled();
    }

    // ---- Already on main: nobody is removed, and the seat count stops being recorded --------------------------------

    [Fact]
    public void BillEnded_EveryMemberStays_AndConvergenceLeavesTheEndedBillsSeatsAsTheyWere()
    {
        EndBill();
        var seatsWhenItEnded = _bills.Find(_team)!.Seats;
        // A paid member joins after the bill ended, so the team's paid-seat count now differs from the bill's: on a
        // running bill this is exactly what convergence corrects.
        _tenants.MintOrLookupBySubject("sub-late", "late@devthrottle-test.internal");
        _teams.AddMember(_team, "sub-late", TeamRole.Developer);

        var corrected = new TeamSeatConvergence(_db, _bills).RunOnce();

        Assert.Equal(0, corrected);
        Assert.Equal(seatsWhenItEnded, _bills.Find(_team)!.Seats);
        Assert.Equal(EntitlementRegistry.StatusCanceled, _bills.Find(_team)!.Status);
        Assert.Equal(TeamRole.Owner, _teams.RoleOf(_team, Owner));
        Assert.Equal(TeamRole.Manager, _teams.RoleOf(_team, Manager));
        Assert.Equal(TeamRole.Developer, _teams.RoleOf(_team, Developer));
        Assert.Equal(TeamRole.Collaborator, _teams.RoleOf(_team, Collaborator));
    }

    [Fact]
    public void RecordSeats_AnEndedBill_IsNoBill_EvenWhenTheCountsDiffer_WhereARunningBillIsRecorded()
    {
        TeamBillSeed.Put(_db, _team, EntitlementRegistry.StatusActive, seats: 1, autoRenew: true);
        Assert.Equal(SeatRecordOutcome.Recorded, _bills.RecordSeats(_team));

        TeamBillSeed.Canceled(_db, _team, seats: 1);

        Assert.Equal(SeatRecordOutcome.NoBill, _bills.RecordSeats(_team));
        Assert.Equal(1, _bills.Find(_team)!.Seats);
    }

    // ---- New: the Owner is told, once per ended bill ----------------------------------------------------------------

    [Fact]
    public async Task CreateInvitation_ManagerRefusedBecauseTheBillEnded_TellsTheOwner_NamingTheTeamAndTheBillOnly()
    {
        EndBill();

        var refused = _teams.CreateInvitation(_team, Manager, "anna@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();

        Assert.Equal(TeamInvitationRefusals.BillCancelled, refused.Refusal);
        var call = Assert.Single(_website.Calls);
        Assert.Equal(BillEndedUri, call.Uri);
        var body = JsonNode.Parse(call.Body)!.AsObject();
        Assert.Equal(new[] { "bill_fingerprint", "owner_subject", "team_id" }, body.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        // The Owner is named by account subject, never by address: the website reads the address itself.
        Assert.Equal(Owner, body["owner_subject"]!.GetValue<string>());
        Assert.Equal(_team, body["team_id"]!.GetValue<string>());
        Assert.Equal(new EntitlementRegistry(_db, requireLivemode: false).ReadTeamBill(_team).Fingerprint, body["bill_fingerprint"]!.GetValue<string>());
        Assert.Equal(Token, call.ServiceToken);
    }

    [Fact]
    public async Task AcceptInvitation_ABurstOfRefusedAccepts_TellsTheOwnerOnce()
    {
        AnHourBeforeThePeriodEnds();
        var tokens = Enumerable.Range(1, 5).Select(i =>
        {
            _tenants.MintOrLookupBySubject($"sub-new-{i}", $"new{i}@devthrottle-test.internal");
            return _teams.CreateInvitation(_team, Owner, $"new{i}@devthrottle-test.internal", TeamRole.Developer).AcceptToken!;
        }).ToList();
        EndBill();

        var answers = tokens.Select((t, i) => _teams.AcceptInvitation(t, $"sub-new-{i + 1}")).ToList();
        await _notice.Settled();

        Assert.All(answers, a => Assert.Equal(TeamInvitationRefusals.BillStopped, a.Refusal));
        Assert.Single(_website.Calls, c => c.Uri == BillEndedUri);
        Assert.Null(_teams.RoleOf(_team, "sub-new-1"));
    }

    [Fact]
    public async Task OpenInvitation_TheInviteeSeesTheRefusal_TellsTheOwner()
    {
        AnHourBeforeThePeriodEnds();
        _tenants.MintOrLookupBySubject("sub-anna", "anna@devthrottle-test.internal");
        var token = _teams.CreateInvitation(_team, Owner, "anna@devthrottle-test.internal", TeamRole.Developer).AcceptToken!;
        EndBill();

        var opened = _teams.OpenInvitation(token, "sub-anna");
        await _notice.Settled();

        Assert.False(opened.Invitation!.CanRespond);
        Assert.Single(_website.Calls, c => c.Uri == BillEndedUri);
    }

    [Fact]
    public async Task CreateInvitation_TheOwnerRefusedThemselves_SendsNoEmail()
    {
        EndBill();

        var refused = _teams.CreateInvitation(_team, Owner, "anna@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();

        Assert.Equal(TeamInvitationRefusals.BillCancelled, refused.Refusal);
        Assert.Empty(_website.Calls);
    }

    [Fact]
    public async Task CreateInvitation_ThePlanNeverStarted_IsRefused_ButTellsNobody()
    {
        TeamBillSeed.Remove(_db, _team);

        var refused = _teams.CreateInvitation(_team, Manager, "anna@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();

        Assert.Equal(TeamInvitationRefusals.BillNotStarted, refused.Refusal);
        Assert.Empty(_website.Calls);
    }

    // past_due and incomplete are statuses nothing writes any more - the team bill has no payment to fail. They are
    // seeded straight into the table so the gate's answer on them stays pinned: past_due still invites, an unknown
    // status is "not started", and neither is an ended bill, so nobody is told.
    [Theory]
    [InlineData("past_due", TeamInvitationOutcome.Done, null)]
    [InlineData("incomplete", TeamInvitationOutcome.Refused, TeamInvitationRefusals.BillNotStarted)]
    public async Task CreateInvitation_AStatusNothingWritesAnyMore_IsNotAnEndedBill_AndTellsNobody(
        string status, TeamInvitationOutcome expected, string? refusal)
    {
        TeamBillSeed.Put(_db, _team, status, seats: 3, autoRenew: false);

        var result = _teams.CreateInvitation(_team, Manager, "anna@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(refusal, result.Refusal);
        Assert.Empty(_website.Calls);
    }

    [Fact]
    public async Task TheEmailWasNotSent_TheNextRefusalTriesAgain_ThenStops()
    {
        EndBill();
        _website.NextBillEnded.Enqueue(HttpStatusCode.ServiceUnavailable);

        _teams.CreateInvitation(_team, Manager, "a@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();
        _teams.CreateInvitation(_team, Manager, "b@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();
        _teams.CreateInvitation(_team, Manager, "c@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();

        Assert.Equal(2, _website.Calls.Count(c => c.Uri == BillEndedUri));
    }

    [Fact]
    public async Task ThePlanRenewedAndEndedAgain_TheOwnerIsToldAgain()
    {
        EndBill();
        _teams.CreateInvitation(_team, Manager, "a@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();

        Assert.Equal(TeamBillChangeOutcome.Done, _bills.Renew(_team).Outcome);
        EndBill();
        _teams.CreateInvitation(_team, Manager, "b@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();

        var fingerprints = _website.Calls.Where(c => c.Uri == BillEndedUri)
            .Select(c => JsonNode.Parse(c.Body)!["bill_fingerprint"]!.GetValue<string>()).ToList();
        Assert.Equal(2, fingerprints.Count);
        Assert.NotEqual(fingerprints[0], fingerprints[1]);
    }

    [Fact]
    public async Task AGatewayWithNoTeamEmails_StillRefusesExactlyTheSame()
    {
        var teams = new TeamRegistry(_db, _tenants, () => _now, bills: _bills);
        EndBill();

        var refused = teams.CreateInvitation(_team, Manager, "anna@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();

        Assert.Equal(TeamInvitationOutcome.Refused, refused.Outcome);
        Assert.Equal(TeamInvitationRefusals.BillCancelled, refused.Refusal);
        Assert.Empty(_website.Calls);
    }

    [Fact]
    public async Task AnEndedBillReadWithoutAFingerprint_IsStillRefused_AndTellsNobody_InsteadOfThrowing()
    {
        var teams = new TeamRegistry(_db, _tenants, () => _now, bills: _bills, billEndedNotice: _notice,
            readTeamBill: _ => new TeamBill(true, true, EntitlementRegistry.StatusCanceled, 3, Fingerprint: null));

        var refused = teams.CreateInvitation(_team, Manager, "anna@devthrottle-test.internal", TeamRole.Developer);
        await _notice.Settled();

        Assert.Equal(TeamInvitationRefusals.BillCancelled, refused.Refusal);
        Assert.Empty(_website.Calls);
    }

    [Fact]
    public async Task ATeamWithNoOwnerRecorded_IsStillRefused_AndTellsNobody_InsteadOfThrowing()
    {
        EndBill();
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.TeamMembers.Remove(ctx.TeamMembers.Single(m => m.TeamId == _team && m.AccountSubject == Owner));
            ctx.SaveChanges();
        }

        var refused = _teams.CreateInvitation(_team, Manager, "anna@devthrottle-test.invalid", TeamRole.Developer);
        await _notice.Settled();

        Assert.Equal(TeamInvitationRefusals.BillCancelled, refused.Refusal);
        Assert.Empty(_website.Calls);
    }

    // ---- New: the mailer ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TellOwnerBillEndedAsync_NoServiceCredential_IsNotSent_AndCallsNothing()
    {
        var mailer = new TeamInvitationMailer(new TeamInvitationMailClient(new HttpClient(_website), "https://website.test"), () => null);

        var result = await mailer.TellOwnerBillEndedAsync(_team, Owner, "fingerprint");

        Assert.False(result.Sent);
        Assert.Contains("not set up to send email", result.Error);
        Assert.Empty(_website.Calls);
    }

    [Fact]
    public async Task TellOwnerBillEndedAsync_WebsiteUnreachable_IsNotSent_AndDoesNotThrow()
    {
        var mailer = new TeamInvitationMailer(
            new TeamInvitationMailClient(new HttpClient(new UnreachableWebsite()), "https://website.test"), () => Token);

        var result = await mailer.TellOwnerBillEndedAsync(_team, Owner, "fingerprint");

        Assert.False(result.Sent);
        Assert.Contains("could not be reached", result.Error);
    }

    // ---- New: the Team page, for those who may see the team's bill ---------------------------------------------------

    [Fact]
    public void DescribeTeamPage_BillEnded_TheOwnerSeesWhatItMeansAndToRenewTheTeamPlanHere()
    {
        EndBill();

        var page = _teams.DescribeTeamPage(_team, Owner).Page!;

        Assert.Equal(
            "The team's bill has ended. Nobody can join the team: new invitations cannot be sent and waiting ones cannot be accepted. " +
            "Everyone already in the team stays in it - nobody is removed - but the paid features are off until the team plan is renewed. " +
            "To let people join again, renew the team plan on the Team page.",
            page.BillNotice);
        // Teams v1 has no outside payment provider (owner ruling, 7 October): the way back is never another site.
        Assert.DoesNotContain("Stripe", page.BillNotice);
        Assert.DoesNotContain("http", page.BillNotice);
        // Nobody was removed: the page still lists all four.
        Assert.Equal(4, page.Members.Count);
    }

    [Fact]
    public void DescribeTeamPage_BillEnded_AManagerIsToldOnlyTheOwnerCanRenew()
    {
        EndBill();

        var page = _teams.DescribeTeamPage(_team, Manager).Page!;

        Assert.Equal(TeamBillNotices.EndedWhatItMeans + " Only the team's Owner can renew the team plan.", page.BillNotice);
    }

    [Fact]
    public void DescribeTeamPage_BillEnded_ADeveloperIsNotTold_BecauseTheyMayNotSeeTheTeamsBill()
    {
        EndBill();

        var page = _teams.DescribeTeamPage(_team, Developer).Page!;

        Assert.Null(page.BillNotice);
        Assert.Null(page.Bill);
    }

    [Fact]
    public void DescribeTeamPage_ARunningBill_ShowsNoNotice()
    {
        var page = _teams.DescribeTeamPage(_team, Owner).Page!;

        Assert.Null(page.BillNotice);
    }

    [Fact]
    public async Task TeamPage_Route_CarriesTheNotice_AndNoLinkToAnotherSite()
    {
        EndBill();

        var (status, body) = await RenderAsync(TeamEndpoints.TeamPage(_teams, Owner, _team));

        Assert.Equal(200, status);
        Assert.Equal(TeamBillNotices.Ended(TeamRole.Owner), body.GetProperty("billNotice").GetString());
        Assert.False(body.TryGetProperty("restartBillingUrl", out _));
    }

    [Fact]
    public async Task TeamPage_Route_ARunningBill_CarriesANullNotice()
    {
        var (_, body) = await RenderAsync(TeamEndpoints.TeamPage(_teams, Owner, _team));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("billNotice").ValueKind);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>The Owner has cancelled and the clock stands an hour before the period ends: the bill still runs, so an
    /// invitation can be sent, and it is still live (seven days) when <see cref="EndBill"/> ends the bill.</summary>
    private void AnHourBeforeThePeriodEnds()
    {
        Assert.Equal(TeamBillChangeOutcome.Done, _bills.Cancel(_team).Outcome);
        _now = _bills.Find(_team)!.CurrentPeriodEndUtc.AddHours(-1);
    }

    /// <summary>End the team's bill the way production does: the Owner cancels, the period runs out, and the renewal
    /// pass makes it canceled.</summary>
    private void EndBill()
    {
        Assert.Equal(TeamBillChangeOutcome.Done, _bills.Cancel(_team).Outcome);
        _now = _bills.Find(_team)!.CurrentPeriodEndUtc;
        var pass = _bills.RenewDue();
        Assert.Equal(1, pass.Ended);
        Assert.Equal(EntitlementRegistry.StatusCanceled, _bills.Find(_team)!.Status);
        _now = _now.AddMinutes(1);
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

    /// <summary>The website's bill-ended email route: records every call and answers the next queued status (200 with
    /// sent=true when none is queued).</summary>
    private sealed class RecordingWebsite : HttpMessageHandler
    {
        public List<(string Uri, string Body, string? ServiceToken)> Calls { get; } = new();
        public Queue<HttpStatusCode> NextBillEnded { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var token = request.Headers.TryGetValues(AccountNotifyByTenantClient.ServiceTokenHeader, out var v) ? v.Single() : null;
            lock (Calls) Calls.Add((request.RequestUri!.ToString(), body, token));
            HttpStatusCode status;
            lock (NextBillEnded) status = NextBillEnded.Count > 0 ? NextBillEnded.Dequeue() : HttpStatusCode.OK;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK
                    ? "{\"data\":{\"sent\":true,\"id\":\"re_1\"}}"
                    : "{\"error\":{\"code\":\"unavailable\",\"message\":\"down\"}}"),
            };
        }
    }

    private sealed class UnreachableWebsite : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No connection could be made.");
    }
}
