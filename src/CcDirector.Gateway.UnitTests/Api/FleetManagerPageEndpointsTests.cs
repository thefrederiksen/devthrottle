using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The Fleet Manager page route (the Fleet Manager mission, step 6), driven through its handler with a real
/// outcome store and handed-in roster sources. The guard's refusal of a session key is also in
/// <see cref="SessionKeyGuardTests"/>; this pins the handler's own second refusal and the account partition.
/// </summary>
public sealed class FleetManagerPageEndpointsTests : IDisposable
{
    private const string TenantHeader = "X-Test-Account";
    private static readonly TenantId TenantA = new("acct-fm-page-a");
    private static readonly TenantId TenantB = new("acct-fm-page-b");
    private static readonly DateTime Now = new(2026, 9, 16, 14, 30, 0, DateTimeKind.Utc);
    private const string MarkedA = "70000000-0000-4000-8000-000000000001";
    private const string OwnedA = "70000000-0000-4000-8000-000000000002";
    private const string LooseB = "70000000-0000-4000-8000-000000000003";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly FleetOutcomeStore _outcomes;

    public FleetManagerPageEndpointsTests()
    {
        _outcomes = new FleetOutcomeStore(_harness.Open());
    }

    public void Dispose() => _harness.Dispose();

    private static TenantId? ResolveTenant(HttpContext ctx)
        => ctx.Request.Headers.TryGetValue(TenantHeader, out var v) && v.ToString().Length > 0 ? new TenantId(v.ToString()) : null;

    private static DefaultHttpContext Request(TenantId? tenant, bool asSession = false)
    {
        var ctx = new DefaultHttpContext();
        if (tenant is { } t) ctx.Request.Headers[TenantHeader] = t.Value;
        if (asSession)
            ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
                new SessionCredentialIdentity(Guid.Parse(MarkedA), tenant ?? TenantId.Local, "dir-a");
        return ctx;
    }

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string? Error(IResult result)
    {
        var value = result.GetType().GetProperty("Value")!.GetValue(result)!;
        return value.GetType().GetProperty("error")!.GetValue(value) as string;
    }

    private static FleetManagerPageSources Sources() => new(
        LiveRoster: tenant => tenant == TenantA
            ? new[]
            {
                new SessionDto { SessionId = MarkedA, Name = "Fleet Manager", ActivityState = "Idle", CreatedAt = Now.AddHours(-2) },
                new SessionDto { SessionId = OwnedA, Name = "Fix the flaky list test", ActivityState = "Working",
                    ControllerSessionId = MarkedA, IsControlled = true, HasLiveSupervisor = true, CreatedAt = Now.AddMinutes(-9) },
            }
            : new[] { new SessionDto { SessionId = LooseB, Name = "Another account's session", ActivityState = "Idle", CreatedAt = Now } },
        MarkedSessionId: tenant => tenant == TenantA ? MarkedA : null,
        LastKnownSession: (tenant, sid) => tenant == TenantA && sid == MarkedA
            ? new SessionDto { SessionId = MarkedA, Name = "Fleet Manager", ActivityState = "Idle" }
            : null,
        LatestVerdict: (_, _) => null,
        TimeZone: _ => TimeZoneInfo.Utc,
        NowUtc: () => Now);

    private void FileDecision(TenantId tenant, string title)
        => _outcomes.File(tenant, new FleetOutcomeFileRequest
        {
            Kind = "decision",
            Title = title,
            Decision = new FleetDecisionDetails { Question = title, Options = { "Yes.", "No." } },
        }, MarkedA, Now.AddMinutes(-4));

    [Fact]
    public void Route_IsTheMappedLiteral()
        => Assert.Equal("/gateway/fleet-manager/page", FleetManagerPageEndpoints.PageRoute);

    [Fact]
    public void Read_NoAccount_IsRefused()
        => Assert.Equal(403, Status(FleetManagerPageEndpoints.Read(Request(null), ResolveTenant, _outcomes, Sources())));

