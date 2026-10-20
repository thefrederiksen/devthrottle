using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// PRODUCES THE TWO FORECAST EXAMPLES OF SECTION 3 OF missions/license-switching/cockpit-api.md (License Switching).
///
/// Like OverviewExample beside the overview example, it is in no project: produce_forecast_example.py copies it into
/// the Gateway's unit test project, runs these two methods, writes the answers into the document and takes the copy
/// away again. Each answer is the forecast route handler's own, over the real store on a throwaway SQLite file and a
/// usage history fed the same reports the store took. Nothing in either is typed.
///
/// <see cref="ProduceForTheOverviewPicture"/> is the picture of section 2: two machines and three accounts at
/// 2026-10-11 03:12:00 UTC, account A in use and rising on its 5-hour window.
///
/// <see cref="ProduceWithAGap"/> is the picture the outlook was designed from, with example accounts: at 2026-10-20
/// 15:22:00 UTC account A is in use at 44% of its weekly limit, rising 2.5% an hour; B is at 98% of its weekly limit until
/// 22 October 21:00, and its subscription renews today; C is at 100% until 24 October 18:59; D was last read two hours
/// ago. So when A runs out no other account has room until B's week resets.
/// </summary>
public sealed class ForecastExample
{
    private static readonly DateTime G0 = new(2026, 10, 20, 15, 22, 0, DateTimeKind.Utc);

    private static LicenseHolding Read(string email, bool active, double fiveHour, double week, DateTime fiveHourResets, DateTime weekResets,
        DateTime readAt, DateTime signedIn, DateTime expires)
        => new()
        {
            AccountEmail = email,
            IsActive = active,
            Plan = "max",
            RateLimitTier = "default_claude_max_20x",
            FiveHour = new LicenseWindow { Utilization = fiveHour, ResetsAtUtc = fiveHourResets },
            SevenDay = new LicenseWindow { Utilization = week, ResetsAtUtc = weekResets },
            ExtraUsage = new LicenseExtraUsage { IsEnabled = false },
            ReadAtUtc = readAt,
            SignedInAtUtc = signedIn,
            SignInExpiresAtUtc = expires,
        };

