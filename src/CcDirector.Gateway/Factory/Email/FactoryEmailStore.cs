using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Email;

/// <summary>What became of a factory email slot.</summary>
public static class FactoryEmailStates
{
    /// <summary>The email was sent.</summary>
    public const string Sent = "sent";

    /// <summary>The slot had nothing to send (a check with no new problem, or an account with no factory schedules).</summary>
    public const string NothingToSend = "nothing-to-send";

    /// <summary>The send failed. It is tried again until the slot's late window closes.</summary>
    public const string Failed = "failed";

    /// <summary>The account cannot be sent the factory email yet (a team account). Final.</summary>
    public const string NotSupported = "not-supported";
}

/// <summary>
/// The kept factory emails (Factory Control, step 4): one row per account and slot. The sweep writes it; the factory
/// page reads it.
///
/// THE READ SIDE, for the factory page: <see cref="List"/> returns the account's kept emails, newest first, each with
/// its subject, state and - when asked - the HTML exactly as composed. A slot that had nothing to send has a row too,
/// so "no 16:00 check today" can be told apart from "the 16:00 check failed". Served at
/// <c>GET /gateway/factory-email/reports?kind=&amp;limit=&amp;html=true</c>.
/// </summary>
public sealed class FactoryEmailStore
{
    /// <summary>The most kept emails a list returns.</summary>
    public const int MaxList = 100;

    /// <summary>How long a kept email is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FactoryEmailStore(GatewayDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>The account's row for a slot, or null when the slot has not been handled.</summary>
    public FactoryEmailReportEntity? Find(TenantId tenant, string slotKey)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.FactoryEmailReports.AsNoTracking().FirstOrDefault(r => r.SlotKey == slotKey);
    }

    /// <summary>
    /// Record a slot: insert its row, or update the row a failed send left. One row per account and slot - the
    /// database's unique index stands behind that, so two writers can never both record the same slot.
    /// </summary>
    public FactoryEmailReportEntity Save(TenantId tenant, string slotKey, Action<FactoryEmailReportEntity> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.FactoryEmailReports.FirstOrDefault(r => r.SlotKey == slotKey);
            if (row is null)
            {
                row = new FactoryEmailReportEntity { SlotKey = slotKey };
                row.TenantId = ctx.ActiveTenant!;
                fill(row);
                ctx.FactoryEmailReports.Add(row);
            }
            else
            {
                fill(row);
            }
            ctx.SaveChanges();
            FileLog.Write($"[FactoryEmailStore] Save: tenant={tenant.ToLogString()}, slot={slotKey}, kind={row.Kind}, state={row.State}, attempts={row.Attempts}");
            return row;
        }
    }

    /// <summary>The account's kept emails, newest first, at most <paramref name="limit"/> (capped at
    /// <see cref="MaxList"/>). <paramref name="includeHtml"/> adds each body.</summary>
    public FactoryEmailReportsDto List(TenantId tenant, string? kind, int limit, bool includeHtml)
    {
        var take = Math.Clamp(limit, 1, MaxList);
        using var ctx = _db.CreateContext(tenant);
        var query = ctx.FactoryEmailReports.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind))
            query = query.Where(r => r.Kind == kind);
        var rows = query.OrderByDescending(r => r.BoundaryUtc).Take(take).ToList();
        var reports = rows.Select(r => new FactoryEmailReportDto
        {
            SlotKey = r.SlotKey,
            Kind = r.Kind,
            BoundaryUtc = DateTime.SpecifyKind(r.BoundaryUtc, DateTimeKind.Utc),
            ComposedAtUtc = DateTime.SpecifyKind(r.ComposedAtUtc, DateTimeKind.Utc),
            State = r.State,
            Detail = r.Detail,
            Subject = r.Subject,
            Html = includeHtml ? r.Html : null,
            ProblemCount = r.ProblemCount,
            SentAtUtc = r.SentAtUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null,
        }).ToList();
        return new FactoryEmailReportsDto { Count = reports.Count, Reports = reports };
    }

    /// <summary>Delete the account's kept emails older than <see cref="Retention"/>. Returns how many went.</summary>
    public int Prune(TenantId tenant, DateTime nowUtc)
    {
        var cutoff = nowUtc - Retention;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var old = ctx.FactoryEmailReports.Where(r => r.BoundaryUtc < cutoff).ToList();
            if (old.Count == 0) return 0;
            ctx.FactoryEmailReports.RemoveRange(old);
            ctx.SaveChanges();
            FileLog.Write($"[FactoryEmailStore] Prune: tenant={tenant.ToLogString()}, removed={old.Count}");
            return old.Count;
        }
    }
}
