using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats.Data;
using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats;

/// <summary>What one roll-up run did, or why it did not run.</summary>
public sealed record ResourceRollupResult(
    int HoursRolled,
    int WindowsDeleted,
    int SessionWindowsDeleted,
    int HoursDeleted,
    int SessionHoursDeleted,
    string? NotRunReason)
{
    public static ResourceRollupResult NotRun(string reason) => new(0, 0, 0, 0, 0, reason);
}

/// <summary>
/// The hourly roll-up and retention of the resource summaries (Mission: Resource Monitor, section 6): five-minute rows
/// are rolled into hourly rows, five-minute rows older than 90 days are deleted, and hourly rows older than two years
/// are deleted.
///
/// WHICH HOURS ARE ROLLED. Every hour that has a five-minute window the hour's roll-up has not yet seen: an hour with no
/// hourly row, or one with a window received at or after its row was rolled. So the current hour is rolled again as
/// its windows arrive, a Director catching up after an outage makes the hours it fills roll again, and an hour missed
/// because the Gateway was down for a day is rolled on the next run. Nothing depends on the run happening exactly on
/// the hour.
///
/// ROLL BEFORE DELETE. The roll-up runs first, so a five-minute row is never deleted before its hour holds it. Once a
/// window is past 90 days no Director can send it again (a Director buffers 24 hours), so its hour is final.
///
/// A roll-up REPLACES the hour's rows, computed afresh from all of its windows, so running it twice is harmless.
/// </summary>
public sealed class ResourceSummaryRollup
{
    private readonly Func<IDbContextFactory<GatewayStatsDbContext>?> _factory;
    private readonly TimeProvider _time;

    public ResourceSummaryRollup(Func<IDbContextFactory<GatewayStatsDbContext>?> factory, TimeProvider time)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Roll up every hour that needs it, then delete what is past its retention.</summary>
    public ResourceRollupResult RunOnce()
    {
        var factory = _factory();
        if (factory is null)
        {
            FileLog.Write("[ResourceSummaryRollup] RunOnce: not run - the statistics store is not open");
            return ResourceRollupResult.NotRun("the Gateway's statistics store is not open");
        }

        // Taken BEFORE anything is read, and written as each hour's rolled-at time: a window that arrives while this run
        // is reading is received at or after it, so the next run rolls its hour again.
        var runStarted = _time.GetUtcNow().UtcDateTime;
        FileLog.Write($"[ResourceSummaryRollup] RunOnce: start at {runStarted:O}");

        using var db = factory.CreateDbContext();

        var due = db.ResourceWindows
            .Where(w => !db.ResourceHours.Any(h => h.Tenant == w.Tenant && h.DirectorId == w.DirectorId
                                                   && h.HourStartUtc == w.HourStartUtc && h.RolledAtUtc > w.ReceivedUtc))
            .Select(w => new { w.Tenant, w.DirectorId, w.HourStartUtc })
            .Distinct()
            .ToList();

        foreach (var hour in due)
            RollHour(db, hour.Tenant, hour.DirectorId, ResourceSummaryStore.Utc(hour.HourStartUtc), runStarted);

        var windowCutoff = runStarted - ResourceSummaryStore.WindowRetention;
        var hourCutoff = runStarted - ResourceSummaryStore.HourRetention;
        var sessionWindowsDeleted = db.ResourceSessionWindows.Where(w => w.WindowStartUtc < windowCutoff).ExecuteDelete();
        var windowsDeleted = db.ResourceWindows.Where(w => w.WindowStartUtc < windowCutoff).ExecuteDelete();
        var sessionHoursDeleted = db.ResourceSessionHours.Where(h => h.HourStartUtc < hourCutoff).ExecuteDelete();
        var hoursDeleted = db.ResourceHours.Where(h => h.HourStartUtc < hourCutoff).ExecuteDelete();

        var result = new ResourceRollupResult(due.Count, windowsDeleted, sessionWindowsDeleted, hoursDeleted, sessionHoursDeleted, null);
        FileLog.Write($"[ResourceSummaryRollup] RunOnce: done - hours rolled={result.HoursRolled}, "
                      + $"five-minute rows deleted={result.WindowsDeleted} machine / {result.SessionWindowsDeleted} session, "
                      + $"hourly rows deleted={result.HoursDeleted} machine / {result.SessionHoursDeleted} session");
        return result;
    }

