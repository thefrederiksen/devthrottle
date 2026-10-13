using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using System.Text.Json;
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
/// THE READ SIDE, for the factory page (agreed with the Implementation Lead, 11 October 2026):
///   - <see cref="List"/>: one factory's (or the account's) SENT emails, newest first - id, slot, sent time, title,
///     the factories covered. <c>GET /gateway/factory-emails?factory=&amp;kind=&amp;limit=N</c>.
///   - <see cref="Get"/>: one email's HTML exactly as sent, by id. <c>GET /gateway/factory-emails/{id}</c>.
/// A slot that had nothing to send, or failed, has a row too; <c>state=all</c> lists those, so "no 16:00 check today"
/// can be told apart from "the 16:00 check failed".
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

    /// <summary>
    /// The account's kept emails, newest first, at most <paramref name="limit"/> (capped at <see cref="MaxList"/>):
    /// only the SENT ones unless <paramref name="allStates"/>, only those covering <paramref name="factory"/> when one is
    /// named, only one <paramref name="kind"/> when one is named. Without the body - that is <see cref="Get"/>.
    /// </summary>
    public FactoryEmailsDto List(TenantId tenant, string? factory, string? kind, int limit, bool allStates)
    {
        var take = Math.Clamp(limit, 1, MaxList);
        var wanted = string.IsNullOrWhiteSpace(factory) ? null : factory.Trim();
        using var ctx = _db.CreateContext(tenant);
        var query = ctx.FactoryEmailReports.AsNoTracking();
        if (!allStates)
            query = query.Where(r => r.State == FactoryEmailStates.Sent);
        if (!string.IsNullOrWhiteSpace(kind))
            query = query.Where(r => r.Kind == kind);
        // At most three rows a day for the retention period, so the factory filter reads the JSON list in memory.
        var emails = query.OrderByDescending(r => r.BoundaryUtc).ToList()
            .Select(r => ToDto(r, includeHtml: false))
            .Where(d => wanted is null || d.Factories.Contains(wanted, StringComparer.OrdinalIgnoreCase))
            .Take(take)
            .ToList();
        return new FactoryEmailsDto { Count = emails.Count, Factory = wanted, Emails = emails };
    }

    /// <summary>One kept email of the account with its body exactly as composed, or null when there is no such id.</summary>
    public FactoryEmailDto? Get(TenantId tenant, Guid id)
    {
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.FactoryEmailReports.AsNoTracking().FirstOrDefault(r => r.Id == id);
        return row is null ? null : ToDto(row, includeHtml: true);
    }

    private static FactoryEmailDto ToDto(FactoryEmailReportEntity r, bool includeHtml) => new()
    {
        Id = r.Id.ToString("D"),
        SlotKey = r.SlotKey,
        Slot = r.SlotKey.Length >= 13 ? r.SlotKey[^2..] + ":00" : "",
        Kind = r.Kind,
        BoundaryUtc = DateTime.SpecifyKind(r.BoundaryUtc, DateTimeKind.Utc),
        State = r.State,
        SentUtc = r.SentAtUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null,
        Title = r.Subject,
        Factories = JsonSerializer.Deserialize<List<string>>(r.FactoriesJson)
                    ?? throw new InvalidOperationException($"kept factory email {r.Id} holds no factory list"),
        ProblemCount = r.ProblemCount,
        Detail = r.Detail,
        Html = includeHtml ? r.Html : null,
    };

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
