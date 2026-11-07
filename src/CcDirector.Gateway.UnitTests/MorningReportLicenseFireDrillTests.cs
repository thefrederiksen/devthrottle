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
/// The license fire drill is a line of the daily report (mission "Licenses - Always Ready, Loud When Not", step 7): the
/// line the drill folds is added as given, and it reaches the email renderer with its own type word.
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
        var line = new LicenseFireDrillAttentionDto { Passed = true, Text = "License fire drill passed.", PassedInARow = 2, RanAtUtc = Now.AddHours(-1) };

        var report = new MorningReportBuilder(Db, utcNow: () => Now, licenseFireDrill: t => t == Alice ? line : null)
            .Build("someone@example.com", Alice, Yesterday);

        Assert.Same(line, Assert.Single(report.Attention.OfType<LicenseFireDrillAttentionDto>()));
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"type\":\"license-fire-drill\"", json);
        Assert.Contains("\"passedInARow\":2", json);
    }

    [Fact]
    public void Build_NoLine_AddsNothing()
    {
        var report = new MorningReportBuilder(Db, utcNow: () => Now, licenseFireDrill: _ => null)
            .Build("someone@example.com", Alice, Yesterday);

        Assert.Empty(report.Attention.OfType<LicenseFireDrillAttentionDto>());
    }
}
