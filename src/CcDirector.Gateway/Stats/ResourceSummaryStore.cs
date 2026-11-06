using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats.Data;
using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// Where the Directors' five-minute resource summaries are kept (Mission: Resource Monitor, section 6), in the
/// statistics database's <c>gateway_stats</c> schema, keyed by tenant.
///
/// STORED ONCE. A window is identified by (tenant, Director, window start). A Director re-sends every window it has
/// not seen acknowledged, so after a lost answer the same window arrives twice; the second is answered "held" and
/// written nowhere. That is what makes the Director's catch-up safe to repeat.
///
/// THE STATISTICS STORE MAY NOT BE OPEN. On a hosted Gateway it opens after the port is bound and can be unavailable
/// for good. The factory is asked on every call, and when there is none the batch is answered "not stored" with the
/// reason, so the Director keeps every window and sends it again - never a silent drop.
///
/// The tenant is always the caller's, taken from the Director's connection binding; nothing in a batch can name one.
/// </summary>
public sealed class ResourceSummaryStore
{
    /// <summary>Five-minute rows are kept this long, then deleted by <see cref="ResourceSummaryRollup"/>.</summary>
    public static readonly TimeSpan WindowRetention = TimeSpan.FromDays(90);

    /// <summary>Hourly rows are kept this long, then deleted by <see cref="ResourceSummaryRollup"/>.</summary>
    public static readonly TimeSpan HourRetention = TimeSpan.FromDays(730);

    /// <summary>How far ahead of the Gateway's clock a window may end before it is refused as from the future. Allows
    /// for a Director clock that runs a little fast.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(10);

    internal static readonly JsonSerializerOptions CrossingJson = new(JsonSerializerDefaults.Web);

    private readonly Func<IDbContextFactory<GatewayStatsDbContext>?> _factory;
    private readonly TimeProvider _time;

