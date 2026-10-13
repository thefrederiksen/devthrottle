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
/// PRODUCES THE EXAMPLE OF SECTION 2 OF missions/license-switching/cockpit-api.md (License Switching, phase 3).
///
/// It is not a test of the product and it is in no project: produce_overview_example.py beside it copies it into the
/// Gateway's unit test project, runs this one method, writes the answer into the document and takes the copy away
/// again. It lives in that project for the length of the run because the overview's route handler is internal to the
/// Gateway and that project is one the Gateway lets see it.
///
/// The picture is the one the document has always shown: two machines and three accounts, the Gateway's clock at
/// 2026-10-11 03:12:00 UTC, no approver set and no Director connected (a route handler over a store has no tunnel).
/// The answer is the route handler's own, over the real store on a throwaway SQLite file. Nothing in it is typed.
/// </summary>
public sealed class OverviewExample
{
    private static LicenseHolding Read(string email, bool active, double fiveHour, double week, DateTime readAt, DateTime signedIn, DateTime expires)
        => new()
        {
            AccountEmail = email,
            IsActive = active,
            Plan = "max",
            RateLimitTier = "default_claude_max_20x",
            FiveHour = new LicenseWindow { Utilization = fiveHour, ResetsAtUtc = new DateTime(2026, 10, 11, 5, 0, 0, DateTimeKind.Utc) },
            SevenDay = new LicenseWindow { Utilization = week, ResetsAtUtc = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc) },
            ExtraUsage = new LicenseExtraUsage { IsEnabled = false },
            ReadAtUtc = readAt,
            SignedInAtUtc = signedIn,
            SignInExpiresAtUtc = expires,
        };

    [Fact]
    public async Task Produce()
    {
        var target = Environment.GetEnvironmentVariable("LICENSE_OVERVIEW_EXAMPLE_OUT");
        Assert.False(string.IsNullOrWhiteSpace(target), "LICENSE_OVERVIEW_EXAMPLE_OUT names no file: run produce_overview_example.py");

        using var harness = new GatewayDbTestHarness();
        var db = harness.Open();
        var store = new LicenseStore(db);
        var tenant = new TenantId("acct-one");

        var macAt = T0.AddMinutes(-1);
        store.ApplyReport(tenant, "dir-mac", 1, Report("the-mac", macAt,
            Read(A, true, 96, 61, macAt, T0.AddDays(-20), T0.AddDays(10)),
            Read(B, false, 4, 22, T0.AddMinutes(-7), T0.AddDays(-5), T0.AddDays(25))), macAt, TimeSpan.Zero);
        store.ApplyReport(tenant, "dir-windows", 1, Report("the-windows-machine", T0,
            Read(A, true, 96, 61, T0.AddSeconds(-2), T0.AddDays(-21), T0.AddDays(9)),
            Read(B, false, 4, 22, T0.AddMinutes(-9), T0.AddDays(-28), T0.AddDays(2).AddHours(5)),
            Read(C, false, 99, 40, T0.AddMinutes(-9), T0.AddDays(-1), T0.AddDays(29))), T0, TimeSpan.Zero);

        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        var result = LicenseEndpoints.Overview(ctx, _ => tenant, new LicenseSnapshotSource(store, new LicenseReadModel(store)), () => T0,
            new LicenseApproverStore(new TenantSettingsStore(db)), new LicenseSignInLog(db), (_, _) => false);
        await result.ExecuteAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        using var answer = JsonDocument.Parse(text);
        File.WriteAllText(target!, JsonSerializer.Serialize(answer.RootElement, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
