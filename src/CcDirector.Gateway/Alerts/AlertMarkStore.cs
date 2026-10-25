using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Alerts;

/// <summary>
/// WHAT THE OWNER HAS SEEN AND CLEARED, per account, over the <c>alert_marks</c> table (the Alerts mission). The only
/// state Alerts keeps; the alerts themselves are folded from their features' records on every read.
///
/// TENANT-PARTITIONED BY CONSTRUCTION: every method takes the account and opens its context with
/// <see cref="GatewayDatabase.CreateContext(TenantId)"/>, so the global query filter keeps one account from reading or
/// writing another's marks. Writes are serialised in-process, so two clears of one alert at the same moment cannot both
/// try to insert its row.
/// </summary>
public sealed class AlertMarkStore
{
    private readonly GatewayDatabase _db;
    private readonly object _gate = new();

    public AlertMarkStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// A seen version as the table can hold it: whole microseconds. PostgreSQL keeps a <c>timestamp with time zone</c>
    /// to the microsecond and Npgsql drops the rest, while a restart request's <c>UpdatedAtUtc</c> comes from an
    /// in-memory clock with 100-nanosecond ticks - so a version stored as given reads back up to 900 nanoseconds
    /// EARLIER than the alert it marks, and the alert would never read as seen (phase 2 review, finding 1). Every
    /// version is cut to this precision before it is stored and before it is compared, so both providers agree.
    /// </summary>
    public static DateTime ToStoredPrecision(DateTime utc)
        => new(utc.Ticks - utc.Ticks % TicksPerMicrosecond, DateTimeKind.Utc);

    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>Every mark in the account, by alert id.</summary>
    public IReadOnlyDictionary<string, AlertMarkEntity> All(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.AlertMarks.AsNoTracking().ToList().ToDictionary(m => m.AlertId, StringComparer.Ordinal);
    }

    /// <summary>Record that the owner saw each alert at the given version (its <c>UpdatedAtUtc</c>), stored at
    /// <see cref="ToStoredPrecision"/>.</summary>
    public void MarkSeen(TenantId tenant, IReadOnlyCollection<(string AlertId, DateTime Version)> seen, DateTime nowUtc)
    {
        if (seen.Count == 0) return;
        Write(tenant, seen.Select(s => s.AlertId), nowUtc, (row, id) =>
            row.SeenVersionUtc = ToStoredPrecision(seen.First(s => s.AlertId == id).Version));
        FileLog.Write($"[AlertMarkStore] MarkSeen: tenant={tenant.ToLogString()}, count={seen.Count}");
    }

    /// <summary>Record that the owner cleared each alert.</summary>
    public void MarkCleared(TenantId tenant, IReadOnlyCollection<string> alertIds, DateTime nowUtc)
    {
        if (alertIds.Count == 0) return;
        Write(tenant, alertIds, nowUtc, (row, _) => row.ClearedAtUtc = nowUtc);
        FileLog.Write($"[AlertMarkStore] MarkCleared: tenant={tenant.ToLogString()}, count={alertIds.Count}");
    }

    /// <summary>
    /// Delete every mark whose alert no source lists any more and that was last written more than
    /// <paramref name="olderThan"/> ago. An alert that has left every source never returns - its id names one record -
    /// so its mark has nothing left to say. The age guard keeps a mark written a moment after the list was read.
    /// </summary>
    public int Sweep(TenantId tenant, IReadOnlySet<string> listedIds, TimeSpan olderThan, DateTime nowUtc)
    {
        var before = nowUtc - olderThan;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var stale = ctx.AlertMarks.Where(m => m.TouchedAtUtc < before).ToList()
                .Where(m => !listedIds.Contains(m.AlertId))
                .ToList();
            if (stale.Count == 0) return 0;
            ctx.AlertMarks.RemoveRange(stale);
            ctx.SaveChanges();
            FileLog.Write($"[AlertMarkStore] Sweep: tenant={tenant.ToLogString()}, removed={stale.Count}");
            return stale.Count;
        }
    }

    private void Write(TenantId tenant, IEnumerable<string> alertIds, DateTime nowUtc, Action<AlertMarkEntity, string> apply)
    {
        var ids = alertIds.Distinct(StringComparer.Ordinal).ToList();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var existing = ctx.AlertMarks.Where(m => ids.Contains(m.AlertId)).ToDictionary(m => m.AlertId, StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!existing.TryGetValue(id, out var row))
                {
                    row = new AlertMarkEntity { TenantId = ctx.ActiveTenant!, AlertId = id };
                    ctx.AlertMarks.Add(row);
                }
                apply(row, id);
                row.TouchedAtUtc = nowUtc;
            }
            ctx.SaveChanges();
        }
    }
}
