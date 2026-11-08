using System.Text.Json;
using CcDirector.Core.Machine;
using CcDirector.Core.Machine.ResourceSummaries;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static CcDirector.Gateway.UnitTests.Resources.ResourceWindows;

namespace CcDirector.Gateway.UnitTests.Resources;

/// <summary>
/// Disk Space on the Meter on the Gateway (the owner's answers of 7 November 2026): each window's drives are stored and
/// read back, a Director older than that - which sends no drives - is still accepted, the statistics migration adds
/// the column and the notice table without touching existing rows, the owner is told in Your inbox when a drive goes
/// red (at most once a day per drive) and when it recovers, and the Cockpit's machine page gets each drive's words.
/// Every clock is a fake.
/// </summary>
[Trait("Category", "Resources")]
public sealed class DiskSpaceGatewayTests : IDisposable
{
    private static readonly DateTime W0 = new(2026, 11, 7, 15, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId Tenant = new("tenant-a");
    private const string Director = "director-1";

    private readonly ResourceSummaryTestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(W0.AddHours(2)));

    public void Dispose() => _db.Dispose();

    private ResourceSummaryStore NewStore() => new(() => _db.Factory, _clock);

    /// <summary>A whole window in which drive C: holds <paramref name="freeGb"/> GB free throughout, and D: is roomy.</summary>
    private static ResourceSummaryWindow WithDrives(DateTime start, double freeGb, string machine = "SOREN_NORTH") =>
        ResourceSummaryBuilder.Build(start, machine, Enumerable.Range(0, 60).Select(i =>
            new ResourceSummarySample(start.AddSeconds(5 * i), 20, null, ResourceLevel.Normal, 8 * Gb, 16 * Gb, null,
                ResourceLevel.Normal, [], null,
                [
                    new ResourceSummaryDriveSample("C:", 931 * Gb, (long)(freeGb * Gb), null),
                    new ResourceSummaryDriveSample("D:", 3815 * Gb, 325 * Gb, null),
                ])));

    // ---- the store ----

    [Fact]
    public void Store_TheWindowsDrives_AreStoredAndReadBack()
    {
        var store = NewStore();

        var ack = store.Store(Tenant, Director, Batch(WithDrives(W0, 6.4)));

        Assert.Empty(ack.RejectedWindowStartsUtc);
        var row = Assert.Single(store.MachineWindows(Tenant, W0, W0.AddHours(1))!);
        Assert.Null(row.RefusedReason);
        var drives = ResourceSummaryStore.DrivesOf(row);
        Assert.Equal(new[] { "C:", "D:" }, drives.Select(d => d.Name));
        Assert.Equal((long)(6.4 * Gb), drives[0].FreeBytesAtEnd);
        Assert.Equal(931 * Gb, drives[0].TotalBytes);
        Assert.Equal(ResourceSummaryContract.Red, Assert.Single(drives[0].Crossings).Level);
    }

