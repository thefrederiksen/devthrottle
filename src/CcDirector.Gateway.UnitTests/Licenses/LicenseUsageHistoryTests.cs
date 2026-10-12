using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// The recent readings the forecast takes its rates from (License Switching): two hours per tenant and account, in
/// memory, fed by every ACCEPTED usage report and by nothing else.
/// </summary>
public sealed class LicenseUsageHistoryTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly TenantId Tenant = new("acct-a");
    private static readonly TenantId Other = new("acct-b");

    private static LicenseUsageReport ReadingAt(DateTime at, double fiveHour, double sevenDay = 20)
        => Report("the-windows-machine", at, Holding(A, true, fiveHour, sevenDay, at));

    [Fact]
    public void Record_KeepsEachReadingOncePerWindow_OldestFirst()
    {
        var history = new LicenseUsageHistory();
        history.Record(Tenant, ReadingAt(T0, 10, 20), T0, TimeSpan.Zero);
        history.Record(Tenant, ReadingAt(T0.AddMinutes(2), 14, 21), T0.AddMinutes(2), TimeSpan.Zero);
        // A failed read repeats the previous numbers with their old read time: the same point, kept once.
        history.Record(Tenant, ReadingAt(T0.AddMinutes(2), 14, 21), T0.AddMinutes(4), TimeSpan.Zero);

        var account = Assert.Single(history.Read(Tenant, T0.AddMinutes(4)));

        Assert.Equal(A, account.AccountEmail);
        Assert.Equal(new[] { new LicenseUsagePoint(T0, 10), new LicenseUsagePoint(T0.AddMinutes(2), 14) }, account.FiveHour);
        Assert.Equal(new[] { new LicenseUsagePoint(T0, 20), new LicenseUsagePoint(T0.AddMinutes(2), 21) }, account.SevenDay);
        // The series is what the forecast's rate is taken from: four percent in two minutes.
        Assert.Equal(120, LicenseForecaster.RatePerHour(account.FiveHour));
    }

    [Fact]
    public void Read_DropsReadingsOlderThanTwoHours()
    {
        var history = new LicenseUsageHistory();
        history.Record(Tenant, ReadingAt(T0, 10), T0, TimeSpan.Zero);
        history.Record(Tenant, ReadingAt(T0.AddMinutes(90), 40), T0.AddMinutes(90), TimeSpan.Zero);

        var atTwoHours = Assert.Single(history.Read(Tenant, T0 + LicenseUsageHistory.Kept));
        var justAfter = Assert.Single(history.Read(Tenant, T0 + LicenseUsageHistory.Kept + TimeSpan.FromSeconds(1)));

        Assert.Equal(2, atTwoHours.FiveHour.Count);
        Assert.Equal(new[] { new LicenseUsagePoint(T0.AddMinutes(90), 40) }, justAfter.FiveHour);
        Assert.Empty(history.Read(Tenant, T0.AddHours(4)));
    }

    [Fact]
    public void Record_PutsAPointWhereTheGatewayJudgesTheRead_NotWhereAWrongClockPutIt()
    {
        var history = new LicenseUsageHistory();
        // The machine's clock is five minutes behind: the ingest judges its reads five minutes later.
        history.Record(Tenant, ReadingAt(T0.AddMinutes(-5), 10), T0, TimeSpan.FromMinutes(5));

        Assert.Equal(T0, Assert.Single(Assert.Single(history.Read(Tenant, T0)).FiveHour).AtUtc);
    }

    [Fact]
    public void Record_ARead_TimeAheadOfTheGatewaysClock_IsNotKept()
    {
        var history = new LicenseUsageHistory();
        history.Record(Tenant, ReadingAt(T0.AddDays(30), 10), T0, TimeSpan.Zero);

        Assert.Empty(history.Read(Tenant, T0));
        Assert.Empty(history.Read(Tenant, T0.AddDays(30)));
    }

    [Fact]
    public void Read_OneTenantNeverSeesAnothers()
    {
        var history = new LicenseUsageHistory();
        history.Record(Tenant, ReadingAt(T0, 10), T0, TimeSpan.Zero);

        Assert.Empty(history.Read(Other, T0));
    }

    [Fact]
    public void Ingest_FeedsTheHistoryFromAnAcceptedReport_AndNotFromARefusedOne()
    {
        var history = new LicenseUsageHistory();
        var ingest = new LicenseUsageIngest(new LicenseStore(_harness.Open()), history);
        ingest.RegisterConnection(Tenant, "dir-1", "conn-1");

        var accepted = ingest.Ingest(Tenant, "dir-1", "conn-1", 1, ReadingAt(T0, 10), T0);
        var refused = ingest.Ingest(Tenant, "dir-1", "conn-other", 2, ReadingAt(T0.AddMinutes(2), 90), T0.AddMinutes(2));

        Assert.Equal((LicenseIngestOutcome.Accepted, LicenseIngestOutcome.NotActiveConnection), (accepted, refused));
        var point = Assert.Single(Assert.Single(history.Read(Tenant, T0.AddMinutes(2))).FiveHour);
        Assert.Equal(new LicenseUsagePoint(T0, 10), point);
    }
}
