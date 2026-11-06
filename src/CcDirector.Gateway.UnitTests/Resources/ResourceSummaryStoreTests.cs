using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static CcDirector.Gateway.UnitTests.Resources.ResourceWindows;

namespace CcDirector.Gateway.UnitTests.Resources;

/// <summary>
/// The Gateway's resource summary store and its hourly roll-up (Mission: Resource Monitor, section 6), on a statistics
/// database built by the real migration chain. Every clock is a fake: the roll-up runs because the fake clock reached
/// its hour, never because time passed.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceSummaryStoreTests : IDisposable
{
    private static readonly DateTime W0 = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId Tenant = new("tenant-a");
    private const string Director = "director-1";

    private readonly ResourceSummaryTestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(W0.AddMinutes(30)));

    public void Dispose() => _db.Dispose();

    private ResourceSummaryStore NewStore() => new(() => _db.Factory, _clock);

    [Fact]
    public void Store_MachineAndSessionRows_AreStoredAndReadBack()
    {
        var store = NewStore();
        var window = Steady(W0, 35, 9 * Gb, "devlinux",
            new ResourceSummarySessionSample("s-1", "Marketing", 12, 2 * Gb, null),
            new ResourceSummarySessionSample("s-2", null, 3, 1 * Gb, null));

        var ack = store.Store(Tenant, Director, Batch(window));

        Assert.Null(ack.NotStoredReason);
        Assert.Equal(W0, Assert.Single(ack.HeldWindowStartsUtc));
        var row = Assert.Single(store.MachineWindows(Tenant, W0, W0.AddHours(1))!);
        Assert.Equal("devlinux", row.MachineName);
        Assert.Equal(W0, row.WindowStartUtc);
        Assert.Equal(DateTimeKind.Utc, row.WindowStartUtc.Kind);
        Assert.Equal(W0, row.HourStartUtc);
        Assert.Equal(35, row.ProcessorAveragePercent);
        Assert.Equal(9 * Gb, row.MemoryPeakUsedBytes);
        Assert.Equal(16 * Gb, row.MemoryTotalBytes);
        Assert.Equal(60, row.SampleCount);
        var sessions = store.SessionWindows(Tenant, "DEVLINUX", W0, W0.AddHours(1))!;
        Assert.Equal(new[] { "s-1", "s-2" }, sessions.Select(s => s.SessionId).OrderBy(s => s));
        Assert.Equal("Marketing", sessions.Single(s => s.SessionId == "s-1").Factory);
        Assert.Null(sessions.Single(s => s.SessionId == "s-1").MemoryTotalBytes);
    }

    [Fact]
    public void Store_NinetySecondRedSpike_TheStoredRowStillShowsIt()
    {
        var store = NewStore();

        store.Store(Tenant, Director, Batch(WithRedSpike(W0)));

        var row = Assert.Single(store.MachineWindows(Tenant, W0, W0.AddHours(1))!);
        Assert.InRange(row.ProcessorAveragePercent!.Value, 43, 45);
        Assert.Equal(99, row.ProcessorPeakPercent);
        var crossing = Assert.Single(ResourceSummaryStore.CrossingsOf(row));
        Assert.Equal(ResourceSummaryContract.Processor, crossing.Resource);
        Assert.Equal(ResourceSummaryContract.Red, crossing.Level);
        Assert.Equal(90, crossing.Seconds);
        Assert.Equal(W0.AddMinutes(2), crossing.StartedUtc);
    }

    [Fact]
    public void Store_TheSameWindowTwice_IsStoredOnceAndHeldBothTimes()
    {
        var store = NewStore();
        store.Store(Tenant, Director, Batch(Steady(W0, 10, 4 * Gb)));

        // The Director lost the first answer and sends it again, with a later window behind it.
        var ack = store.Store(Tenant, Director, Batch(Steady(W0, 90, 4 * Gb), Steady(W0.AddMinutes(5), 10, 4 * Gb)));

        Assert.Equal(new[] { W0, W0.AddMinutes(5) }, ack.HeldWindowStartsUtc);
        var rows = store.MachineWindows(Tenant, W0, W0.AddHours(1))!;
        Assert.Equal(2, rows.Count);
        Assert.Equal(10, rows[0].ProcessorAveragePercent);
    }

    [Fact]
    public void Store_AnotherTenantsRows_AreNeverRead()
    {
        var store = NewStore();
        store.Store(new TenantId("tenant-b"), Director, Batch(Steady(W0, 10, 4 * Gb)));

        Assert.Empty(store.MachineWindows(Tenant, W0, W0.AddHours(1))!);
    }

    [Fact]
    public void Store_MalformedWindows_AreRefusedForGoodAndTheRestStored()
    {
        var store = NewStore();
        var offBoundary = Steady(W0, 10, 4 * Gb);
        offBoundary.WindowStartUtc = W0.AddMinutes(1);
        var noMachine = Steady(W0.AddMinutes(5), 10, 4 * Gb);
        noMachine.MachineName = " ";
        var future = Steady(W0.AddHours(2), 10, 4 * Gb);

        var ack = store.Store(Tenant, Director, Batch(offBoundary, noMachine, future, Steady(W0.AddMinutes(10), 10, 4 * Gb)));

        Assert.Equal(3, ack.RejectedWindowStartsUtc.Count);
        Assert.Equal(W0.AddMinutes(10), Assert.Single(ack.HeldWindowStartsUtc));
        Assert.Single(store.MachineWindows(Tenant, W0, W0.AddHours(3))!);
    }

    /// <summary>
    /// Figures no machine can produce are refused, not stored and not tidied into range: a probe that reports a
    /// per-core 400% on a four-core machine is a defect to surface, and a clamp to 100 would hide it.
    /// </summary>
    [Theory]
    [InlineData("processor 400")]
    [InlineData("processor below zero")]
    [InlineData("peak below average")]
    [InlineData("memory above total")]
    [InlineData("session processor 400")]
    [InlineData("crossing of an unknown resource")]
    public void Store_ImpossibleFigures_AreRefusedForGood(string defect)
    {
        var store = NewStore();
        var window = Steady(W0, 30, 8 * Gb, "devlinux", new ResourceSummarySessionSample("s-1", null, 10, 1 * Gb, null));
        switch (defect)
        {
            case "processor 400": window.Machine.ProcessorPeakPercent = 400; break;
            case "processor below zero": window.Machine.ProcessorAveragePercent = -1; break;
            case "peak below average": window.Machine.ProcessorPeakPercent = 10; break;
            case "memory above total": window.Machine.MemoryPeakUsedBytes = 17 * Gb; break;
            case "session processor 400": window.Sessions[0].Figures.ProcessorPeakPercent = 400; break;
            case "crossing of an unknown resource":
                window.Machine.Crossings.Add(new ResourceCrossing { Resource = "disk", Level = ResourceSummaryContract.Red, StartedUtc = W0, Seconds = 60 });
                break;
        }

        var ack = store.Store(Tenant, Director, Batch(window));

        Assert.Equal(W0, Assert.Single(ack.RejectedWindowStartsUtc));
        Assert.Empty(ack.HeldWindowStartsUtc);
        Assert.Empty(store.MachineWindows(Tenant, W0, W0.AddHours(1))!);
    }

    [Fact]
    public void Store_StatisticsStoreNotOpen_AnswersNotStoredWithTheReason()
    {
        var store = new ResourceSummaryStore(() => null, _clock);

        var ack = store.Store(Tenant, Director, Batch(Steady(W0, 10, 4 * Gb)));

        Assert.Equal("the Gateway's statistics store is not open", ack.NotStoredReason);
        Assert.Empty(ack.HeldWindowStartsUtc);
        Assert.Null(store.MachineWindows(Tenant, W0, W0.AddHours(1)));
    }

    [Fact]
    public void Store_UnreadableProcessor_IsStoredAsNullWithTheReason()
    {
        var store = NewStore();
        var window = Steady(W0, 10, 4 * Gb);
        window.Machine.ProcessorCouldRead = false;
        window.Machine.ProcessorUnreadableReason = "the performance counter is missing";

        store.Store(Tenant, Director, Batch(window));

        var row = Assert.Single(store.MachineWindows(Tenant, W0, W0.AddHours(1))!);
        Assert.Null(row.ProcessorAveragePercent);
        Assert.Null(row.ProcessorPeakPercent);
        Assert.Equal("the performance counter is missing", row.ProcessorUnreadableReason);
    }

    [Fact]
    public void RollupJob_OnTheHour_RollsTheHourUp_WeightedAndWithEveryCrossing()
    {
        var store = NewStore();
        // Two windows of the 10:00 hour: one quiet at 20%, one at 20% with a red spike to 99%. And one of 11:00, sent
        // once that window has closed.
        _clock.SetUtcNow(new DateTimeOffset(W0.AddMinutes(66)));
        store.Store(Tenant, Director, Batch(Steady(W0, 20, 4 * Gb), WithRedSpike(W0.AddMinutes(5)), Steady(W0.AddHours(1), 50, 6 * Gb)));
        using var job = new ResourceSummaryRollupJob(new ResourceSummaryRollup(() => _db.Factory, _clock), _clock);
        job.Start();
        Assert.Equal(0, job.Runs);

        _clock.Advance(ResourceSummaryRollupJob.Period);

        Assert.Equal(1, job.Runs);
        Assert.Equal(2, job.LastResult!.HoursRolled);
        using var db = _db.Open();
        var hour = db.ResourceHours.Single(h => h.HourStartUtc == W0);
        Assert.Equal(2, hour.WindowCount);
        Assert.Equal(120, hour.SampleCount);
        Assert.Equal(99, hour.ProcessorPeakPercent);
        var expectedAverage = (20.0 * 60 + (20.0 * 42 + 99.0 * 18)) / 120;
        Assert.Equal(expectedAverage, hour.ProcessorAveragePercent!.Value, 6);
        Assert.Equal(ResourceSummaryContract.Red, Assert.Single(ResourceSummaryStore.CrossingsOf(hour)).Level);
        Assert.Single(db.ResourceHours.Where(h => h.HourStartUtc == W0.AddHours(1)));
    }

    [Fact]
    public void RollupJob_SessionHours_AreRolledWithTheirFactory()
    {
        var store = NewStore();
        var share = new ResourceSummarySessionSample("s-1", "Biographies", 30, 2 * Gb, null);
        store.Store(Tenant, Director, Batch(Steady(W0, 40, 4 * Gb, "devlinux", share), Steady(W0.AddMinutes(5), 40, 4 * Gb, "devlinux", share)));
        using var job = new ResourceSummaryRollupJob(new ResourceSummaryRollup(() => _db.Factory, _clock), _clock);
        job.Start();

        _clock.Advance(ResourceSummaryRollupJob.Period);

        using var db = _db.Open();
        var sessionHour = db.ResourceSessionHours.Single();
        Assert.Equal("s-1", sessionHour.SessionId);
        Assert.Equal("Biographies", sessionHour.Factory);
        Assert.Equal(2, sessionHour.WindowCount);
        Assert.Equal(30, sessionHour.ProcessorAveragePercent);
    }

    [Fact]
    public void Rollup_AWindowArrivingAfterItsHourWasRolled_RollsThatHourAgain()
    {
        var store = NewStore();
        var rollup = new ResourceSummaryRollup(() => _db.Factory, _clock);
        store.Store(Tenant, Director, Batch(Steady(W0, 20, 4 * Gb)));
        // The run starts after the window arrived. (A run that starts on the very tick a window arrives cannot tell
        // whether it read that window, so it rolls the hour again next time - harmless, and the safe side.)
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, rollup.RunOnce().HoursRolled);
        Assert.Equal(0, rollup.RunOnce().HoursRolled);

        // A Director that was cut off sends a window of the same hour late.
        _clock.Advance(TimeSpan.FromMinutes(10));
        store.Store(Tenant, Director, Batch(Steady(W0.AddMinutes(5), 80, 4 * Gb)));
        Assert.Equal(1, rollup.RunOnce().HoursRolled);

        using var db = _db.Open();
        var hour = db.ResourceHours.Single();
        Assert.Equal(2, hour.WindowCount);
        Assert.Equal(50, hour.ProcessorAveragePercent);
    }

    /// <summary>
    /// Retention, run by the hourly job itself. The clock is moved with NO timer armed and then a job is started and its
    /// one hourly run fires, so each step is exactly one scheduled run at a known time - not thousands of hourly runs
    /// across two years of fake time. Successive steps must be at least one period apart: the fake clock only moves
    /// forward.
    /// </summary>
    private ResourceRollupResult OneScheduledRunAt(DateTime when)
    {
        _clock.SetUtcNow(new DateTimeOffset(when - ResourceSummaryRollupJob.Period));
        using var job = new ResourceSummaryRollupJob(new ResourceSummaryRollup(() => _db.Factory, _clock), _clock);
        job.Start();
        _clock.Advance(ResourceSummaryRollupJob.Period);
        Assert.Equal(1, job.Runs);
        return job.LastResult!;
    }

    [Fact]
    public void RollupJob_OnSchedule_DeletesFiveMinuteRowsAfter90Days_AndHourlyRowsAfterTwoYears()
    {
        var store = NewStore();
        var share = new ResourceSummarySessionSample("s-1", null, 10, 1 * Gb, null);
        store.Store(Tenant, Director, Batch(Steady(W0, 20, 4 * Gb, "devlinux", share)));

        // The first run rolls the hour up and deletes nothing.
        var first = OneScheduledRunAt(W0.AddHours(2));
        Assert.Equal(1, first.HoursRolled);
        Assert.Equal(0, first.WindowsDeleted);

        // Just inside 90 days the window is kept.
        Assert.Equal(0, OneScheduledRunAt(W0.AddDays(90)).WindowsDeleted);
        using (var db = _db.Open()) Assert.Equal(1, db.ResourceWindows.Count());

        // Past 90 days the five-minute rows go; the hourly rows stay.
        var past90 = OneScheduledRunAt(W0.AddDays(90).AddHours(1));
        Assert.Equal(1, past90.WindowsDeleted);
        Assert.Equal(1, past90.SessionWindowsDeleted);
        Assert.Equal(0, past90.HoursDeleted);
        using (var db = _db.Open())
        {
            Assert.Equal(0, db.ResourceWindows.Count());
            Assert.Equal(0, db.ResourceSessionWindows.Count());
            Assert.Equal(1, db.ResourceHours.Count());
            Assert.Equal(1, db.ResourceSessionHours.Count());
        }

        // Just inside two years the hour is kept; past two years it goes.
        Assert.Equal(0, OneScheduledRunAt(W0.AddDays(730)).HoursDeleted);
        var pastTwoYears = OneScheduledRunAt(W0.AddDays(730).AddHours(1));
        Assert.Equal(1, pastTwoYears.HoursDeleted);
        Assert.Equal(1, pastTwoYears.SessionHoursDeleted);
        using (var db = _db.Open())
        {
            Assert.Equal(0, db.ResourceHours.Count());
            Assert.Equal(0, db.ResourceSessionHours.Count());
        }
    }

    [Fact]
    public void Rollup_AnHourNeverRolledBeforeItsWindowsExpire_IsRolledBeforeTheyAreDeleted()
    {
        // The Gateway was down for the whole 90 days: the first run it ever makes finds windows already past retention.
        var store = NewStore();
        store.Store(Tenant, Director, Batch(Steady(W0, 20, 4 * Gb)));
        _clock.Advance(TimeSpan.FromDays(100));

        var result = new ResourceSummaryRollup(() => _db.Factory, _clock).RunOnce();

        Assert.Equal(1, result.HoursRolled);
        Assert.Equal(1, result.WindowsDeleted);
        using var db = _db.Open();
        Assert.Equal(20, db.ResourceHours.Single().ProcessorAveragePercent);
    }

    [Fact]
    public void Rollup_StatisticsStoreNotOpen_DoesNotRunAndSaysWhy()
    {
        var result = new ResourceSummaryRollup(() => null, _clock).RunOnce();

        Assert.Equal("the Gateway's statistics store is not open", result.NotRunReason);
    }
}
