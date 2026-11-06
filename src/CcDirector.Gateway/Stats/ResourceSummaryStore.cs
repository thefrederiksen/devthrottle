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

    /// <summary>The most samples a window may claim. A five-second sampler takes 60 in five minutes; timer jitter can add
    /// a few, and twice that is the most that is believable.</summary>
    public const int MaxSamplesPerWindow = 120;

    /// <summary>How far past its window a crossing's rounded end may reach.</summary>
    public static readonly TimeSpan CrossingRoundingSlack = TimeSpan.FromSeconds(1);

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
        FileLog.Write("[ResourceSummaryStore] constructed: five-minute resource summaries are kept in the statistics database");
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
            // FROM THE FUTURE IS NOT "FOR GOOD". A Director whose clock runs fast sends windows that end ahead of this
            // Gateway's clock; the same window is acceptable once this clock catches up. So it is neither held nor
            // refused: the Director keeps it and sends it again on a later reseed. Refusing it for good would leave a
            // fast-clocked machine with no rows at all (review finding 1).
            if (window is null)
            {
                FileLog.Write($"[ResourceSummaryStore] Store: director={directorId} sent an empty window entry; it names no window, so there is nothing to answer");
                continue;
            }
            if (Utc(window.WindowStartUtc).AddSeconds(window.WindowSeconds) > now + FutureTolerance)
            {
                FileLog.Write($"[ResourceSummaryStore] Store: window {window.WindowStartUtc:O} from director={directorId} ends more than "
                              + $"{FutureTolerance.TotalMinutes:F0} minutes ahead of the Gateway's clock ({now:O}); NOT stored yet - the "
                              + "Director keeps it and sends it again. Its clock is probably fast.");
                continue;
            }
            var refusal = StructuralRefusalFor(window, now);
            if (refusal is null)
            {
                valid.Add(window);
                continue;
            }
            ack.RejectedWindowStartsUtc.Add(window.WindowStartUtc);
            FileLog.Write($"[ResourceSummaryStore] Store: refused window {window.WindowStartUtc:O} from director={directorId}: {refusal}");
        }

        // The same window twice in one batch is stored once: the copy with the most samples, as across batches.
        var distinct = valid
            .GroupBy(w => Utc(w.WindowStartUtc))
            .Select(g => g.OrderByDescending(w => w.SampleCount).First())
            .ToList();
        if (distinct.Count == 0) return ack;

        var starts = distinct.Select(w => Utc(w.WindowStartUtc)).ToList();
        using var db = factory.CreateDbContext();
        var already = db.ResourceWindows
            .Where(w => w.Tenant == tenant.Value && w.DirectorId == directorId && starts.Contains(w.WindowStartUtc))
            .ToList()
            .ToDictionary(w => Utc(w.WindowStartUtc));

        var stored = 0;
        var refusedFigures = 0;
        var replaced = 0;
        foreach (var window in distinct)
        {
            var start = Utc(window.WindowStartUtc);
            ack.HeldWindowStartsUtc.Add(start);
            if (already.TryGetValue(start, out var existing))
            {
                // A resend after a lost acknowledgement is the same window again: nothing to do. A copy with MORE samples
                // is a fuller measurement of the same five minutes - a clock that moved back closed a fragment first - so
                // it replaces the fragment, and the hour rolls again because the row is received anew (review note 5).
                if (window.SampleCount <= existing.SampleCount) continue;
                FileLog.Write($"[ResourceSummaryStore] Store: window {start:O} from director={directorId} replaces a held copy of "
                              + $"{existing.SampleCount} samples with one of {window.SampleCount}");
                db.ResourceWindows.Remove(existing);
                db.ResourceSessionWindows.RemoveRange(db.ResourceSessionWindows.Where(s =>
                    s.Tenant == tenant.Value && s.DirectorId == directorId && s.WindowStartUtc == start));
                replaced++;
            }
            if (FiguresRefusalFor(window) is { } refused)
            {
                // Stored MARKED, not dropped, and its session rows - from the same suspect measurement - are not kept.
                FileLog.Write($"[ResourceSummaryStore] Store: window {start:O} from director={directorId} stored with its figures REFUSED: {refused}");
                db.ResourceWindows.Add(RefusedRow(tenant, directorId, window, now, refused));
                refusedFigures++;
                stored++;
                continue;
            }
            db.ResourceWindows.Add(MachineRow(tenant, directorId, window, now));
            foreach (var session in window.Sessions
                         .Where(s => !string.IsNullOrWhiteSpace(s.SessionId))
                         .GroupBy(s => s.SessionId, StringComparer.Ordinal)
                         .Select(g => g.First()))
                db.ResourceSessionWindows.Add(SessionRow(tenant, directorId, window, session, now));
            stored++;
        }
        db.SaveChanges();
        FileLog.Write($"[ResourceSummaryStore] Store: director={directorId} stored={stored} (figures refused in {refusedFigures}, "
                      + $"fuller copies replacing held ones {replaced}) alreadyHeld={already.Count} rejected={ack.RejectedWindowStartsUtc.Count}");
        return ack;
    }

    /// <summary>Every machine window of this tenant that starts in [<paramref name="fromUtc"/>, <paramref name="toUtc"/>),
    /// oldest first. Null when the statistics store is not open.</summary>
    public IReadOnlyList<ResourceWindowEntity>? MachineWindows(TenantId tenant, DateTime fromUtc, DateTime toUtc)
    {
        var factory = _factory();
        if (factory is null)
        {
            FileLog.Write($"[ResourceSummaryStore] MachineWindows: tenant={tenant.ToLogString()} - the statistics store is not open");
            return null;
        }
        using var db = factory.CreateDbContext();
        var from = Utc(fromUtc);
        var to = Utc(toUtc);
        var rows = db.ResourceWindows.AsNoTracking()
            .Where(w => w.Tenant == tenant.Value && w.WindowStartUtc >= from && w.WindowStartUtc < to)
            .OrderBy(w => w.WindowStartUtc)
            .ToList();
        FileLog.Write($"[ResourceSummaryStore] MachineWindows: tenant={tenant.ToLogString()} {from:O}..{to:O} rows={rows.Count}");
        return rows;
    }

    /// <summary>Every session window of this tenant on one machine that starts in the range, oldest first. Null when
    /// the statistics store is not open.</summary>
    public IReadOnlyList<ResourceSessionWindowEntity>? SessionWindows(TenantId tenant, string machineName, DateTime fromUtc, DateTime toUtc)
    {
        var factory = _factory();
        if (factory is null)
        {
            FileLog.Write($"[ResourceSummaryStore] SessionWindows: tenant={tenant.ToLogString()} machine={machineName} - the statistics store is not open");
            return null;
        }
        using var db = factory.CreateDbContext();
        var from = Utc(fromUtc);
        var to = Utc(toUtc);
        var rows = db.ResourceSessionWindows.AsNoTracking()
            .Where(w => w.Tenant == tenant.Value && w.WindowStartUtc >= from && w.WindowStartUtc < to)
            .OrderBy(w => w.WindowStartUtc)
            .ToList()
            // Machine names are compared the way the Machines view groups them: ignoring case.
            .Where(w => string.Equals(w.MachineName, machineName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        FileLog.Write($"[ResourceSummaryStore] SessionWindows: tenant={tenant.ToLogString()} machine={machineName} rows={rows.Count}");
        return rows;
    }

    /// <summary>Every machine hour of this tenant that starts in the range, oldest first. Null when the statistics
    /// store is not open.</summary>
    public IReadOnlyList<ResourceHourEntity>? MachineHours(TenantId tenant, DateTime fromUtc, DateTime toUtc)
    {
        var factory = _factory();
        if (factory is null)
        {
            FileLog.Write($"[ResourceSummaryStore] MachineHours: tenant={tenant.ToLogString()} - the statistics store is not open");
            return null;
        }
        using var db = factory.CreateDbContext();
        var from = Utc(fromUtc);
        var to = Utc(toUtc);
        var rows = db.ResourceHours.AsNoTracking()
            .Where(h => h.Tenant == tenant.Value && h.HourStartUtc >= from && h.HourStartUtc < to)
            .OrderBy(h => h.HourStartUtc)
            .ToList();
        FileLog.Write($"[ResourceSummaryStore] MachineHours: tenant={tenant.ToLogString()} rows={rows.Count}");
        return rows;
    }

    /// <summary>Why a window can never be stored at all - it has no place to go - or null when it can. A window whose
    /// figures are impossible CAN be stored, marked; see <see cref="FiguresRefusalFor"/>.</summary>
    internal static string? StructuralRefusalFor(ResourceSummaryWindow? window, DateTime nowUtc)
    {
        if (window is null) return "the window is empty";
        if (!ResourceSummaryContract.IsWindowStart(window.WindowStartUtc))
            return "its start is not on a five-minute boundary";
        if (window.WindowSeconds != ResourceSummaryContract.WindowSeconds)
            return $"it is {window.WindowSeconds} seconds long, not {ResourceSummaryContract.WindowSeconds}";
        if (string.IsNullOrWhiteSpace(window.MachineName))
            return "it names no machine";
        // By HOUR, not by start: once any window of an hour has passed retention its hour is final, and a late window for
        // it would make the roll-up rebuild the hour from what is left (review note 6).
        if (HourOf(Utc(window.WindowStartUtc)) < nowUtc - WindowRetention)
            return "its hour is older than the 90 days five-minute rows are kept";
        return null;
    }

    /// <summary>Why a window's figures cannot be true of any machine, or null when they can.</summary>
    internal static string? FiguresRefusalFor(ResourceSummaryWindow window)
    {
        // The sample counts are the roll-up's WEIGHTS, so they are checked like figures: one window claiming a million
        // samples would otherwise own its hour's average (review finding 3b).
        if (window.SampleCount <= 0 || window.SampleCount > MaxSamplesPerWindow)
            return $"it claims {window.SampleCount} samples, where a five-second sampler takes about 60 in five minutes (at most {MaxSamplesPerWindow} accepted)";
        var start = Utc(window.WindowStartUtc);
        var end = start.AddSeconds(window.WindowSeconds);
        if (FiguresProblem(window.Machine, isMachine: true, window.SampleCount, start, end) is { } machineProblem)
            return $"the machine's figures are impossible: {machineProblem}";
        var machineTotal = window.Machine is { MemoryCouldRead: true } m ? m.MemoryTotalBytes : null;
        foreach (var session in window.Sessions ?? new List<ResourceSessionSummary>())
        {
            if (session is null) return "it holds an empty session row";
            if (session.ProcessesWithUnknownFigures < 0) return $"session {session.SessionId} counts fewer than no unknown processes";
            if (session.SampleCount <= 0 || session.SampleCount > window.SampleCount)
                return $"session {session.SessionId} claims {session.SampleCount} samples in a window of {window.SampleCount}";
            if (FiguresProblem(session.Figures, isMachine: false, session.SampleCount, start, end) is { } sessionProblem)
                return $"session {session.SessionId}'s figures are impossible: {sessionProblem}";
            if (machineTotal is { } total && session.Figures.MemoryPeakUsedBytes is { } sessionPeak && sessionPeak > total)
                return $"session {session.SessionId}'s memory peak {sessionPeak} bytes is more than the machine's total {total}";
        }
        return null;
    }

    /// <summary>
    /// Why a set of figures cannot be true of any machine, or null when it can. A figure out of range is a DEFECT IN THE
    /// DIRECTOR, surfaced by refusing the window - never clamped into range, which would turn a per-core 400% into a
    /// believable 100% and hide the very defect the range exists to catch. The refusal is logged on both sides.
    /// </summary>
    internal static string? FiguresProblem(ResourceSummaryFigures? f, bool isMachine, int sampleCount, DateTime windowStart, DateTime windowEnd)
    {
        if (f is null) return "there are none";
        if (f.ProcessorSamples < 0 || f.ProcessorSamples > sampleCount || f.MemorySamples < 0 || f.MemorySamples > sampleCount)
            return $"processor samples {f.ProcessorSamples} or memory samples {f.MemorySamples} is outside the {sampleCount} samples taken";
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
            // The START must lie inside the window. The END may pass it by one second: the Director rounds a crossing's
            // length to whole seconds (at least one), and a sampler that ticks 0.3 seconds off the five-second mark puts a
            // run to the end of the window 0.3 seconds past it. Refusing that would refuse correct windows - exactly the
            // ones carrying a sustained red across a window boundary.
            var startedUtc = Utc(c.StartedUtc);
            if (startedUtc < windowStart || startedUtc >= windowEnd || startedUtc.AddSeconds(c.Seconds) > windowEnd + CrossingRoundingSlack)
                return $"a crossing from {startedUtc:O} for {c.Seconds} seconds does not lie inside its window {windowStart:O}";
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

    /// <summary>A window whose figures were refused: present, with the reason, and with no figure at all.</summary>
    private static ResourceWindowEntity RefusedRow(TenantId tenant, string directorId, ResourceSummaryWindow window, DateTime now, string reason)
    {
        var start = Utc(window.WindowStartUtc);
        var why = $"The Gateway refused this window's figures because {reason}";
        return new ResourceWindowEntity
        {
            Tenant = tenant.Value,
            DirectorId = directorId,
            WindowStartUtc = start,
            HourStartUtc = HourOf(start),
            MachineName = window.MachineName.Trim(),
            WindowSeconds = window.WindowSeconds,
            SampleCount = window.SampleCount,
            ReceivedUtc = now,
            RefusedReason = reason,
            ProcessorUnreadableReason = why,
            MemoryUnreadableReason = why,
            CrossingsJson = "[]",
        };
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
