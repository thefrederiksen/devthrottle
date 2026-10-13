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
        // Four writers on one account, and four readers that read while the writers write. Without the lock a reader
        // copies a list while a writer inserts into it or drops its oldest point: the copy throws, or comes out torn -
        // a point twice, or one more than the most kept. With the lock every copy is whole, in order, and each read
        // time is in it once.
        //
        // The overlap is arranged, not hoped for. A writer does not stop until it has written its share AND every
        // reader has finished its share of reads, so each of those reads is made while all four writers are still
        // writing - however the machine schedules the eight of them. Each runs on a thread of its own, so none
        // waits in the pool's queue behind the others. Nothing here waits on a clock.
        //
        // The writers' read times interleave (writer 0 writes ticks 1, 5, 9; writer 1 writes 2, 6, 10), so every
        // write lands among the newest points and pushes the oldest out: the list is being shifted all the time.
        var history = new LicenseUsageHistory();
        const int writerCount = 4;
        const int readerCount = 4;
        const int writesEach = 10000;
        const int readsEach = 2000;
        var now = T0.AddHours(1); // later than any read time written, and inside the two hours kept
        using var go = new ManualResetEventSlim();
        using var readersDone = new CountdownEvent(readerCount);

        var writers = Enumerable.Range(0, writerCount).Select(w => Task.Factory.StartNew(() =>
        {
            go.Wait();
            long written = 0;
            while (written < writesEach || !readersDone.IsSet)
            {
                history.Record(TenantA, Report("m" + w, Holding(A, true, 1, readAt: T0.AddTicks(written * writerCount + w + 1))), TimeSpan.Zero, now);
                written++;
            }
            return written;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var readers = Enumerable.Range(0, readerCount).Select(_ => Task.Factory.StartNew(() =>
        {
            go.Wait();
            try
            {
                for (var read = 0; read < readsEach; read++)
                {
                    foreach (var account in history.Read(TenantA, now))
                    {
                        var times = account.FiveHour.Select(p => p.AtUtc).ToList();
                        Assert.True(times.Count <= LicenseUsageHistory.MaxPointsPerWindow, $"a reader was handed {times.Count} points, more than the most kept");
                        for (var i = 1; i < times.Count; i++)
                            Assert.True(times[i - 1] < times[i], $"a reader was handed a series out of order or with a point twice, at index {i}");
                    }
                }
            }
            finally
            {
                readersDone.Signal(); // also when a read failed: the writers stop, and the failure is what the test reports
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        go.Set();
        await Task.WhenAll(readers);
        var written = await Task.WhenAll(writers);

        Assert.All(written, count => Assert.True(count >= writesEach));
        var points = history.Read(TenantA, now).Single().FiveHour;
        Assert.Equal(LicenseUsageHistory.MaxPointsPerWindow, points.Count); // 40,000 or more written, the newest 600 kept
        var newestWritten = Enumerable.Range(0, writerCount).Max(w => (written[w] - 1) * writerCount + w + 1);
        Assert.Equal(T0.AddTicks(newestWritten), points[^1].AtUtc);
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
            ingest.Ingest(TenantA, "dir-1", "conn-1", 2, Report("m1", later, Holding(A, true, 99, readAt: later.AddSeconds(-1))), later));
        Assert.Equal(LicenseIngestOutcome.NotActiveConnection,
            ingest.Ingest(TenantA, "dir-1", "conn-other", 9, Report("m1", later, Holding(A, true, 99, readAt: later.AddSeconds(-2))), later));

        var points = history.Read(TenantA, later).Single().FiveHour;
        Assert.Equal(new[] { 50d, 60d }, points.Select(p => p.Utilization));
        Assert.Equal(60d, LicenseForecaster.RatePerHour(points)!.Value, 6); // ten percent in ten minutes
    }
}