    private static void RollHour(GatewayStatsDbContext db, string tenant, string directorId, DateTime hourStart, DateTime rolledAt)
    {
        var windows = db.ResourceWindows
            .Where(w => w.Tenant == tenant && w.DirectorId == directorId && w.HourStartUtc == hourStart)
            .OrderBy(w => w.WindowStartUtc)
            .ToList();
        if (windows.Count == 0) return;
        var sessionWindows = db.ResourceSessionWindows
            .Where(w => w.Tenant == tenant && w.DirectorId == directorId && w.HourStartUtc == hourStart)
            .OrderBy(w => w.WindowStartUtc)
            .ToList();

        using var transaction = db.Database.BeginTransaction();
        db.ResourceHours.Where(h => h.Tenant == tenant && h.DirectorId == directorId && h.HourStartUtc == hourStart).ExecuteDelete();
        db.ResourceSessionHours.Where(h => h.Tenant == tenant && h.DirectorId == directorId && h.HourStartUtc == hourStart).ExecuteDelete();

        var machineHour = new ResourceHourEntity
        {
            Tenant = tenant,
            DirectorId = directorId,
            HourStartUtc = hourStart,
            MachineName = windows[^1].MachineName,
            WindowCount = windows.Count,
            SampleCount = windows.Sum(w => w.SampleCount),
            RolledAtUtc = rolledAt,
        };
        Combine(windows, machineHour);
        db.ResourceHours.Add(machineHour);

        foreach (var group in sessionWindows.GroupBy(w => w.SessionId, StringComparer.Ordinal))
        {
            var rows = group.ToList();
            var sessionHour = new ResourceSessionHourEntity
            {
                Tenant = tenant,
                DirectorId = directorId,
                HourStartUtc = hourStart,
                SessionId = group.Key,
                MachineName = rows[^1].MachineName,
                Factory = rows.Select(r => r.Factory).LastOrDefault(f => f is not null),
                WindowCount = rows.Count,
                SampleCount = rows.Sum(r => r.SampleCount),
                RolledAtUtc = rolledAt,
            };
            Combine(rows, sessionHour);
            db.ResourceSessionHours.Add(sessionHour);
        }

        db.SaveChanges();
        transaction.Commit();
    }

    /// <summary>
    /// Fold windows into one hour: averages weighted by the samples that read them, the highest peak, the largest total,
    /// and every crossing in time order. A figure no window could read stays null with the newest window's reason.
    /// </summary>
    internal static void Combine(IReadOnlyList<IResourceFigureColumns> windows, IResourceFigureColumns hour)
    {
        var processor = windows.Where(w => w.ProcessorAveragePercent.HasValue && w.ProcessorSamples > 0).ToList();
        hour.ProcessorSamples = processor.Sum(w => w.ProcessorSamples);
        hour.ProcessorAveragePercent = processor.Count == 0 ? null
            : processor.Sum(w => w.ProcessorAveragePercent!.Value * w.ProcessorSamples) / hour.ProcessorSamples;
        hour.ProcessorPeakPercent = windows.Max(w => w.ProcessorPeakPercent);
        hour.ProcessorUnreadableReason = processor.Count == 0
            ? windows.Select(w => w.ProcessorUnreadableReason).LastOrDefault(r => r is not null)
            : null;

        var memory = windows.Where(w => w.MemoryAverageUsedBytes.HasValue && w.MemorySamples > 0).ToList();
        hour.MemorySamples = memory.Sum(w => w.MemorySamples);
        hour.MemoryAverageUsedBytes = memory.Count == 0 ? null
            : (long)Math.Round(memory.Sum(w => (double)w.MemoryAverageUsedBytes!.Value * w.MemorySamples) / hour.MemorySamples);
        hour.MemoryPeakUsedBytes = windows.Max(w => w.MemoryPeakUsedBytes);
        hour.MemoryTotalBytes = windows.Max(w => w.MemoryTotalBytes);
        hour.MemoryUnreadableReason = memory.Count == 0
            ? windows.Select(w => w.MemoryUnreadableReason).LastOrDefault(r => r is not null)
            : null;

        var crossings = windows
            .SelectMany(ResourceSummaryStore.CrossingsOf)
            .OrderBy(c => c.StartedUtc)
            .ToList();
        hour.CrossingsJson = JsonSerializer.Serialize(crossings, ResourceSummaryStore.CrossingJson);
    }
}

/// <summary>
/// Runs <see cref="ResourceSummaryRollup"/> every hour on the Gateway's clock. The timer callback is a boundary: a
/// failed run is logged and the next hour's run tries again; runs never overlap.
/// </summary>
public sealed class ResourceSummaryRollupJob : IDisposable
{
    /// <summary>How often the roll-up runs.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromHours(1);

    private readonly ResourceSummaryRollup _rollup;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private int _running;
    private int _runs;

    public ResourceSummaryRollupJob(ResourceSummaryRollup rollup, TimeProvider time)
    {
        _rollup = rollup ?? throw new ArgumentNullException(nameof(rollup));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>How many runs have finished, successful or not.</summary>
    public int Runs => Volatile.Read(ref _runs);

    /// <summary>The last run's result, or null before the first run finishes or when it failed.</summary>
    public ResourceRollupResult? LastResult { get; private set; }

    /// <summary>Start the hourly timer: the first run one period from now, then every period.</summary>
    public void Start()
    {
        if (_timer is not null) return;
        FileLog.Write($"[ResourceSummaryRollupJob] Start: rolling up resource summaries every {Period.TotalMinutes:F0} minutes");
        _timer = _time.CreateTimer(_ => Tick(), null, Period, Period);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            FileLog.Write("[ResourceSummaryRollupJob] Tick: SKIPPED, the previous run is still going");
            return;
        }
        try
        {
            LastResult = _rollup.RunOnce();
        }
        catch (Exception ex)
        {
            LastResult = null;
            FileLog.Write($"[ResourceSummaryRollupJob] Tick FAILED (the next hour's run tries again): {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Increment(ref _runs);
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