    /// <param name="factory">Asked on every call: the statistics context factory, or null while the store is not
    /// open.</param>
    /// <param name="time">The Gateway's clock: stamps when a window arrived and judges windows from the future.</param>
    public ResourceSummaryStore(Func<IDbContextFactory<GatewayStatsDbContext>?> factory, TimeProvider time)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        FileLog.Write("[ResourceSummaryStore] ctor: five-minute resource summaries are kept in the statistics database");
    }

    /// <summary>
    /// Store a batch of windows from one Director. Answers which windows the Gateway now holds (stored now or before),
    /// which it refuses for good, or - when nothing could be stored - why.
    /// </summary>
    public ResourceSummaryAck Store(TenantId tenant, string directorId, ResourceSummaryBatch? batch)
    {
        if (string.IsNullOrWhiteSpace(directorId)) throw new ArgumentException("A resource summary needs its Director.", nameof(directorId));
        var windows = batch?.Windows ?? new List<ResourceSummaryWindow>();
        FileLog.Write($"[ResourceSummaryStore] Store: tenant={tenant.ToLogString()} director={directorId} windows={windows.Count}");
        var ack = new ResourceSummaryAck();
        if (windows.Count == 0) return ack;

        var factory = _factory();
        if (factory is null)
        {
            ack.NotStoredReason = "the Gateway's statistics store is not open";
            FileLog.Write($"[ResourceSummaryStore] Store: NOT stored for director={directorId}: {ack.NotStoredReason}");
            return ack;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var valid = new List<ResourceSummaryWindow>();
        foreach (var window in windows)
        {
            var refusal = RefusalFor(window, now);
            if (refusal is null)
            {
                valid.Add(window);
                continue;
            }
            ack.RejectedWindowStartsUtc.Add(window.WindowStartUtc);
            FileLog.Write($"[ResourceSummaryStore] Store: refused window {window.WindowStartUtc:O} from director={directorId}: {refusal}");
        }

        // The same window twice in one batch is stored once: the first copy wins, as it does across batches.
        var distinct = valid
            .GroupBy(w => Utc(w.WindowStartUtc))
            .Select(g => g.First())
            .ToList();
        if (distinct.Count == 0) return ack;

        var starts = distinct.Select(w => Utc(w.WindowStartUtc)).ToList();
        using var db = factory.CreateDbContext();
        var already = db.ResourceWindows
            .Where(w => w.Tenant == tenant.Value && w.DirectorId == directorId && starts.Contains(w.WindowStartUtc))
            .Select(w => w.WindowStartUtc)
            .ToList()
            .Select(Utc)
            .ToHashSet();

        var stored = 0;
        foreach (var window in distinct)
        {
            var start = Utc(window.WindowStartUtc);
            ack.HeldWindowStartsUtc.Add(start);
            if (already.Contains(start)) continue;
            db.ResourceWindows.Add(MachineRow(tenant, directorId, window, now));
            foreach (var session in window.Sessions
                         .Where(s => !string.IsNullOrWhiteSpace(s.SessionId))
                         .GroupBy(s => s.SessionId, StringComparer.Ordinal)
                         .Select(g => g.First()))
                db.ResourceSessionWindows.Add(SessionRow(tenant, directorId, window, session, now));
            stored++;
        }
        db.SaveChanges();
        FileLog.Write($"[ResourceSummaryStore] Store: director={directorId} stored={stored} alreadyHeld={already.Count} refused={ack.RejectedWindowStartsUtc.Count}");
        return ack;
    }

    /// <summary>Every machine window of this tenant that starts in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>),
    /// oldest first. Null when the statistics store is not open.</summary>
    public IReadOnlyList<ResourceWindowEntity>? MachineWindows(TenantId tenant, DateTime fromUtc, DateTime toUtc)
    {
        var factory = _factory();
        if (factory is null) return null;
        using var db = factory.CreateDbContext();
        var from = Utc(fromUtc);
        var to = Utc(toUtc);
        return db.ResourceWindows.AsNoTracking()
            .Where(w => w.Tenant == tenant.Value && w.WindowStartUtc >= from && w.WindowStartUtc < to)
            .OrderBy(w => w.WindowStartUtc)
            .ToList();
    }

    /// <summary>Every session window of this tenant on one machine that starts in the range, oldest first. Null when
    /// the statistics store is not open.</summary>
    public IReadOnlyList<ResourceSessionWindowEntity>? SessionWindows(TenantId tenant, string machineName, DateTime fromUtc, DateTime toUtc)
    {
        var factory = _factory();
        if (factory is null) return null;
        using var db = factory.CreateDbContext();
        var from = Utc(fromUtc);
        var to = Utc(toUtc);
        return db.ResourceSessionWindows.AsNoTracking()
            .Where(w => w.Tenant == tenant.Value && w.WindowStartUtc >= from && w.WindowStartUtc < to)
            .OrderBy(w => w.WindowStartUtc)
            .ToList()
            // Machine names are compared the way the Machines view groups them: ignoring case.
            .Where(w => string.Equals(w.MachineName, machineName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Every machine hour of this tenant that starts in the range, oldest first. Null when the statistics
    /// store is not open.</summary>
    public IReadOnlyList<ResourceHourEntity>? MachineHours(TenantId tenant, DateTime fromUtc, DateTime toUtc)
    {
        var factory = _factory();
        if (factory is null) return null;
        using var db = factory.CreateDbContext();
        var from = Utc(fromUtc);
        var to = Utc(toUtc);
        return db.ResourceHours.AsNoTracking()
            .Where(h => h.Tenant == tenant.Value && h.HourStartUtc >= from && h.HourStartUtc < to)
            .OrderBy(h => h.HourStartUtc)
            .ToList();
    }

    /// <summary>Why a window can never be stored, or null when it can.</summary>
    internal static string? RefusalFor(ResourceSummaryWindow? window, DateTime nowUtc)
    {
        if (window is null) return "the window is empty";
        if (!ResourceSummaryContract.IsWindowStart(window.WindowStartUtc))
            return "its start is not on a five-minute boundary";
        if (window.WindowSeconds != ResourceSummaryContract.WindowSeconds)
            return $"it is {window.WindowSeconds} seconds long, not {ResourceSummaryContract.WindowSeconds}";
        if (string.IsNullOrWhiteSpace(window.MachineName))
            return "it names no machine";
        var start = Utc(window.WindowStartUtc);
        if (start.AddSeconds(window.WindowSeconds) > nowUtc + FutureTolerance)
            return "it ends in the future by the Gateway's clock";
        if (start < nowUtc - WindowRetention)
            return "it is older than the 90 days five-minute rows are kept";
        if (FiguresProblem(window.Machine, isMachine: true) is { } machineProblem)
            return $"the machine's figures are impossible: {machineProblem}";
        foreach (var session in window.Sessions ?? new List<ResourceSessionSummary>())
        {
            if (session is null) return "it holds an empty session row";
            if (session.ProcessesWithUnknownFigures < 0) return $"session {session.SessionId} counts fewer than no unknown processes";
            if (FiguresProblem(session.Figures, isMachine: false) is { } sessionProblem)
                return $"session {session.SessionId}'s figures are impossible: {sessionProblem}";
        }
        return null;
    }

    /// <summary>
    /// Why a set of figures cannot be true of any machine, or null when it can. A figure out of range is a DEFECT IN THE
    /// DIRECTOR, surfaced by refusing the window - never clamped into range, which would turn a per-core 400% into a
    /// believable 100% and hide the very defect the range exists to catch. The refusal is logged on both sides.
    /// </summary>
    internal static string? FiguresProblem(ResourceSummaryFigures? f, bool isMachine)
    {
        if (f is null) return "there are none";
        if (f.ProcessorCouldRead)
        {
            if (f.ProcessorAveragePercent is not { } average || f.ProcessorPeakPercent is not { } peak)
                return "the processor is marked read but has no average or peak";
            if (f.ProcessorSamples <= 0) return "the processor is marked read from no samples";
            if (!IsPercent(average) || !IsPercent(peak))
                return $"processor average {average} or peak {peak} is outside 0 to 100 per cent";
            if (peak < average) return $"processor peak {peak} is below its average {average}";
        }
        if (f.MemoryCouldRead)
        {
            if (f.MemoryAverageUsedBytes is not { } average || f.MemoryPeakUsedBytes is not { } peak)
                return "memory is marked read but has no average or peak";
            if (f.MemorySamples <= 0) return "memory is marked read from no samples";
            if (average < 0 || peak < 0) return "memory in use is below zero";
            if (peak < average) return $"memory peak {peak} is below its average {average}";
            if (f.MemoryTotalBytes is { } total && (total <= 0 || peak > total))
                return $"memory peak {peak} bytes is more than the machine's total {total}";
        }
        if (!isMachine && f.MemoryTotalBytes is not null) return "a session row carries the machine's total memory";
        foreach (var c in f.Crossings ?? new List<ResourceCrossing>())
        {
            if (c is null) return "a crossing is empty";
            if (c.Resource is not (ResourceSummaryContract.Processor or ResourceSummaryContract.Memory))
                return $"a crossing names the resource '{c.Resource}', which is neither the processor nor memory";
            if (c.Level is not (ResourceSummaryContract.Amber or ResourceSummaryContract.Red))
                return $"a crossing has the level '{c.Level}', which is neither amber nor red";
            if (c.Seconds <= 0 || c.Seconds > ResourceSummaryContract.WindowSeconds)
                return $"a crossing lasts {c.Seconds} seconds, outside one window";
            if (c.PeakPercent is { } p && !IsPercent(p)) return $"a crossing peaks at {p} per cent";
        }
        return null;
    }

    private static bool IsPercent(double value) => !double.IsNaN(value) && value >= 0 && value <= 100;

    private static ResourceWindowEntity MachineRow(TenantId tenant, string directorId, ResourceSummaryWindow window, DateTime now)
    {
        var start = Utc(window.WindowStartUtc);
        var row = new ResourceWindowEntity
        {
            Tenant = tenant.Value,
            DirectorId = directorId,
            WindowStartUtc = start,
            HourStartUtc = HourOf(start),
            MachineName = window.MachineName.Trim(),
            WindowSeconds = window.WindowSeconds,
            SampleCount = window.SampleCount,
            ReceivedUtc = now,
        };
        CopyFigures(window.Machine ?? new ResourceSummaryFigures(), row, keepTotal: true);
        return row;
    }

    private static ResourceSessionWindowEntity SessionRow(TenantId tenant, string directorId, ResourceSummaryWindow window,
        ResourceSessionSummary session, DateTime now)
    {
        var start = Utc(window.WindowStartUtc);
        var row = new ResourceSessionWindowEntity
        {
            Tenant = tenant.Value,
            DirectorId = directorId,
            WindowStartUtc = start,
            SessionId = session.SessionId,
            HourStartUtc = HourOf(start),
            MachineName = window.MachineName.Trim(),
            Factory = string.IsNullOrWhiteSpace(session.Factory) ? null : session.Factory.Trim(),
            SampleCount = session.SampleCount,
            ProcessesWithUnknownFigures = session.ProcessesWithUnknownFigures,
            ReceivedUtc = now,
        };
        CopyFigures(session.Figures ?? new ResourceSummaryFigures(), row, keepTotal: false);
        return row;
    }

    /// <summary>Copy the wire figures onto a row. A figure the Director says it could not read is stored as null with
    /// its reason, whatever number came with it.</summary>
    private static void CopyFigures(ResourceSummaryFigures figures, IResourceFigureColumns row, bool keepTotal)
    {
        row.ProcessorSamples = figures.ProcessorCouldRead ? figures.ProcessorSamples : 0;
        row.ProcessorAveragePercent = figures.ProcessorCouldRead ? figures.ProcessorAveragePercent : null;
        row.ProcessorPeakPercent = figures.ProcessorCouldRead ? figures.ProcessorPeakPercent : null;
        row.ProcessorUnreadableReason = figures.ProcessorCouldRead ? null
            : figures.ProcessorUnreadableReason ?? "the Director could not read the processor and gave no reason";
        row.MemorySamples = figures.MemoryCouldRead ? figures.MemorySamples : 0;
        row.MemoryAverageUsedBytes = figures.MemoryCouldRead ? figures.MemoryAverageUsedBytes : null;
        row.MemoryPeakUsedBytes = figures.MemoryCouldRead ? figures.MemoryPeakUsedBytes : null;
        row.MemoryTotalBytes = figures.MemoryCouldRead && keepTotal ? figures.MemoryTotalBytes : null;
        row.MemoryUnreadableReason = figures.MemoryCouldRead ? null
            : figures.MemoryUnreadableReason ?? "the Director could not read memory and gave no reason";
        row.CrossingsJson = JsonSerializer.Serialize(figures.Crossings ?? new List<ResourceCrossing>(), CrossingJson);
    }

    /// <summary>Read a row's crossings back.</summary>
    public static List<ResourceCrossing> CrossingsOf(IResourceFigureColumns row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return JsonSerializer.Deserialize<List<ResourceCrossing>>(row.CrossingsJson, CrossingJson)
               ?? throw new InvalidOperationException("A stored resource row's crossings read back as null.");
    }

    internal static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    internal static DateTime HourOf(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
}
