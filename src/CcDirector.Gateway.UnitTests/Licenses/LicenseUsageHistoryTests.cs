using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE USAGE HISTORY (License Switching): the short in-memory series of readings the forecast takes its rate from.
/// What these hold down: an accepted report adds one point per window at the judged read time; the same reading sent
/// again adds nothing; a point is kept for two hours and no longer; one account never reads another's; and only a
/// report the ingest ACCEPTED is recorded.
/// </summary>
public sealed class LicenseUsageHistoryTests
{
    private static readonly TenantId TenantA = new("acct-a");
    private static readonly TenantId TenantB = new("acct-b");

    [Fact]
    public void Record_AddsOnePointPerWindow_AtTheJudgedReadTime()
    {
        var history = new LicenseUsageHistory();
        var shift = TimeSpan.FromMinutes(7);

        history.Record(TenantA, Report("m1", Holding(A, true, 50, 20, readAt: T0), Holding(B, false, 4, 22, readAt: T0.AddMinutes(-5))), shift, T0.AddMinutes(8));

        var read = history.Read(TenantA, T0.AddMinutes(8));
        Assert.Equal(new[] { A, B }, read.Select(h => h.AccountEmail));
        Assert.Equal(new LicenseUsagePoint(T0 + shift, 50), Assert.Single(read[0].FiveHour));
        Assert.Equal(new LicenseUsagePoint(T0 + shift, 20), Assert.Single(read[0].SevenDay));
        Assert.Equal(new LicenseUsagePoint(T0.AddMinutes(-5) + shift, 4), Assert.Single(read[1].FiveHour));
    }

    [Fact]
    public void Record_TheSameReadingSentAgain_AddsNothing()
    {
        var history = new LicenseUsageHistory();
        history.Record(TenantA, Report("m1", Holding(A, true, 50, readAt: T0)), TimeSpan.Zero, T0);

        // A failed read: the next report carries the previous numbers and their old read time (contract 2.2).
        history.Record(TenantA, Report("m1", Holding(A, true, 50, readAt: T0, readError: "network")), TimeSpan.Zero, T0.AddMinutes(2));

        Assert.Single(history.Read(TenantA, T0.AddMinutes(2)).Single().FiveHour);
    }

    [Fact]
    public void Record_AHoldingNeverRead_OrWithAWindowMissing_AddsNoPointForIt()
    {
        var history = new LicenseUsageHistory();
        var never = Holding(B, false);
        never.ReadAtUtc = null;
        var noWeek = Holding(A, true, 50);
        noWeek.SevenDay = null;

        history.Record(TenantA, Report("m1", noWeek, never), TimeSpan.Zero, T0);

        var only = Assert.Single(history.Read(TenantA, T0));
        Assert.Equal(A, only.AccountEmail);
        Assert.Single(only.FiveHour);
        Assert.Empty(only.SevenDay);
    }

    [Fact]
    public void Record_ReadingsOutOfOrder_AreKeptOldestFirst()
    {
        var history = new LicenseUsageHistory();
        history.Record(TenantA, Report("m1", Holding(A, true, 60, readAt: T0.AddMinutes(10))), TimeSpan.Zero, T0.AddMinutes(10));
        history.Record(TenantA, Report("m2", Holding(A, false, 50, readAt: T0)), TimeSpan.Zero, T0.AddMinutes(10));
        history.Record(TenantA, Report("m1", Holding(A, true, 55, readAt: T0.AddMinutes(5))), TimeSpan.Zero, T0.AddMinutes(10));

        Assert.Equal(new[] { 50d, 55d, 60d }, history.Read(TenantA, T0.AddMinutes(10)).Single().FiveHour.Select(p => p.Utilization));
    }

    [Fact]
    public void Read_APointIsKeptForTwoHours_AndNoLonger()
    {
        var history = new LicenseUsageHistory();
        history.Record(TenantA, Report("m1", Holding(A, true, 50, readAt: T0)), TimeSpan.Zero, T0);

        Assert.Single(history.Read(TenantA, T0 + LicenseUsageHistory.Kept));
        Assert.Empty(history.Read(TenantA, T0 + LicenseUsageHistory.Kept + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Record_MoreThanTheMostPointsKept_DropsTheOldest()
    {
        var history = new LicenseUsageHistory();
        for (var i = 0; i <= LicenseUsageHistory.MaxPointsPerWindow; i++)
            history.Record(TenantA, Report("m1", Holding(A, true, 1, readAt: T0.AddSeconds(i))), TimeSpan.Zero, T0.AddSeconds(i));

        var points = history.Read(TenantA, T0.AddSeconds(LicenseUsageHistory.MaxPointsPerWindow)).Single().FiveHour;

        Assert.Equal(LicenseUsageHistory.MaxPointsPerWindow, points.Count);
        Assert.Equal(T0.AddSeconds(1), points[0].AtUtc);
    }

    [Fact]
    public void Read_OneAccount_NeverReadsAnothers()
    {
        var history = new LicenseUsageHistory();
        history.Record(TenantA, Report("m1", Holding(A, true, 50)), TimeSpan.Zero, T0);

        Assert.Empty(history.Read(TenantB, T0));
        Assert.Single(history.Read(TenantA, T0));
    }

    [Fact]
    public void Ingest_OnlyAnAcceptedReport_IsRecorded_AndTheForecastGetsItsRate()
    {
        using var harness = new GatewayDbTestHarness();
        var store = new LicenseStore(harness.Open());
        var history = new LicenseUsageHistory();
        var ingest = new LicenseUsageIngest(store, history);
        ingest.RegisterConnection(TenantA, "dir-1", "conn-1");
        var later = T0.AddMinutes(10);

        Assert.Equal(LicenseIngestOutcome.Accepted, ingest.Ingest(TenantA, "dir-1", "conn-1", 1, Report("m1", Holding(A, true, 50)), T0));
        Assert.Equal(LicenseIngestOutcome.Accepted,
            ingest.Ingest(TenantA, "dir-1", "conn-1", 2, Report("m1", later, Holding(A, true, 60, readAt: later)), later));
        // Refused twice over: an old sequence, and a connection that does not own the Director. Neither is recorded.
        Assert.Equal(LicenseIngestOutcome.StaleSequence,
            ingest.Ingest(TenantA, "dir-1", "conn-1", 2, Report("m1", later, Holding(A, true, 99, readAt: later.AddMinutes(1))), later));
        Assert.Equal(LicenseIngestOutcome.NotActiveConnection,
            ingest.Ingest(TenantA, "dir-1", "conn-other", 9, Report("m1", later, Holding(A, true, 99, readAt: later.AddMinutes(2))), later));

        var points = history.Read(TenantA, later).Single().FiveHour;
        Assert.Equal(new[] { 50d, 60d }, points.Select(p => p.Utilization));
        Assert.Equal(60d, LicenseForecaster.RatePerHour(points)!.Value, 6); // ten percent in ten minutes
    }
}
