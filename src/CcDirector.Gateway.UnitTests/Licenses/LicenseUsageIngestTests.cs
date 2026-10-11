using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE USAGE REPORT GATE (License Switching, contract sections 2.1 and 5.2): only the active connection, only a higher
/// sequence from that connection, never a malformed report; a skewed clock's ages are judged from the receive time.
/// </summary>
public sealed class LicenseUsageIngestTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseUsageIngest _ingest;

    private static readonly TenantId Tenant = new("acct-a");

    public LicenseUsageIngestTests()
    {
        _store = new LicenseStore(_harness.Open());
        _ingest = new LicenseUsageIngest(_store);
    }

    public void Dispose() => _harness.Dispose();

    private LicenseIngestOutcome Push(long seq, double fiveHour, string conn = "conn-1", bool active = true)
        => _ingest.Ingest(Tenant, "dir-1", conn, active, seq, Report("m1", Holding(A, true, fiveHour)), T0);

    private double? StoredFiveHour() => new LicenseReadModel(_store).Read(Tenant, T0).Accounts.SingleOrDefault()?.Freshest?.FiveHourUtilization;

    [Fact]
    public void Ingest_ActiveConnectionHigherSequence_IsAccepted()
    {
        Assert.Equal(LicenseIngestOutcome.Accepted, Push(1, 10));
        Assert.Equal(LicenseIngestOutcome.Accepted, Push(2, 20));
        Assert.Equal(20, StoredFiveHour());
    }

    [Fact]
    public void Ingest_NotTheActiveConnection_IsRefused_AndStoresNothing()
    {
        Assert.Equal(LicenseIngestOutcome.NotActiveConnection, Push(1, 10, active: false));
        Assert.Null(StoredFiveHour());
    }

    [Fact]
    public void Ingest_SameOrLowerSequence_IsRefused_AndKeepsTheNewerNumbers()
    {
        Push(5, 50);

        Assert.Equal(LicenseIngestOutcome.StaleSequence, Push(5, 60));
        Assert.Equal(LicenseIngestOutcome.StaleSequence, Push(4, 40));
        Assert.Equal(50, StoredFiveHour());
    }

    [Fact]
    public void Ingest_ANewConnection_StartsAFreshSequenceBaseline()
    {
        Push(50, 50);

        Assert.Equal(LicenseIngestOutcome.Accepted, Push(1, 15, conn: "conn-2"));
        Assert.Equal(15, StoredFiveHour());
    }

    [Theory]
    [InlineData("two-active")]
    [InlineData("held-twice")]
    [InlineData("no-machine")]
    [InlineData("no-email")]
    [InlineData("bad-switch-id")]
    public void Ingest_MalformedReport_IsRefused(string fault)
    {
        var report = Report("m1", Holding(A, true), Holding(B, false));
        switch (fault)
        {
            case "two-active": report.Holdings[1].IsActive = true; break;
            case "held-twice": report.Holdings[1].AccountEmail = "ACCOUNT-A@example.com"; break;
            case "no-machine": report.MachineName = " "; break;
            case "no-email": report.Holdings[1].AccountEmail = ""; break;
            case "bad-switch-id": report.SwitchInProgressId = "not-a-switch-id"; break;
        }

        Assert.Equal(LicenseIngestOutcome.Malformed, _ingest.Ingest(Tenant, "dir-1", "conn-1", true, 1, report, T0));
        Assert.Null(StoredFiveHour());
    }

    [Fact]
    public void Ingest_NullReport_IsRefused()
        => Assert.Equal(LicenseIngestOutcome.Malformed, _ingest.Ingest(Tenant, "dir-1", "conn-1", true, 1, null, T0));

    [Fact]
    public void ReadAtShift_WithinTwoMinutes_IsZero()
        => Assert.Equal(TimeSpan.Zero, _ingest.ReadAtShift(Tenant, Report("m1", T0.AddMinutes(-2)), T0));

    [Fact]
    public void ReadAtShift_SkewedClock_JudgesAgeFromTheReceiveTime()
    {
        // The machine's clock is an hour behind: it says it read at 02:10 and sent at 02:12; the Gateway got it at 03:12.
        var report = Report("m1", T0.AddHours(-1), Holding(A, true, readAt: T0.AddHours(-1).AddMinutes(-2)));

        _ingest.Ingest(Tenant, "dir-1", "conn-1", true, 1, report, T0);

        var reading = new LicenseReadModel(_store).Read(Tenant, T0).Accounts[0];
        Assert.Equal(T0.AddMinutes(-2), reading.Freshest!.JudgedReadAtUtc);
        Assert.False(reading.IsStale);
    }
}
