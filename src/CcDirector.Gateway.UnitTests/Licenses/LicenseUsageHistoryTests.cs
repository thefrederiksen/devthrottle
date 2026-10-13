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
    public void Record_TheSameReadingSentAgain_AddsNothing_EvenWhenTheClockCorrectionDiffersEachTime()
    {
        // A machine whose clock is wrong: the correction is the receive time less the sent time, and it differs by
        // milliseconds from one report to the next. The reading is the same one - its own read time has not changed.
        var history = new LicenseUsageHistory();
        for (var i = 0; i < 5; i++)
            history.Record(TenantA, Report("m1", Holding(A, true, 50, readAt: T0, readError: i == 0 ? null : "network")),
                TimeSpan.FromMinutes(7) + TimeSpan.FromMilliseconds(i * 13), T0.AddMinutes(8 + 2 * i));

        var points = history.Read(TenantA, T0.AddMinutes(16)).Single().FiveHour;

        Assert.Single(points);
        Assert.Null(LicenseForecaster.RatePerHour(points)); // one reading shows no rate: "No rate yet", never "Not rising"
    }

    [Fact]
    public void Record_AReadingStampedInTheFuture_IsKeptAtNow_AndAgesOutLikeAnyOther()
    {
        var history = new LicenseUsageHistory();
        history.Record(TenantA, Report("m1", Holding(A, true, 50, readAt: T0.AddYears(10))), TimeSpan.Zero, T0);

        Assert.Equal(T0, history.Read(TenantA, T0).Single().FiveHour.Single().AtUtc);
        Assert.Single(history.Read(TenantA, T0 + LicenseUsageHistory.Kept));
        Assert.Empty(history.Read(TenantA, T0 + LicenseUsageHistory.Kept + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Record_AReadingAtExactlyNow_KeepsItsOwnTime()
    {
        var history = new LicenseUsageHistory();
        history.Record(TenantA, Report("m1", Holding(A, true, 50, readAt: T0)), TimeSpan.Zero, T0);

        Assert.Equal(T0, history.Read(TenantA, T0).Single().FiveHour.Single().AtUtc);
    }

    [Fact]
    public void Record_MoreAccountsThanTheMostKept_DropsTheOnesHeardFromLongestAgo()
    {
        // The reviewer's case: report after report, each naming new accounts with readings stamped years ahead.
        var history = new LicenseUsageHistory();
        var now = T0;
        for (var report = 0; report < 50; report++)
        {
            now = T0.AddMinutes(report);
            var holdings = Enumerable.Range(0, LicenseUsageIngest.MaxHoldings)
                .Select(i => Holding($"account-{report:000}-{i:00}@example.com", false, 1, readAt: T0.AddYears(10).AddSeconds(report * 100 + i)))
                .ToArray();
            history.Record(TenantA, Report("m1", holdings), TimeSpan.Zero, now);
        }

        var kept = history.Read(TenantA, now);

        Assert.Equal(LicenseUsageHistory.MaxAccountsPerTenant, kept.Count);
        Assert.Contains(kept, h => h.AccountEmail.StartsWith("account-049-", StringComparison.Ordinal));  // the newest stay
        Assert.DoesNotContain(kept, h => h.AccountEmail.StartsWith("account-000-", StringComparison.Ordinal)); // the oldest went
        Assert.Empty(history.Read(TenantA, now.AddDays(3))); // and three days later nothing is left at all
    }

    [Fact]
    public void Record_ExactlyTheMostAccountsKept_DropsNone()
    {
        var history = new LicenseUsageHistory();
        for (var i = 0; i < LicenseUsageHistory.MaxAccountsPerTenant; i++)
            history.Record(TenantA, Report("m1", Holding($"account-{i:000}@example.com", false, 1, readAt: T0.AddSeconds(i))), TimeSpan.Zero, T0.AddSeconds(i));

        Assert.Equal(LicenseUsageHistory.MaxAccountsPerTenant, history.Read(TenantA, T0.AddSeconds(LicenseUsageHistory.MaxAccountsPerTenant)).Count);
    }

    [Fact]
    public async Task RecordAndRead_FromSeveralThreadsAtOnce_NeverSeeAHalfWrittenSeries()
    {
        // Four writers on one account, and four readers that read for as long as the writers write. Without the
        // lock a reader copies a list while a writer inserts into it: the copy throws, or comes out torn. With it
        // every copy is whole and in order. The readers stop when the writers do, so nothing here waits on a clock.
        var history = new LicenseUsageHistory();
        const int perWriter = 25000;
        using var go = new ManualResetEventSlim();
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            go.Wait();
            for (var i = 0; i < perWriter; i++)
                history.Record(TenantA, Report("m" + w, Holding(A, true, 1, readAt: T0.AddTicks(w * perWriter + i + 1))), TimeSpan.Zero, T0.AddMinutes(1));
        })).ToArray();
        var written = Task.WhenAll(writers);
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            go.Wait();
            var reads = 0;
            while (!written.IsCompleted)
            {
                foreach (var account in history.Read(TenantA, T0.AddMinutes(1)))
                {
                    var times = account.FiveHour.Select(p => p.AtUtc).ToList();
                    Assert.Equal(times.OrderBy(t => t), times);
                    Assert.True(times.Count <= LicenseUsageHistory.MaxPointsPerWindow);
                }
                reads++;
            }
            return reads;
        })).ToArray();

        go.Set();
        await written;
        var reads = await Task.WhenAll(readers);

        Assert.All(reads, count => Assert.True(count > 0, "a reader never read while the writers wrote"));
        var points = history.Read(TenantA, T0.AddMinutes(1)).Single().FiveHour;
        Assert.Equal(LicenseUsageHistory.MaxPointsPerWindow, points.Count); // 100,000 written, the newest 600 kept
        Assert.Equal(T0.AddTicks(4 * perWriter), points[^1].AtUtc);
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
