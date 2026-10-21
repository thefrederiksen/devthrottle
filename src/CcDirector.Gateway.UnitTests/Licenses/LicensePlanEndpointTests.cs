using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// GET /gateway/licenses/plan over the real services - the store, the kept readings, the forecaster and the planner
/// wired as the Gateway wires them - so a forecaster that stopped filling its weekly paces, or a query the route stopped
/// reading, fails here and not only in production. Account A is in use at 45% of its weekly limit, 72 hours into a week
/// that resets at T0 + 4 days, with kept readings of 9% six hours ago and 45% now: 6% an hour.
/// </summary>
public sealed class LicensePlanEndpointTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly SessionUsageStore _usage;
    private readonly LicensePlanService _plans;
    private static readonly TenantId Tenant = new("acct-a");

    public LicensePlanEndpointTests()
    {
        var db = _harness.Open();
        _store = new LicenseStore(db);
        _usage = new SessionUsageStore(db);
        var reads = new LicenseReadModel(_store);
        var service = new SessionUsageService(_usage, _store, reads, _ => new HashSet<string>());
        var history = new LicenseUsageHistory((tenant, since) => _usage.ReadReadings(tenant, since));
        _plans = new LicensePlanService(service, new LicenseSnapshotSource(_store, reads), history);

        _store.ApplyReport(Tenant, "dir-1", 1, Report("m1", Holding(A, true, 10, 45), Holding(B, false, 4, 22)), T0, TimeSpan.Zero);
        _usage.RecordReadings(Tenant, new[]
        {
            new LicenseUsageReadingRecord(A, T0.AddHours(-6), 9, T0.AddDays(4), 10, T0.AddHours(2)),
            new LicenseUsageReadingRecord(A, T0, 45, T0.AddDays(4), 10, T0.AddHours(2)),
        });
    }

    public void Dispose() => _harness.Dispose();

    private (int Status, JsonElement Json) Get(string query)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Request.QueryString = new QueryString(query);
        ctx.Response.Body = new MemoryStream();
        LicenseEndpoints.Plan(ctx, _ => Tenant, _plans, () => T0).ExecuteAsync(ctx).GetAwaiter().GetResult();
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public void Plan_UsesTheForecastersWeeklyPace_FromTheKeptReadings()
    {
        var (status, json) = Get("?until=reset");

        Assert.Equal(200, status);
        Assert.Equal("reset", json.GetProperty("targetId").GetString());
        // The last 6 hours (6% an hour) are faster than this week's average (45% over 72 hours): that is the burn.
        Assert.Equal(6.0, json.GetProperty("burnPercentPerHour").GetDouble(), 6);
        Assert.Equal(52, json.GetProperty("leftPercent").GetDouble(), 6);
        Assert.False(json.GetProperty("lasts").GetBoolean());
    }

    [Fact]
    public void Plan_ReadsStopAndStopGroup_CommaSeparated_EachIdOnce()
    {
        var (status, json) = Get("?stop=s2,s1,s1&stopGroup=top-1");

        Assert.Equal(200, status);
        var notFound = json.GetProperty("whatIf").GetProperty("notFound").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "s2", "s1", "top-1" }, notFound);
    }

    [Fact]
    public void Plan_TakesAnIsoTime_InUtcUnlessItNamesAZone()
    {
        var (_, utc) = Get("?until=" + Uri.EscapeDataString(T0.AddHours(10).ToString("yyyy-MM-ddTHH:mm")));
        var (_, zoned) = Get("?until=" + Uri.EscapeDataString(T0.AddHours(10).ToString("yyyy-MM-ddTHH:mm") + "+02:00"));

        Assert.Equal(T0.AddHours(10), utc.GetProperty("targetAtUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(T0.AddHours(8), zoned.GetProperty("targetAtUtc").GetDateTime().ToUniversalTime());
    }

    [Theory]
    [InlineData("?until=18:00")]
    [InlineData("?until=next%20tuesday")]
    [InlineData("?until=room:nobody@example.com")]
    public void Plan_RefusesAnUntilThatIsNeitherATargetNorAnIsoTime(string query)
    {
        var (status, json) = Get(query);

        Assert.Equal(400, status);
        Assert.Equal("invalid_until", json.GetProperty("code").GetString());
    }

    [Fact]
    public void Plan_RefusesTooManyIds()
    {
        var ids = string.Join(",", Enumerable.Range(0, LicenseEndpoints.MaxWhatIfIds + 1).Select(i => $"s{i}"));
        var (status, json) = Get("?stop=" + ids);

        Assert.Equal(400, status);
        Assert.Equal("too_many_ids", json.GetProperty("code").GetString());
    }

    [Fact]
    public void Plan_AnAccountNoMachineHolds_Is404()
    {
        var (status, json) = Get("?account=nobody%40example.com");

        Assert.Equal(404, status);
        Assert.Equal("unknown_account", json.GetProperty("code").GetString());
    }
}
