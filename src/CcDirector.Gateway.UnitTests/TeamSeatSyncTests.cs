using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Account;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The seat sync (devthrottle_internal #2299, seam section 4): after a membership change the Gateway names the
/// TEAM to the website - never a number - and a convergence check calls again wherever the bill's seat count and
/// the Gateway's paid-member count differ, so a failed call is retried rather than lost.
/// </summary>
public sealed class TeamSeatSyncTests : IDisposable
{
    private const string TeamA = "7f0c2a52-6f1e-4d7e-9b7a-0a1b2c3d4e5f";
    private const string Token = "test-gateway-service-token";
    private const string BaseUrl = "https://website.test";

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>Records every request and answers with a fixed status.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

        public RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{\"data\":{\"seats\":1}}")
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }

    private GatewayDatabase OpenWithTeamRow(string? status, int? seats)
    {
        var db = _harness.Open();
        using var ctx = db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS team_entitlements (" +
            "team_id TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, seats INTEGER NULL, " +
            "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, livemode INTEGER NULL, updated_at TEXT NULL)");
        if (status is not null)
            ctx.Database.ExecuteSqlRaw(
                "INSERT INTO team_entitlements (team_id, status, seats, livemode) VALUES ({0}, {1}, {2}, 1)",
                TeamA, status, seats);
        return db;
    }

    private static TeamSeatSync Sync(GatewayDatabase db, RecordingHandler handler, string? token = Token) =>
        new(new EntitlementRegistry(db, requireLivemode: false),
            new TeamSeatSyncClient(new HttpClient(handler), BaseUrl),
            () => token);

    // ---- The client sends exactly {team_id} and the service header --------------------------------------------

    [Fact]
    public async Task SyncSeatsAsync_AnyTeam_SendsOnlyTheTeamIdAndTheServiceHeader()
    {
        var handler = new RecordingHandler();
        var client = new TeamSeatSyncClient(new HttpClient(handler), BaseUrl);

        var result = await client.SyncSeatsAsync(Token, TeamA);

        Assert.True(result.Synced);
        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{BaseUrl}/api/v1/teams/sync-seats", request.RequestUri!.ToString());

        // Exactly one key, team_id, carrying the team id. No count can ride along.
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(body));
        Assert.Equal(new[] { "team_id" }, json.Select(kv => kv.Key).ToArray());
        Assert.Equal(TeamA, (string?)json["team_id"]);

        // The service credential in its own header, exactly as the owner-email client sends it, and no
        // Authorization header beside it.
        Assert.Equal(Token, Assert.Single(request.Headers.GetValues(AccountNotifyByTenantClient.ServiceTokenHeader)));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task SyncSeatsAsync_WebsiteRefuses_ReturnsNotSyncedWithTheWebsitesMessage()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden,
            "{\"error\":{\"code\":\"bad_token\",\"message\":\"The Gateway token was refused.\"}}");
        var client = new TeamSeatSyncClient(new HttpClient(handler), BaseUrl);

        var result = await client.SyncSeatsAsync(Token, TeamA);

        Assert.False(result.Synced);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("bad_token", result.ErrorCode);
        Assert.Equal("The Gateway token was refused.", result.Error);
    }

    // ---- The convergence check ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 1, SeatSyncVerdict.InSync)]
    [InlineData(4, 4, SeatSyncVerdict.InSync)]
    [InlineData(3, 2, SeatSyncVerdict.CallSync)]
    [InlineData(2, 3, SeatSyncVerdict.CallSync)]
    public void Compare_GatewayCountAgainstBilledSeats_SaysCallSyncOnlyWhenTheyDiffer(int members, int billed, SeatSyncVerdict expected)
    {
        Assert.Equal(expected, TeamSeatSync.Compare(members, billed));
    }

    [Fact]
    public void Compare_BillRecordedNoSeatCount_SaysCallSync()
    {
        Assert.Equal(SeatSyncVerdict.CallSync, TeamSeatSync.Compare(1, null));
    }

    [Fact]
    public async Task ConvergeAsync_CountsDiffer_CallsSyncOnce()
    {
        var db = OpenWithTeamRow(EntitlementRegistry.StatusActive, seats: 2);
        var handler = new RecordingHandler();

        var (verdict, call) = await Sync(db, handler).ConvergeAsync(TeamA, gatewayPaidMembers: 3);

        Assert.Equal(SeatSyncVerdict.CallSync, verdict);
        Assert.NotNull(call);
        Assert.True(call!.Synced);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ConvergeAsync_CountsEqual_CallsNothing()
    {
        var db = OpenWithTeamRow(EntitlementRegistry.StatusActive, seats: 3);
        var handler = new RecordingHandler();

        var (verdict, call) = await Sync(db, handler).ConvergeAsync(TeamA, gatewayPaidMembers: 3);

        Assert.Equal(SeatSyncVerdict.InSync, verdict);
        Assert.Null(call);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ConvergeAsync_PastDueAndCountsDiffer_StillCallsSync()
    {
        // A failed payment does not freeze the seat count: the bill keeps following membership.
        var db = OpenWithTeamRow(EntitlementRegistry.StatusPastDue, seats: 1);
        var handler = new RecordingHandler();

        var (verdict, _) = await Sync(db, handler).ConvergeAsync(TeamA, gatewayPaidMembers: 2);

        Assert.Equal(SeatSyncVerdict.CallSync, verdict);
        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData(null)]          // no row: the Owner has not finished checkout
    [InlineData("canceled")]    // the subscription has ended
    public async Task ConvergeAsync_NoLiveBill_CallsNothing(string? status)
    {
        var db = OpenWithTeamRow(status, seats: 1);
        var handler = new RecordingHandler();

        var (verdict, _) = await Sync(db, handler).ConvergeAsync(TeamA, gatewayPaidMembers: 5);

        Assert.Equal(SeatSyncVerdict.NoBill, verdict);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ConvergeAsync_TeamRowUnreadable_IsUnknownAndCallsNothing()
    {
        var db = _harness.Open();   // no team_entitlements table
        var handler = new RecordingHandler();

        var (verdict, _) = await Sync(db, handler).ConvergeAsync(TeamA, gatewayPaidMembers: 2);

        Assert.Equal(SeatSyncVerdict.Unknown, verdict);
        Assert.Empty(handler.Calls);
    }

    // ---- The method the membership code calls after its commit -------------------------------------------------

    [Fact]
    public async Task SyncAfterMembershipChangeAsync_AnyChange_CallsTheWebsiteForThatTeam()
    {
        var db = _harness.Open();
        var handler = new RecordingHandler();

        var result = await Sync(db, handler).SyncAfterMembershipChangeAsync(TeamA);

        Assert.True(result.Synced);
        var (_, body) = Assert.Single(handler.Calls);
        Assert.Equal(TeamA, (string?)JsonNode.Parse(body)!["team_id"]);
    }

    [Fact]
    public async Task SyncAfterMembershipChangeAsync_ServiceTokenNotSet_CallsNothingAndSaysWhy()
    {
        var db = _harness.Open();
        var handler = new RecordingHandler();

        var result = await Sync(db, handler, token: null).SyncAfterMembershipChangeAsync(TeamA);

        Assert.False(result.Synced);
        Assert.Contains(AccountNotifyByTenantClient.ServiceTokenEnvVar, result.Error);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task SyncAfterMembershipChangeAsync_WebsiteUnreachable_ReturnsNotSyncedWithoutThrowing()
    {
        // The membership change has already committed; failing the member's request now would report a change
        // that happened as one that did not. Convergence retries.
        var db = _harness.Open();
        var sync = new TeamSeatSync(new EntitlementRegistry(db, requireLivemode: false),
            new TeamSeatSyncClient(new HttpClient(new ThrowingHandler()), BaseUrl), () => Token);

        var result = await sync.SyncAfterMembershipChangeAsync(TeamA);

        Assert.False(result.Synced);
        Assert.Equal(0, result.StatusCode);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    // ---- Who counts as a paid seat -----------------------------------------------------------------------------

    [Theory]
    [InlineData(TeamSeatRoles.Owner, true)]
    [InlineData(TeamSeatRoles.Manager, true)]
    [InlineData(TeamSeatRoles.Developer, true)]
    [InlineData(TeamSeatRoles.Collaborator, false)]
    [InlineData("pending", false)]
    [InlineData(null, false)]
    public void IsPaidSeat_EachRole_CountsOnlyOwnerManagerAndDeveloper(string? role, bool paid)
    {
        Assert.Equal(paid, TeamSeatRoles.IsPaidSeat(role));
    }
}