    [Fact]
    public void Read_FromASessionKey_IsRefusedEvenForTheFleetManagerItself()
    {
        FileDecision(TenantA, "Keep the old layout?");

        var result = FleetManagerPageEndpoints.Read(Request(TenantA, asSession: true), ResolveTenant, _outcomes, Sources());

        Assert.Equal(403, Status(result));
        Assert.Equal("The Fleet Manager page is the owner's. A session reads GET /gateway/fleet-manager/digest.", Error(result));
    }

    [Fact]
    public void Read_Owner_ReturnsTheFoldedPageForTheirAccount()
    {
        FileDecision(TenantA, "Keep the old layout?");

        var result = FleetManagerPageEndpoints.Read(Request(TenantA), ResolveTenant, _outcomes, Sources());

        Assert.Equal(200, Status(result));
        var dto = Assert.IsType<JsonHttpResult<FleetManagerPageDto>>(result).Value!;
        Assert.Equal(MarkedA, dto.FleetManagerSessionId);
        Assert.Equal(1, dto.WaitingCount);
        Assert.Equal("Keep the old layout?", Assert.Single(dto.Waiting.Items).Title);
        Assert.Equal("4m", dto.Waiting.Items[0].Age);
        Assert.Equal(new[] { "Yes.", "No." }, Assert.Single(dto.Cards).Actions.Select(a => a.Words));
        Assert.Equal(OwnedA, Assert.Single(dto.UnderWay.Items).Id);
        Assert.Equal(0, dto.NotMine.Count);
    }

    [Fact]
    public void Read_AnotherAccountsRecordsAndSessions_AreNeverShown()
    {
        FileDecision(TenantA, "Account A's question");
        FileDecision(TenantB, "Account B's question");

        var dto = Assert.IsType<JsonHttpResult<FleetManagerPageDto>>(
            FleetManagerPageEndpoints.Read(Request(TenantB), ResolveTenant, _outcomes, Sources())).Value!;

        Assert.Null(dto.FleetManagerSessionId);
        Assert.Equal("Account B's question", Assert.Single(dto.Cards).Title);
        Assert.Equal("Account B's question", Assert.Single(dto.Waiting.Items).Title);
        Assert.Empty(dto.UnderWay.Items);
        Assert.Equal(1, dto.NotMine.Count);
    }

    [Fact]
    public void Read_MoreOpenRecordsThanOnePage_AreAllWaiting()
    {
        // Every open record reaches the badge, not only the store's first page.
        for (var i = 0; i < FleetOutcomeStore.MaxCount + 5; i++)
            FileDecision(TenantA, $"Question {i}");

        var dto = Assert.IsType<JsonHttpResult<FleetManagerPageDto>>(
            FleetManagerPageEndpoints.Read(Request(TenantA), ResolveTenant, _outcomes, Sources())).Value!;

        Assert.Equal(FleetOutcomeStore.MaxCount + 5, dto.WaitingCount);
        Assert.Equal(FleetManagerPageFold.CardCount, dto.Cards.Count);
    }

    [Fact]
    public void Read_AnAnsweredRecord_LeavesTheWaitingCountButKeepsItsCard()
    {
        FileDecision(TenantA, "Keep the old layout?");
        var id = Guid.Parse(_outcomes.List(TenantA, "open", null, 10).Single().Id);
        _outcomes.Answer(TenantA, id, "Yes.", FleetOutcomeStore.OwnerCaller, FleetOutcomeStore.RoleOwner, Now);

        var dto = Assert.IsType<JsonHttpResult<FleetManagerPageDto>>(
            FleetManagerPageEndpoints.Read(Request(TenantA), ResolveTenant, _outcomes, Sources())).Value!;

        Assert.Equal(0, dto.WaitingCount);
        var card = Assert.Single(dto.Cards);
        Assert.True(card.Answered);
        Assert.Equal("Yes.", card.Answer);
        Assert.Empty(card.Actions);
    }
}
