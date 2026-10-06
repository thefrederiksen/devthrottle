using System.Net;
using System.Text.Json.Nodes;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The seat convergence does not retry forever for a team whose subscription has ENDED (devthrottle_internal#2311,
/// Phase 1 review finding 3). The website refuses that team's sync with 409 no_active_team_subscription; the team is
/// marked and logged once, and convergence stops calling for it until its bill row changes. Every other failure is
/// retried as before. Over a real, throwaway, fully migrated Gateway database; the website is a recording HTTP
/// handler and nothing leaves the process. In the log-capture collection because the tests read what was logged.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class TeamSeatConvergenceEndedSubscriptionTests : IDisposable
{
    private const string Owner = "sub-owner";
    private const string Developer = "sub-developer";
    private const string Token = "test-gateway-service-token";
    private const string SubscriptionId = "sub_test_ended_123";
    private const string StoppedMarker = "STOPPED - the website says the team has no running bill";

    // The website's exact answer for a team whose subscription Stripe has ended (#2315, team-billing.js syncTeamSeats).
    private const string EndedBody =
        "{\"error\":{\"type\":\"conflict\",\"code\":\"no_active_team_subscription\",\"message\":\"This team's subscription is canceled in Stripe, so there is no seat count to change.\"}}";

    private static readonly DateTime PeriodEnd = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WrittenAt = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly ScriptedWebsite _website = new();
    private readonly TeamSeatSync _seatSync;
    private readonly TeamSeatConvergence _convergence;
    private readonly string _ended;
    private readonly string _healthy;

    public TeamSeatConvergenceEndedSubscriptionTests()
    {
        _db = _harness.Open();
        var tenants = new TenantRegistry(_db);
        using (var ctx = _db.CreateUnscopedContext())
        {
            // The website owns this table and creates it; the Gateway's migrations never do.
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS team_entitlements (" +
                "team_id TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, seats INTEGER NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, livemode INTEGER NULL, updated_at TEXT NULL)");
        }
        tenants.MintOrLookupBySubject(Owner, "owner@acme.example");
        tenants.MintOrLookupBySubject(Developer, "developer@acme.example");

        // No seat sync on the registry: building the teams calls nothing, so every call below is convergence's.
        var teams = new TeamRegistry(_db, tenants);
        _ended = teams.CreateTeam(Owner, "Ended").Team!.TeamId;
        _healthy = teams.CreateTeam(Owner, "Healthy").Team!.TeamId;
        teams.AddMember(_ended, Developer, TeamRole.Developer);
        teams.AddMember(_healthy, Developer, TeamRole.Developer);

        // Both teams have two paid members and a bill for one seat, so both differ and both are called.
        SetBill(_ended, "past_due", seats: 1);
        SetBill(_healthy, "active", seats: 1);

        _seatSync = new TeamSeatSync(new EntitlementRegistry(_db, requireLivemode: false),
            new TeamSeatSyncClient(new HttpClient(_website), "https://website.test"), () => Token);
        _convergence = new TeamSeatConvergence(_db, _seatSync);
    }

    public void Dispose() => _harness.Dispose();

    // ---- The four tests the mandate names ------------------------------------------------------------------------

    [Fact]
    public async Task RunOnceAsync_WebsiteSaysSubscriptionEnded_MarksTheTeamLogsOnceAndStopsCalling()
    {
        _website.AnswerFor[_ended] = () => (HttpStatusCode.Conflict, EndedBody);
        BillInStep(_healthy);

        IReadOnlyList<string> lines;
        using (var log = FileLog.RedirectForTests())
        {
            Assert.Equal(1, await _convergence.RunOnceAsync());
            Assert.Equal(0, await _convergence.RunOnceAsync());
            Assert.Equal(0, await _convergence.RunOnceAsync());
            lines = log.DrainAndReadLines();
        }

        Assert.Equal(1, _website.CallsFor(_ended));
        // The pass summary never says the stopped team will be retried: the first pass counts it as stopped, the
        // later ones as not called.
        var summaries = lines.Where(l => l.Contains("[TeamSeatConvergence] RunOnceAsync:", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, summaries.Count);
        Assert.Contains("0 of those not done (retried next pass), 1 refused because their subscription has ended", summaries[0]);
        Assert.Contains("0 of those not done (retried next pass), 0 refused because their subscription has ended", summaries[1]);
        Assert.Contains("1 not called because their subscription has ended", summaries[1]);
        var stopped = Assert.Single(lines, l => l.Contains(StoppedMarker, StringComparison.Ordinal));
        // The team is logged only in its hashed form, and no person is named.
        Assert.Contains(new Core.Tenancy.TenantId(_ended).ToLogString(), stopped);
        Assert.DoesNotContain(lines, l => l.Contains(_ended, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains('@'));
        Assert.DoesNotContain(lines, l => l.Contains(Owner, StringComparison.Ordinal) || l.Contains(Developer, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(SubscriptionId, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("seats")]
    [InlineData("period_end")]
    [InlineData("subscription")]
    [InlineData("updated_at")]
    public async Task RunOnceAsync_EndedTeamsBillRowChanges_CallsSyncExactlyOnceMore(string changed)
    {
        _website.AnswerFor[_ended] = () => (HttpStatusCode.Conflict, EndedBody);
        BillInStep(_healthy);
        await _convergence.RunOnceAsync();
        await _convergence.RunOnceAsync();
        Assert.Equal(1, _website.CallsFor(_ended));

        switch (changed)
        {
            case "status": SetBill(_ended, "active", seats: 1); break;
            case "seats": SetBill(_ended, "past_due", seats: 3); break;   // still differs from the two paid members
            case "period_end": SetBill(_ended, "past_due", seats: 1, periodEnd: PeriodEnd.AddMonths(1)); break;
            case "subscription": SetBill(_ended, "past_due", seats: 1, subscriptionId: "sub_test_other_456"); break;
            case "updated_at": SetBill(_ended, "past_due", seats: 1, updatedAt: WrittenAt.AddMinutes(5)); break;
        }

        Assert.Equal(1, await _convergence.RunOnceAsync());   // tried again, once
        Assert.Equal(2, _website.CallsFor(_ended));
        Assert.Equal(0, await _convergence.RunOnceAsync());   // refused again, so stopped again
        Assert.Equal(2, _website.CallsFor(_ended));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":\"unavailable\",\"message\":\"down\"}}")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.GatewayTimeout, "")]
    // Another 409 from the same route is NOT the ended refusal and must not stop the retries.
    [InlineData(HttpStatusCode.Conflict, "{\"error\":{\"code\":\"unexpected_subscription_shape\",\"message\":\"x\"}}")]
    // The ended code on any status other than 409 is not the website's refusal either.
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"no_active_team_subscription\",\"message\":\"x\"}}")]
    public async Task RunOnceAsync_OtherFailure_IsRetriedEveryPassAndNeverMarksTheTeam(HttpStatusCode status, string body)
    {
        _website.AnswerFor[_ended] = () => (status, body);
        BillInStep(_healthy);

        IReadOnlyList<string> lines;
        using (var log = FileLog.RedirectForTests())
        {
            Assert.Equal(1, await _convergence.RunOnceAsync());
            Assert.Equal(1, await _convergence.RunOnceAsync());
            Assert.Equal(1, await _convergence.RunOnceAsync());
            lines = log.DrainAndReadLines();
        }

        Assert.Equal(3, _website.CallsFor(_ended));
        Assert.DoesNotContain(lines, l => l.Contains(StoppedMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnceAsync_WebsiteUnreachable_IsRetriedEveryPassAndNeverMarksTheTeam()
    {
        _website.AnswerFor[_ended] = () => throw new HttpRequestException("connection refused");
        BillInStep(_healthy);

        Assert.Equal(1, await _convergence.RunOnceAsync());
        Assert.Equal(1, await _convergence.RunOnceAsync());

        Assert.Equal(2, _website.CallsFor(_ended));
    }

    [Fact]
    public async Task RunOnceAsync_OneTeamEndedOneHealthy_TheHealthyTeamConvergesAsBefore()
    {
        _website.AnswerFor[_ended] = () => (HttpStatusCode.Conflict, EndedBody);
        _website.AnswerFor[_healthy] = () => (HttpStatusCode.OK, "{\"data\":{\"changed\":true,\"seats\":2}}");

        Assert.Equal(2, await _convergence.RunOnceAsync());   // both differ, both are called
        Assert.Equal(1, _website.CallsFor(_healthy));

        // The website billed the healthy team's new count and its webhook wrote it to the row.
        SetBill(_healthy, "active", seats: 2);
        Assert.Equal(0, await _convergence.RunOnceAsync());   // healthy in step, ended stopped

        // A later change to the healthy team is still converged as before.
        SetBill(_healthy, "active", seats: 1, updatedAt: WrittenAt.AddHours(1));
        Assert.Equal(1, await _convergence.RunOnceAsync());
        Assert.Equal(2, _website.CallsFor(_healthy));
        Assert.Equal(1, _website.CallsFor(_ended));
    }

    // ---- Every public method added -------------------------------------------------------------------------------

    [Fact]
    public async Task ConvergeAsync_MarkedTeamWithAnUnchangedBill_AnswersSubscriptionEndedWithoutCalling()
    {
        _website.AnswerFor[_ended] = () => (HttpStatusCode.Conflict, EndedBody);

        var (first, firstCall) = await _seatSync.ConvergeAsync(_ended, gatewayPaidMembers: 2);
        var (second, secondCall) = await _seatSync.ConvergeAsync(_ended, gatewayPaidMembers: 2);

        Assert.Equal(SeatSyncVerdict.CallSync, first);
        Assert.True(firstCall!.SubscriptionEnded);
        Assert.Equal(SeatSyncVerdict.SubscriptionEnded, second);
        Assert.Null(secondCall);
        Assert.Equal(1, _website.CallsFor(_ended));
    }

    [Fact]
    public async Task ConvergeAsync_MarkedTeamComesIntoStep_ClearsTheMarkSoALaterDifferenceIsCalled()
    {
        _website.AnswerFor[_ended] = () => (HttpStatusCode.Conflict, EndedBody);
        await _seatSync.ConvergeAsync(_ended, gatewayPaidMembers: 2);

        // The counts match (a member left), then differ again on the SAME bill row: the stop no longer applies.
        Assert.Equal(SeatSyncVerdict.InSync, (await _seatSync.ConvergeAsync(_ended, gatewayPaidMembers: 1)).Verdict);
        Assert.Equal(SeatSyncVerdict.CallSync, (await _seatSync.ConvergeAsync(_ended, gatewayPaidMembers: 2)).Verdict);

        Assert.Equal(2, _website.CallsFor(_ended));
    }

    [Theory]
    [InlineData(false, 409, "no_active_team_subscription", true)]
    [InlineData(false, 409, "unexpected_subscription_shape", false)]
    [InlineData(false, 409, "subscription_team_mismatch", false)]
    [InlineData(false, 409, "no_paid_seats", false)]
    [InlineData(false, 409, null, false)]
    [InlineData(false, 503, "no_active_team_subscription", false)]
    [InlineData(false, 0, null, false)]
    [InlineData(true, 409, "no_active_team_subscription", false)]
    public void SubscriptionEnded_EachResult_IsTrueOnlyForTheWebsites409NoActiveSubscription(bool synced, int status, string? code, bool expected)
    {
        Assert.Equal(expected, new TeamSeatSyncResult(synced, status, "message", code).SubscriptionEnded);
    }

    [Fact]
    public void TeamBillFingerprint_EachFieldThatDescribesTheBill_ChangesTheFingerprint()
    {
        var baseline = EntitlementRegistry.TeamBillFingerprint(Row());

        Assert.Equal(baseline, EntitlementRegistry.TeamBillFingerprint(Row()));
        Assert.Equal(baseline, EntitlementRegistry.TeamBillFingerprint(Row(status: " past_due ")));   // read trimmed, as the status always is
        Assert.NotEqual(baseline, EntitlementRegistry.TeamBillFingerprint(Row(status: "active")));
        Assert.NotEqual(baseline, EntitlementRegistry.TeamBillFingerprint(Row(seats: 2)));
        Assert.NotEqual(baseline, EntitlementRegistry.TeamBillFingerprint(Row(seats: null)));
        Assert.NotEqual(baseline, EntitlementRegistry.TeamBillFingerprint(Row(periodEnd: PeriodEnd.AddDays(1))));
        Assert.NotEqual(baseline, EntitlementRegistry.TeamBillFingerprint(Row(subscriptionId: "sub_other")));
        Assert.NotEqual(baseline, EntitlementRegistry.TeamBillFingerprint(Row(updatedAt: WrittenAt.AddSeconds(1))));
        // A one-way hash: the subscription reference is not readable in it.
        Assert.DoesNotContain(SubscriptionId, baseline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadTeamBill_TeamWithABill_CarriesTheRowsFingerprint()
    {
        var read = new EntitlementRegistry(_db, requireLivemode: false).ReadTeamBill(_ended);

        Assert.Equal(EntitlementRegistry.TeamBillFingerprint(Row()), read.Fingerprint);
    }

    [Fact]
    public void ReadTeamBill_OnAHostedGateway_ALiveRowCarriesItsFingerprint_AndATestModeRowIsNoBillWithNone()
    {
        // Decision D8 beside #3537: the one reader computes the fingerprint for every row it counts as a bill. On a
        // hosted Gateway a test-mode row is no bill, so it has no fingerprint, and convergence answers NoBill for it.
        var hosted = new EntitlementRegistry(_db, requireLivemode: true);
        Assert.Equal(EntitlementRegistry.TeamBillFingerprint(Row()), hosted.ReadTeamBill(_ended).Fingerprint);

        using (var ctx = _db.CreateUnscopedContext())
            ctx.Database.ExecuteSqlRaw("UPDATE team_entitlements SET livemode = 0 WHERE team_id = {0}", _ended);
        var testMode = hosted.ReadTeamBill(_ended);
        Assert.True(testMode.Known);
        Assert.False(testMode.HasBill);
        Assert.Null(testMode.Fingerprint);
        Assert.Equal(SeatSyncVerdict.NoBill, TeamSeatSync.Decide(testMode, gatewayPaidMembers: 2));
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static TeamEntitlementEntity Row(string status = "past_due", int? seats = 1, DateTime? periodEnd = null,
        string subscriptionId = SubscriptionId, DateTime? updatedAt = null) => new()
    {
        TeamId = "any",
        Status = status,
        Seats = seats,
        CurrentPeriodEnd = periodEnd ?? PeriodEnd,
        StripeSubscriptionId = subscriptionId,
        Livemode = true,
        UpdatedAt = updatedAt ?? WrittenAt,
    };

    private void SetBill(string teamId, string status, int seats, DateTime? periodEnd = null,
        string subscriptionId = SubscriptionId, DateTime? updatedAt = null)
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("DELETE FROM team_entitlements WHERE team_id = {0}", teamId);
        var row = Row(status, seats, periodEnd, subscriptionId, updatedAt);
        row.TeamId = teamId;
        ctx.TeamEntitlements.Add(row);
        ctx.SaveChanges();
    }

    // The healthy team's bill matches its two paid members, so it calls nothing and the counts below are the ended
    // team's alone.
    private void BillInStep(string teamId) => SetBill(teamId, "active", seats: 2);

    /// <summary>Answers each team's sync from a script and counts the calls per team.</summary>
    private sealed class ScriptedWebsite : HttpMessageHandler
    {
        public Dictionary<string, Func<(HttpStatusCode Status, string Body)>> AnswerFor { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

        public int CallsFor(string teamId) { lock (_calls) return _calls.GetValueOrDefault(teamId); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var teamId = JsonNode.Parse(body)!["team_id"]!.GetValue<string>();
            lock (_calls) _calls[teamId] = _calls.GetValueOrDefault(teamId) + 1;
            var (status, answer) = AnswerFor.TryGetValue(teamId, out var script)
                ? script()
                : throw new InvalidOperationException("The test did not script an answer for this team.");
            return new HttpResponseMessage(status) { Content = new StringContent(answer) };
        }
    }
}