    [Fact]
    public void Store_AnOlderDirectorsWindow_WithNoDrivesInItsJson_IsAcceptedUnchanged()
    {
        // What an older Director actually sends: the same message with no "drives" property at all.
        var json = JsonSerializer.Serialize(Batch(Steady(W0, 30, 4 * Gb)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var withoutDrives = json.Replace(",\"drives\":[]", "", StringComparison.Ordinal);
        Assert.DoesNotContain("drives", withoutDrives, StringComparison.Ordinal);
        var batch = JsonSerializer.Deserialize<ResourceSummaryBatch>(withoutDrives, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var store = NewStore();

        var ack = store.Store(Tenant, Director, batch);

        Assert.Equal(W0, Assert.Single(ack.HeldWindowStartsUtc));
        Assert.Empty(ack.RejectedWindowStartsUtc);
        var row = Assert.Single(store.MachineWindows(Tenant, W0, W0.AddHours(1))!);
        Assert.Null(row.RefusedReason);
        Assert.Equal(30, row.ProcessorAveragePercent);
        Assert.Equal("[]", row.DrivesJson);
        Assert.Empty(ResourceSummaryStore.DrivesOf(row));
    }

    [Theory]
    [InlineData("free above total")]
    [InlineData("lowest above end")]
    [InlineData("unreadable with figures")]
    [InlineData("twice")]
    [InlineData("crossing for memory")]
    public void Store_ImpossibleDriveFigures_TheWindowsFiguresAreRefused(string what)
    {
        var window = WithDrives(W0, 50);
        var c = window.Drives[0];
        switch (what)
        {
            case "free above total": c.FreeBytesAtEnd = c.TotalBytes + 1; break;
            case "lowest above end": c.LowestFreeBytes = c.FreeBytesAtEnd + 1; break;
            case "unreadable with figures": c.CouldRead = false; break;
            case "twice": window.Drives.Add(window.Drives[0]); break;
            case "crossing for memory":
                c.Crossings.Add(new CcDirector.Gateway.Contracts.ResourceCrossing { Resource = ResourceSummaryContract.Memory, Level = "red", StartedUtc = W0, Seconds = 5 });
                break;
        }
        var store = NewStore();

        store.Store(Tenant, Director, Batch(window));

        var row = Assert.Single(store.MachineWindows(Tenant, W0, W0.AddHours(1))!);
        Assert.StartsWith("the drive figures are impossible", row.RefusedReason, StringComparison.Ordinal);
        Assert.Equal("[]", row.DrivesJson);
    }

    // ---- the migration ----

    [Fact]
    public void Migration_ARowStoredBeforeIt_GetsNoDrives_AndTheNoticeTableAppears()
    {
        var path = Path.Combine(Path.GetTempPath(), "cc-disk-space-migration-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<GatewayStatsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString()).Options;
        try
        {
            using (var db = new GatewayStatsDbContext(options))
            {
                db.GetInfrastructure().GetRequiredService<IMigrator>().Migrate("20261106160918_ResourceSummaryTables");
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO resource_window (tenant, director_id, window_start_utc, hour_start_utc, machine_name, window_seconds, "
                    + "sample_count, received_utc, processor_samples, memory_samples, crossings_json) VALUES "
                    + "('tenant-a', 'director-1', '2026-11-07 15:00:00', '2026-11-07 15:00:00', 'SOREN_NORTH', 300, 60, "
                    + "'2026-11-07 15:05:00', 60, 60, '[]')");
            }

            using (var db = new GatewayStatsDbContext(options))
            {
                db.Database.Migrate();
                var row = db.ResourceWindows.AsNoTracking().Single();
                Assert.Equal("[]", row.DrivesJson);
                Assert.Empty(db.ResourceDiskNotices.ToList());
                Assert.Empty(db.Database.GetPendingMigrations());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    // ---- the notice rules ----

    private static ResourceDriveSummary Drive(double lowestGb, double endGb) => new()
    {
        Name = "C:",
        CouldRead = true,
        TotalBytes = 931 * Gb,
        LowestFreeBytes = (long)(lowestGb * Gb),
        FreeBytesAtEnd = (long)(endGb * Gb),
    };

    [Fact]
    public void Judge_GoingRed_IsOneRedNotice_AndStayingRedIsNothing()
    {
        var (afterFirst, first) = DiskSpaceNoticeRules.Judge(DiskSpaceNoticeState.Never, Drive(6, 6), W0);
        var (_, second) = DiskSpaceNoticeRules.Judge(afterFirst, Drive(5, 5), W0.AddMinutes(5));

        Assert.Equal(DiskSpaceNoticeKind.Red, first);
        Assert.Equal(new DiskSpaceNoticeState(true, W0, true), afterFirst);
        Assert.Null(second);
    }

    [Fact]
    public void Judge_BackAboveTheRedLine_AfterARedNotice_IsOneRecovery()
    {
        var (red, _) = DiskSpaceNoticeRules.Judge(DiskSpaceNoticeState.Never, Drive(6, 6), W0);

        var (recovered, notice) = DiskSpaceNoticeRules.Judge(red, Drive(9, 40), W0.AddHours(1));

        Assert.Equal(DiskSpaceNoticeKind.Recovered, notice);
        Assert.False(recovered.IsRed);
    }

    [Fact]
    public void Judge_RedAgainWithinADay_IsNotTold_AndNeitherIsItsRecovery()
    {
        var (red, _) = DiskSpaceNoticeRules.Judge(DiskSpaceNoticeState.Never, Drive(6, 6), W0);
        var (clear, _) = DiskSpaceNoticeRules.Judge(red, Drive(12, 12), W0.AddHours(1));

        var (redAgain, again) = DiskSpaceNoticeRules.Judge(clear, Drive(8, 8), W0.AddHours(5));
        var (_, recovery) = DiskSpaceNoticeRules.Judge(redAgain, Drive(30, 30), W0.AddHours(6));

        Assert.Null(again);
        Assert.True(redAgain.IsRed);
        Assert.Null(recovery);
    }

    [Fact]
    public void Judge_RedAgainAfterADay_IsToldAgain()
    {
        var (red, _) = DiskSpaceNoticeRules.Judge(DiskSpaceNoticeState.Never, Drive(6, 6), W0);
        var (clear, _) = DiskSpaceNoticeRules.Judge(red, Drive(12, 12), W0.AddHours(1));

        var (_, again) = DiskSpaceNoticeRules.Judge(clear, Drive(8, 8), W0.AddDays(1));

        Assert.Equal(DiskSpaceNoticeKind.Red, again);
    }

    [Fact]
    public void Judge_AmberIsNotANotice_AndAnUnreadableDriveIsNotJudged()
    {
        var (amber, notice) = DiskSpaceNoticeRules.Judge(DiskSpaceNoticeState.Never, Drive(20, 20), W0);
        Assert.Null(notice);
        Assert.False(amber.IsRed);

        var (red, _) = DiskSpaceNoticeRules.Judge(DiskSpaceNoticeState.Never, Drive(6, 6), W0);
        var (stillRed, unread) = DiskSpaceNoticeRules.Judge(red, new ResourceDriveSummary { Name = "C:", CouldRead = false, UnreadableReason = "x" }, W0.AddMinutes(5));
        Assert.Null(unread);
        Assert.Equal(red, stillRed);
    }

    [Fact]
    public void Words_SayTheDriveTheMachineAndTheFigures()
    {
        var (subject, body) = DiskSpaceNoticeRules.Words(DiskSpaceNoticeKind.Red, "SOREN_NORTH", Drive(6, 6.4), W0.AddMinutes(5));

        Assert.Equal("Disk space: drive C: on SOREN_NORTH is under 10 GB free", subject);
        Assert.StartsWith("Drive C: on SOREN_NORTH has 6.40 GB free of 931 GB, under the 10 GB red line, as of 2026-11-07 15:05 UTC. It fell as low as 6.00 GB", body, StringComparison.Ordinal);
        var (recoverySubject, _) = DiskSpaceNoticeRules.Words(DiskSpaceNoticeKind.Recovered, "SOREN_NORTH", Drive(9, 40), W0);
        Assert.Equal("Disk space: drive C: on SOREN_NORTH is back above 10 GB free", recoverySubject);
    }

    // ---- the notifier, on the real statistics database ----

    [Fact]
    public void Notifier_WritesOneRedMessage_AResendWritesNothing_AndTheRecoveryWritesOne()
    {
        var inbox = new List<YourInboxMessageEntity>();
        var notifier = new DiskSpaceNotifier(() => _db.Factory, (_, message) => inbox.Add(message), _clock);

        Assert.Equal(1, notifier.Observe(Tenant, [WithDrives(W0, 6.4)]));
        Assert.Equal(0, notifier.Observe(Tenant, [WithDrives(W0, 6.4)]));
        Assert.Equal(0, notifier.Observe(Tenant, [WithDrives(W0.AddMinutes(5), 6)]));
        Assert.Equal(1, notifier.Observe(Tenant, [WithDrives(W0.AddMinutes(10), 40)]));

        Assert.Equal(2, inbox.Count);
        Assert.Equal("Disk space: drive C: on SOREN_NORTH is under 10 GB free", inbox[0].Subject);
        Assert.Equal("Disk space: drive C: on SOREN_NORTH is back above 10 GB free", inbox[1].Subject);
        Assert.All(inbox, m =>
        {
            Assert.Equal(YourInboxWords.Senders.Gateway, m.FromKind);
            Assert.Equal(YourInboxWords.DiskSpaceSeat, m.Seat);
            Assert.Equal(YourInboxWords.States.Unread, m.State);
            Assert.Null(m.Factory);
        });
    }

    [Fact]
    public void Notifier_TheOncADayRule_SurvivesANewNotifier()
    {
        var inbox = new List<YourInboxMessageEntity>();
        new DiskSpaceNotifier(() => _db.Factory, (_, m) => inbox.Add(m), _clock).Observe(Tenant, [WithDrives(W0, 6)]);
        new DiskSpaceNotifier(() => _db.Factory, (_, m) => inbox.Add(m), _clock).Observe(Tenant, [WithDrives(W0.AddMinutes(5), 12)]);

        // A new notifier - a restarted Gateway - reads where the drive stands from the database, not from memory.
        var restarted = new DiskSpaceNotifier(() => _db.Factory, (_, m) => inbox.Add(m), _clock);
        Assert.Equal(0, restarted.Observe(Tenant, [WithDrives(W0.AddHours(2), 7)]));
        Assert.Equal(2, inbox.Count);
    }

    [Fact]
    public void Notifier_AnOlderDirectorsWindow_WritesNothing()
    {
        var inbox = new List<YourInboxMessageEntity>();
        var notifier = new DiskSpaceNotifier(() => _db.Factory, (_, m) => inbox.Add(m), _clock);

        Assert.Equal(0, notifier.Observe(Tenant, [Steady(W0, 30, 4 * Gb)]));
        Assert.Empty(inbox);
    }

    // ---- the Cockpit's machine page ----

    [Fact]
    public void Fold_EachDrive_HasTheGatewaysWordsToneTrendAndPoints()
    {
        var store = NewStore();
        store.Store(Tenant, Director, Batch(WithDrives(W0, 14.4), WithDrives(W0.AddMinutes(5), 10.4), WithDrives(W0.AddMinutes(10), 6.4)));

        var view = MachineResourceHistoryFold.Fold("SOREN_NORTH", 24, W0, W0.AddMinutes(15),
            store.MachineWindows(Tenant, MachineResourceHistoryFold.ReadFromUtc(W0), W0.AddMinutes(16)),
            store.SessionWindows(Tenant, "SOREN_NORTH", W0, W0.AddMinutes(16)));

        Assert.Null(view.NoDrivesMessage);
        Assert.Equal(new[] { "C:", "D:" }, view.Drives.Select(d => d.Name));
        var c = view.Drives[0];
        Assert.Equal("6.40 GB free of 931 GB", c.Headline);
        Assert.Equal("red, under 10 GB", c.LevelLabel);
        Assert.Equal(FleetTone.Bad, c.Tone);
        Assert.Equal("lost 8.00 GB in the last 10 minutes", c.TrendLabel);
        Assert.Equal(3, c.Points.Count);
        Assert.Equal((long)(6.4 * Gb), c.Points[^1].LowestFreeBytes);
        var d = view.Drives[1];
        Assert.Equal(FleetTone.Ok, d.Tone);
        Assert.Equal("steady", d.TrendLabel);
    }

    [Fact]
    public void Fold_OnlyOlderDirectorWindows_SaysNoDriveWasReported()
    {
        var store = NewStore();
        store.Store(Tenant, Director, Batch(Steady(W0, 30, 4 * Gb, "SOREN_NORTH")));

        var view = MachineResourceHistoryFold.Fold("SOREN_NORTH", 24, W0, W0.AddMinutes(10),
            store.MachineWindows(Tenant, MachineResourceHistoryFold.ReadFromUtc(W0), W0.AddMinutes(11)),
            store.SessionWindows(Tenant, "SOREN_NORTH", W0, W0.AddMinutes(11)));

        Assert.Empty(view.Drives);
        Assert.Contains("older than 7 November 2026", view.NoDrivesMessage, StringComparison.Ordinal);
    }
}
