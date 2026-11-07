using System;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The daily license check is a line of the daily report (mission "Licenses - Always Ready, Loud When Not"): the line the
/// check folds is added as given, and it reaches the email renderer with its own type word. The quiet daily line for a
/// license a machine merely does not hold is gone (the owner, 7 November 2026), and a test here proves it is not produced.
/// </summary>
public sealed class MorningReportLicenseFireDrillTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly DateTime Now = new(2026, 11, 7, 7, 0, 0, DateTimeKind.Utc);
    private static readonly MorningReportWindow Yesterday = MorningReportWindow.Resolve("2026-11-06", "UTC");

    public void Dispose() => _h.Dispose();

    private GatewayDatabase Db => _h.Open(new AsyncLocalTenantContext());

    [Fact]
    public void Build_TheDrillHasALine_ItIsAnAttentionItem_WithItsTypeWord()
    {
        var line = new LicenseFireDrillAttentionDto { Passed = true, Text = "Daily license check passed.", RanAtUtc = Now.AddHours(-1) };

        var report = new MorningReportBuilder(Db, utcNow: () => Now, licenseFireDrill: t => t == Alice ? line : null)
            .Build("someone@example.com", Alice, Yesterday);

        Assert.Same(line, Assert.Single(report.Attention.OfType<LicenseFireDrillAttentionDto>()));
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"type\":\"license-fire-drill\"", json);
        Assert.DoesNotContain("passedInARow", json);
    }

    // The quiet daily gap line is dropped: no attention type for it exists, and a report with a license line carries none.
    [Fact]
    public void Build_WithLicenses_HasNoQuietDailyGapLine()
    {
        var line = new LicenseFireDrillAttentionDto { Passed = false, Text = "Daily license check failed.", RanAtUtc = Now.AddHours(-1) };

        var report = new MorningReportBuilder(Db, utcNow: () => Now, licenseFireDrill: _ => line)
            .Build("someone@example.com", Alice, Yesterday);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("license-coverage-gaps", json);
        Assert.DoesNotContain(typeof(MorningAttentionTypes).GetFields(), f => (string?)f.GetValue(null) == "license-coverage-gaps");
        Assert.DoesNotContain(typeof(MorningAttentionItemDto).Assembly.GetTypes(), t => t.Name == "LicenseCoverageGapsAttentionDto");
        Assert.Single(report.Attention.OfType<LicenseFireDrillAttentionDto>());
    }

    [Fact]
    public void Build_NoLine_AddsNothing()
    {
        var report = new MorningReportBuilder(Db, utcNow: () => Now, licenseFireDrill: _ => null)
            .Build("someone@example.com", Alice, Yesterday);

        Assert.Empty(report.Attention.OfType<LicenseFireDrillAttentionDto>());
    }
}