    private static async Task WriteForecast(LicenseStore store, LicenseUsageHistory history, TenantId tenant, DateTime now, string variable)
    {
        var target = Environment.GetEnvironmentVariable(variable);
        Assert.False(string.IsNullOrWhiteSpace(target), $"{variable} names no file: run produce_forecast_example.py");
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        var result = LicenseEndpoints.Forecast(ctx, _ => tenant, new LicenseSnapshotSource(store, new LicenseReadModel(store)), history, () => now);
        await result.ExecuteAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        using var answer = JsonDocument.Parse(text);
        File.WriteAllText(target!, JsonSerializer.Serialize(answer.RootElement, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static void Apply(LicenseStore store, LicenseUsageHistory history, TenantId tenant, string director, LicenseUsageReport report, DateTime now)
    {
        store.ApplyReport(tenant, director, 1, report, report.SentAtUtc, TimeSpan.Zero);
        history.Record(tenant, report, TimeSpan.Zero, now);
    }

    [Fact]
    public async Task ProduceForTheOverviewPicture()
    {
        using var harness = new GatewayDbTestHarness();
        var store = new LicenseStore(harness.Open());
        var history = new LicenseUsageHistory();
        var tenant = new TenantId("acct-one");
        var fiveHourResets = new DateTime(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc);
        var weekResets = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
        LicenseHolding At(string email, bool active, double fiveHour, double week, DateTime readAt, DateTime signedIn, DateTime expires)
            => Read(email, active, fiveHour, week, fiveHourResets, weekResets, readAt, signedIn, expires);

        // Twenty minutes before: A at 86.5% of its 5-hour window and 60% of its week, as the Windows machine read it. The
        // history holds that machine's readings only, so A has a rate on both windows and B and C have none yet.
        var earlier = T0.AddMinutes(-20);
        history.Record(tenant, Report("the-windows-machine", earlier, At(A, true, 86.5, 60, earlier, T0.AddDays(-21), T0.AddDays(9))), TimeSpan.Zero, T0);

        var macAt = T0.AddMinutes(-1);
        store.ApplyReport(tenant, "dir-mac", 1, Report("the-mac", macAt,
            At(A, true, 96, 61, macAt, T0.AddDays(-20), T0.AddDays(10)),
            At(B, false, 4, 22, T0.AddMinutes(-7), T0.AddDays(-5), T0.AddDays(25))), macAt, TimeSpan.Zero);
        Apply(store, history, tenant, "dir-windows", Report("the-windows-machine", T0,
            At(A, true, 96, 61, T0.AddSeconds(-2), T0.AddDays(-21), T0.AddDays(9)),
            At(B, false, 4, 22, T0.AddMinutes(-9), T0.AddDays(-28), T0.AddDays(2).AddHours(5)),
            At(C, false, 99, 40, T0.AddMinutes(-9), T0.AddDays(-1), T0.AddDays(29))), T0);

        await WriteForecast(store, history, tenant, T0, "LICENSE_FORECAST_EXAMPLE_OUT");
    }

    [Fact]
    public async Task ProduceWithAGap()
    {
        using var harness = new GatewayDbTestHarness();
        var db = harness.Open();
        var store = new LicenseStore(db);
        var history = new LicenseUsageHistory();
        var tenant = new TenantId("acct-one");
        const string D = "account-d@example.com";
        var fiveHourResets = new DateTime(2026, 10, 20, 18, 0, 0, DateTimeKind.Utc);
        var aWeek = new DateTime(2026, 10, 25, 21, 59, 0, DateTimeKind.Utc);
        var bWeek = new DateTime(2026, 10, 22, 21, 0, 0, DateTimeKind.Utc);
        var cWeek = new DateTime(2026, 10, 24, 18, 59, 0, DateTimeKind.Utc);
        var dWeek = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        // Half an hour before: A at 42.75% of its weekly limit. Now 44%: 2.5% an hour.
        var earlier = G0.AddMinutes(-30);
        history.Record(tenant, Report("the-windows-machine", earlier,
            Read(A, true, 12, 42.75, fiveHourResets, aWeek, earlier, G0.AddDays(-12), G0.AddDays(18))), TimeSpan.Zero, G0);

        Apply(store, history, tenant, "dir-mac", Report("the-mac", G0.AddMinutes(-1),
            Read(A, true, 12, 44, fiveHourResets, aWeek, G0.AddMinutes(-1), G0.AddDays(-11), G0.AddDays(19)),
            Read(B, false, 3, 98, fiveHourResets, bWeek, G0.AddMinutes(-6), G0.AddDays(-4), G0.AddDays(26))), G0);
        Apply(store, history, tenant, "dir-windows", Report("the-windows-machine", G0,
            Read(A, true, 12, 44, fiveHourResets, aWeek, G0.AddSeconds(-3), G0.AddDays(-12), G0.AddDays(18)),
            Read(B, false, 3, 98, fiveHourResets, bWeek, G0.AddMinutes(-8), G0.AddDays(-9), G0.AddDays(21)),
            Read(C, false, 0, 100, fiveHourResets, cWeek, G0.AddMinutes(-8), G0.AddDays(-2), G0.AddDays(28)),
            Read(D, false, 0, 30, fiveHourResets, dWeek, G0.AddHours(-2), G0.AddDays(-10), G0.AddDays(20))), G0);
        Assert.True(store.SetRenewalDate(tenant, B, new DateOnly(2026, 10, 20)));
        Assert.True(store.SetRenewalDate(tenant, C, new DateOnly(2026, 11, 3)));

        await WriteForecast(store, history, tenant, G0, "LICENSE_OUTLOOK_EXAMPLE_OUT");

        // And the overview of the same picture, so the cards drawn under the timeline are the same accounts.
        var target = Environment.GetEnvironmentVariable("LICENSE_OUTLOOK_OVERVIEW_EXAMPLE_OUT");
        Assert.False(string.IsNullOrWhiteSpace(target), "LICENSE_OUTLOOK_OVERVIEW_EXAMPLE_OUT names no file: run produce_forecast_example.py");
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        var result = LicenseEndpoints.Overview(ctx, _ => tenant, new LicenseSnapshotSource(store, new LicenseReadModel(store)), () => G0,
            new LicenseApproverStore(new TenantSettingsStore(db)), new LicenseSignInLog(db), (_, _) => false,
            LicenseReadinessTestKit.ForReading(db, store, (_, _) => false, () => G0));
        await result.ExecuteAsync(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        using var answer = JsonDocument.Parse(Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
        File.WriteAllText(target!, JsonSerializer.Serialize(answer.RootElement, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
