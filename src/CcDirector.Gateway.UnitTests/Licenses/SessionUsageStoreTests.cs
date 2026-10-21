using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// What is using the license, over the real store on a throwaway SQLite file: a session keeps ONE running total
/// through a compaction, its first sight writes no sample, a sample is tagged with the account its machine was on,
/// readings are kept only when they say something new, the record is the caller's account's alone, and the two routes
/// answer the shape the page and the command are built from.
/// </summary>
public sealed class SessionUsageStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly SessionUsageStore _usage;
    private readonly LicenseStore _licenses;

    private static readonly TenantId TenantOne = new("acct-one");
    private static readonly TenantId TenantTwo = new("acct-two");

    public SessionUsageStoreTests()
    {
        _db = _harness.Open();
        _usage = new SessionUsageStore(_db);
        _licenses = new LicenseStore(_db);
    }

    public void Dispose() => _harness.Dispose();

    private static SessionUsageObservation Seen(string id, long input, long output, string? account = A, string conversation = "conv-1",
        string? controller = null, string state = "Working")
        => new(id, "dir-1", "claude", 101, "Developer one", null, null, controller, "machine-one", "opus", state,
            T0.AddHours(-3), account, conversation,
            new TokenTotalsDto { InputTokens = input, OutputTokens = output, CacheReadTokens = 0, CacheCreationTokens = 0 });

    [Fact]
    public void Observe_TheFirstSight_GoesIntoTheTotal_ButWritesNoSample_BecauseThatUseWasNotNow()
    {
        var samples = _usage.Observe(TenantOne, new[] { Seen("s1", 1000, 100) }, T0);

        Assert.Equal(0, samples);
        var row = Assert.Single(_usage.ReadSessions(TenantOne, T0.AddDays(-1)));
        Assert.Equal(1500, row.WeightedTotal);
        Assert.Equal(1500, SessionUsageStore.WeightedByAccount(row)[A]);
        Assert.Empty(_usage.UseSince(TenantOne, T0.AddDays(-1)));
    }

    [Fact]
    public void Observe_KeepsOneRunningTotal_ThroughACompactionThatStartsTheCountAgain()
    {
        _usage.Observe(TenantOne, new[] { Seen("s1", 1000, 100) }, T0);
        _usage.Observe(TenantOne, new[] { Seen("s1", 1100, 120) }, T0.AddMinutes(1));       // +100 in, +20 out = 200
        _usage.Observe(TenantOne, new[] { Seen("s1", 10, 2, conversation: "conv-2") }, T0.AddMinutes(2)); // fresh: 10 + 10 = 20

        var row = Assert.Single(_usage.ReadSessions(TenantOne, T0.AddDays(-1)));
        Assert.Equal(1500 + 200 + 20, row.WeightedTotal);
        var use = Assert.Single(_usage.UseSince(TenantOne, T0));
        Assert.Equal(("s1", A, 220.0), (use.SessionId, use.AccountEmail, use.Weighted));
    }

    [Fact]
    public void Observe_CountsNothing_WhenANewConversationIsNamedWithTheOldConversationsCounts()
    {
        // After a clear the Director names the new conversation at once but re-reads the counts only at its first turn
        // end: until then the old counts arrive under the new name, and they are not new use.
        _usage.Observe(TenantOne, new[] { Seen("s1", 1000, 100) }, T0);
        var samples = _usage.Observe(TenantOne, new[] { Seen("s1", 1000, 100, conversation: "conv-2") }, T0.AddMinutes(1));
        // Then the new conversation's own first counts, smaller: all of them are new use.
        _usage.Observe(TenantOne, new[] { Seen("s1", 10, 2, conversation: "conv-2") }, T0.AddMinutes(2));

        Assert.Equal(0, samples);
        var row = Assert.Single(_usage.ReadSessions(TenantOne, T0.AddDays(-1)));
        Assert.Equal(1500 + 20, row.WeightedTotal);
        var use = Assert.Single(_usage.UseSince(TenantOne, T0));
        Assert.Equal(20.0, use.Weighted);
    }

    [Fact]
    public void Observe_CountsANewConversationsOwnCounts_WholeEvenWhenLarger_AfterTheOldCountsCameUnderItsName()
    {
        _usage.Observe(TenantOne, new[] { Seen("s1", 1000, 100) }, T0);
        _usage.Observe(TenantOne, new[] { Seen("s1", 1000, 100, conversation: "conv-2") }, T0.AddMinutes(1));
        _usage.Observe(TenantOne, new[] { Seen("s1", 2000, 200, conversation: "conv-2") }, T0.AddMinutes(2));

        var row = Assert.Single(_usage.ReadSessions(TenantOne, T0.AddDays(-1)));
        Assert.Equal(1500 + 3000, row.WeightedTotal);
    }

    [Fact]
    public void Observe_TagsEachSampleWithTheAccountItsMachineWasOn_AtTheTime()
    {
        _usage.Observe(TenantOne, new[] { Seen("s1", 0, 0) }, T0);
        _usage.Observe(TenantOne, new[] { Seen("s1", 100, 0) }, T0.AddMinutes(1));
        _usage.Observe(TenantOne, new[] { Seen("s1", 300, 0, account: B) }, T0.AddMinutes(2));

        Assert.Equal(100, _usage.AccountUseBetween(TenantOne, A, T0, T0.AddMinutes(5)));
        Assert.Equal(200, _usage.AccountUseBetween(TenantOne, B, T0, T0.AddMinutes(5)));
        var row = Assert.Single(_usage.ReadSessions(TenantOne, T0.AddDays(-1)));
        Assert.Equal((100.0, 200.0), (SessionUsageStore.WeightedByAccount(row)[A], SessionUsageStore.WeightedByAccount(row)[B]));
    }

    [Fact]
    public void Observe_ASessionWithNoTotalsYet_IsDescribed_AndUsesNothing()
    {
        var noTotals = Seen("s1", 0, 0) with { Totals = null };
        _usage.Observe(TenantOne, new[] { noTotals }, T0);

        var row = Assert.Single(_usage.ReadSessions(TenantOne, T0.AddDays(-1)));
        Assert.Equal((0.0, false), (row.WeightedTotal, row.HasBaseline));
    }

    [Fact]
    public void TheRecord_IsTheCallersAccountsAlone()
    {
        _usage.Observe(TenantOne, new[] { Seen("s1", 10, 0) }, T0);

        Assert.Empty(_usage.ReadSessions(TenantTwo, T0.AddDays(-1)));
    }

    [Fact]
    public void RecordReadings_KeepsAReadingOnlyWhenItSaysSomethingNew_OrTenMinutesHavePassed()
    {
        LicenseUsageReadingRecord R(int minutes, double week) => new(A, T0.AddMinutes(minutes), week, T0.AddDays(4), 5, T0.AddHours(2));

        Assert.Equal(1, _usage.RecordReadings(TenantOne, new[] { R(0, 40) }));
        Assert.Equal(0, _usage.RecordReadings(TenantOne, new[] { R(0, 40) }));  // the same reading again
        Assert.Equal(0, _usage.RecordReadings(TenantOne, new[] { R(2, 40) }));  // nothing new, two minutes on
        Assert.Equal(1, _usage.RecordReadings(TenantOne, new[] { R(4, 41) }));  // the week rose
        Assert.Equal(1, _usage.RecordReadings(TenantOne, new[] { R(15, 41) })); // nothing new, but ten minutes on

        Assert.Equal(new[] { 40.0, 41, 41 }, _usage.ReadReadings(TenantOne, T0.AddDays(-1)).Select(r => r.SevenDayUtilization!.Value));
    }

    [Fact]
    public void Prune_DeletesSamplesAndReadingsOlderThanAWeekAndADay()
    {
        _usage.Observe(TenantOne, new[] { Seen("s1", 0, 0) }, T0);
        _usage.Observe(TenantOne, new[] { Seen("s1", 100, 0) }, T0.AddMinutes(1));
        _usage.RecordReadings(TenantOne, new[] { new LicenseUsageReadingRecord(A, T0, 40, T0.AddDays(4), 5, null) });

        _usage.Prune(TenantOne, T0.AddDays(9));

        Assert.Empty(_usage.UseSince(TenantOne, T0.AddDays(-1)));
        Assert.Empty(_usage.ReadReadings(TenantOne, T0.AddDays(-1)));
        Assert.Single(_usage.ReadSessions(TenantOne, T0.AddDays(-1))); // the session's total stays in the record
    }

    // ---- the recorder's observations ----

    [Fact]
    public void Observations_PutAClaudeSessionOnTheAccountItsDirectorsMachineIsOn_AndAnyOtherAgentOnNone()
    {
        _licenses.ApplyReport(TenantOne, "dir-1", 1, Report("machine-one", Holding(A, true), Holding(B, false)), T0, TimeSpan.Zero);
        var overview = new LicenseReadModel(_licenses).Read(TenantOne, T0);
        var live = new List<(string, SessionDto)>
        {
            ("DIR-1", new SessionDto { SessionId = "s1", Agent = "ClaudeCode", Number = 101, ClaudeSessionId = "conv-1", ActivityState = "Working" }),
            ("dir-1", new SessionDto { SessionId = "s2", Agent = "Codex", ActivityState = "Idle", AssessedState = "WaitingForInput" }),
            ("dir-9", new SessionDto { SessionId = "s3", Agent = "ClaudeCode" }),
        };

        var seen = SessionUsageRecorder.Observations(live, overview);

        Assert.Equal((A, "claude", "conv-1", "Working"), (seen[0].AccountEmail, seen[0].AgentKind, seen[0].AgentConversationId, seen[0].State));
        Assert.Equal((null, "codex", "WaitingForInput"), (seen[1].AccountEmail, seen[1].AgentKind, seen[1].State));
        Assert.Null(seen[2].AccountEmail); // a Director that reports no machine
    }

    // ---- the routes ----

    private SessionUsageService Service(IReadOnlySet<string> live) => new(_usage, _licenses, new LicenseReadModel(_licenses), _ => live);

    private static DefaultHttpContext Context(string query = "")
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Request.QueryString = new QueryString(query);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static (int Status, JsonElement Json) Read(DefaultHttpContext ctx, IResult result)
    {
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public void Usage_AnswersTheExactShapeThePageAndTheCommandAreBuiltFrom()
    {
        _licenses.ApplyReport(TenantOne, "dir-1", 1, Report("machine-one", Holding(A, true, sevenDay: 40), Holding(B, false)), T0, TimeSpan.Zero);
        _usage.Observe(TenantOne, new[] { Seen("s1", 0, 0) }, T0.AddMinutes(-90));
        _usage.Observe(TenantOne, new[] { Seen("s1", 1_000_000, 0) }, T0.AddMinutes(-30));
        _usage.RecordReadings(TenantOne, new[]
        {
            new LicenseUsageReadingRecord(A, T0.AddMinutes(-100), 38, T0.AddDays(4), 5, null),
            new LicenseUsageReadingRecord(A, T0.AddMinutes(-20), 40, T0.AddDays(4), 5, null),
        });
        var ctx = Context();

        var (status, json) = Read(ctx, LicenseEndpoints.Usage(ctx, _ => TenantOne, Service(new HashSet<string> { "s1" }), () => T0));

        Assert.Equal(200, status);
        Assert.Equal(new[]
        {
            "asOfUtc", "accountEmail", "accountInUse", "accounts", "emptyText", "calibration", "unitText", "weekUsedPercent",
            "weekResetsAtUtc", "burnPercentPerHour", "burnText", "monthlyPriceUsd", "dollarsPerPercent", "sessions", "topLevel", "missions",
        }, Names(json));
        Assert.Equal(new[] { "isMeasured", "percentPerMillionWeighted", "fromUtc", "toUtc", "risePercent", "weighted", "text" }, Names(json.GetProperty("calibration")));
        var session = json.GetProperty("sessions")[0];
        Assert.Equal(new[]
        {
            "sessionId", "number", "name", "missionId", "missionName", "topLevelSessionId", "isLive", "state", "stateText", "machineName",
            "model", "agentKind", "rateNowPercentPerHour", "last4HoursPercent", "soFarPercent", "runningHours", "averagePercentPerHour",
            "dollarsSoFar", "weightedLastHour", "weightedLast4Hours", "weightedSoFar", "startedAtUtc", "lastSeenAtUtc",
        }, Names(session));
        Assert.Equal(new[]
        {
            "id", "number", "label", "sessionIds", "liveSessionIds", "rateNowPercentPerHour", "last4HoursPercent", "soFarPercent", "dollarsSoFar",
        }, Names(json.GetProperty("topLevel")[0]));
        // The week rose 2 points over 80 minutes while the session used a million: 2% a million.
        Assert.Equal((A, true, 2.0), (json.GetProperty("accountEmail").GetString(), json.GetProperty("calibration").GetProperty("isMeasured").GetBoolean(),
            json.GetProperty("calibration").GetProperty("percentPerMillionWeighted").GetDouble()));
        Assert.Equal(2.0, session.GetProperty("rateNowPercentPerHour").GetDouble(), 6);
    }

    [Fact]
    public void Usage_ForAnAccountThisDevThrottleAccountDoesNotHold_Is404()
    {
        _licenses.ApplyReport(TenantOne, "dir-1", 1, Report("machine-one", Holding(A, true)), T0, TimeSpan.Zero);
        var ctx = Context("?account=nobody@example.com");

        var (status, json) = Read(ctx, LicenseEndpoints.Usage(ctx, _ => TenantOne, Service(new HashSet<string>()), () => T0));

        Assert.Equal((404, "unknown_account"), (status, json.GetProperty("code").GetString()));
    }

    [Fact]
    public void Usage_WithNoAccountBound_IsRefused()
    {
        var ctx = Context();
        var (status, _) = Read(ctx, LicenseEndpoints.Usage(ctx, _ => null, Service(new HashSet<string>()), () => T0));
        Assert.Equal(403, status);
    }

    [Fact]
    public void Usage_ARefusedClosedFlag_Is400()
    {
        var ctx = Context("?closed=maybe");
        var (status, json) = Read(ctx, LicenseEndpoints.Usage(ctx, _ => TenantOne, Service(new HashSet<string>()), () => T0));
        Assert.Equal((400, "invalid_closed"), (status, json.GetProperty("code").GetString()));
    }

    [Fact]
    public void SetMonthlyPrice_StoresWholeCents_AndNullClearsIt()
    {
        _licenses.ApplyReport(TenantOne, "dir-1", 1, Report("machine-one", Holding(A, true)), T0, TimeSpan.Zero);
        var ctx = Context();

        var (status, json) = Read(ctx, LicenseEndpoints.SetMonthlyPrice(ctx, _ => null, _ => TenantOne, _licenses,
            new LicensePriceRequest { AccountEmail = "Account-A@Example.com", MonthlyPriceUsd = 199.999 }, priceGiven: true));

        Assert.Equal((200, A, 200.0), (status, json.GetProperty("accountEmail").GetString(), json.GetProperty("monthlyPriceUsd").GetDouble()));
        Assert.Equal(20000, _licenses.ReadMonthlyPrices(TenantOne)[A]);

        var clear = Context();
        Read(clear, LicenseEndpoints.SetMonthlyPrice(clear, _ => null, _ => TenantOne, _licenses,
            new LicensePriceRequest { AccountEmail = A, MonthlyPriceUsd = null }, priceGiven: true));
        Assert.Empty(_licenses.ReadMonthlyPrices(TenantOne));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void SetMonthlyPrice_OutsideTheRange_Is400(double usd)
    {
        _licenses.ApplyReport(TenantOne, "dir-1", 1, Report("machine-one", Holding(A, true)), T0, TimeSpan.Zero);
        var ctx = Context();
        var (status, json) = Read(ctx, LicenseEndpoints.SetMonthlyPrice(ctx, _ => null, _ => TenantOne, _licenses,
            new LicensePriceRequest { AccountEmail = A, MonthlyPriceUsd = usd }, priceGiven: true));
        Assert.Equal((400, "invalid_price"), (status, json.GetProperty("code").GetString()));
    }

    [Fact]
    public void SetMonthlyPrice_ByAnyoneButTheOwner_IsRefusedBeforeAnythingIsStored()
    {
        _licenses.ApplyReport(TenantOne, "dir-1", 1, Report("machine-one", Holding(A, true)), T0, TimeSpan.Zero);
        var ctx = Context();
        var (status, _) = Read(ctx, LicenseEndpoints.SetMonthlyPrice(ctx, _ => Results.Json(new { code = "owner_only" }, statusCode: 403), _ => TenantOne, _licenses,
            new LicensePriceRequest { AccountEmail = A, MonthlyPriceUsd = 200 }, priceGiven: true));

        Assert.Equal(403, status);
        Assert.Empty(_licenses.ReadMonthlyPrices(TenantOne));
    }

    [Fact]
    public void PriceGiven_TellsAPriceLeftOutFromAPriceGivenAsNull()
    {
        Assert.True(LicenseEndpoints.PriceGiven("{\"accountEmail\":\"a@example.com\",\"monthlyPriceUsd\":null}"));
        Assert.False(LicenseEndpoints.PriceGiven("{\"accountEmail\":\"a@example.com\"}"));
        Assert.False(LicenseEndpoints.PriceGiven("not json"));
    }
}
