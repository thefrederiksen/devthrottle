using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Messaging;

/// <summary>Where a request for a message link stands (issue #3548).</summary>
public static class FleetMessageLinkRequestStatuses
{
    /// <summary>It waits for the owner.</summary>
    public const string Pending = "pending";

    /// <summary>The owner allowed it, and a link was set up.</summary>
    public const string Allowed = "allowed";

    /// <summary>The owner said no.</summary>
    public const string Declined = "declined";

    /// <summary>One of its two sessions ended before the owner answered.</summary>
    public const string Ended = "ended";
}

/// <summary>One request for a message link, as a caller reads it.</summary>
public sealed record FleetMessageLinkRequest(
    string RequestId,
    string RequesterSessionId,
    string TargetSessionId,
    string Reason,
    string Status,
    DateTime AskedAtUtc,
    string? AnsweredBy,
    DateTime? AnsweredAtUtc,
    string? Amount,
    string? LinkId);

/// <summary>What one ask did.</summary>
/// <param name="Request">The request that now waits: the new one, or the one already waiting for the same pair.</param>
/// <param name="Created">True when this ask made a new request; false when the same session was already asking for the
/// same session, and nothing changed.</param>
/// <param name="Refused">True when the session already has <see cref="FleetMessageLinkRequestStore.MaxPendingPerSession"/>
/// requests waiting, so nothing was asked; <see cref="Request"/> is then null.</param>
/// <param name="RecentlyDeclined">True when the owner said no to this same pair less than
/// <see cref="FleetMessageLinkRequestStore.DeclinedCooldown"/> ago, so nothing was asked; <see cref="Request"/> is then
/// null.</param>
public sealed record FleetMessageLinkAsk(FleetMessageLinkRequest? Request, bool Created, bool Refused,
    bool RecentlyDeclined = false);

/// <summary>
/// THE REQUESTS FOR A MESSAGE LINK, per account, over the <c>fleet_message_link_requests</c> table (issue #3548). A
/// session that may not message another can ask the owner to let them talk; the owner allows it - which sets up an
/// ordinary link - or says no.
///
/// TENANT-PARTITIONED BY CONSTRUCTION, like <see cref="FleetMessageLinkStore"/>: every method takes the tenant and opens
/// its context with <see cref="GatewayDatabase.CreateContext(TenantId)"/>.
///
/// ONE WAITING REQUEST PER PAIR, AND A FEW PER SESSION. Asking again for the same session returns the request already
/// waiting, so a session that retries does not fill the owner's list; and a session may have at most
/// <see cref="MaxPendingPerSession"/> waiting at once.
///
/// AN ANSWER CHANGES A REQUEST ONLY WHILE IT WAITS. <see cref="TryAnswer"/> is a conditional update, so two answers at
/// the same moment - the owner's browser and a raised session - cannot both win.
/// </summary>
public sealed class FleetMessageLinkRequestStore
{
    /// <summary>How many requests one session may have waiting at once.</summary>
    public const int MaxPendingPerSession = 3;

    /// <summary>The longest reason kept, in characters; a longer one is cut. Short enough that the governance record,
    /// which carries the reason after the two ids, keeps it whole.</summary>
    public const int MaxReasonLength = 380;

    /// <summary>After the owner says no, how long the same session may not ask for the same session again - so a
    /// session that ignores the answer cannot put the same card back in front of the owner straight away.</summary>
    public static readonly TimeSpan DeclinedCooldown = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FleetMessageLinkRequestStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Ask, for <paramref name="requesterSessionId"/>, to talk to <paramref name="targetSessionId"/>.</summary>
    /// <exception cref="ArgumentException">An id is not a session id, the two are the same session, or the reason is
    /// empty.</exception>
    public FleetMessageLinkAsk Ask(TenantId tenant, string requesterSessionId, string targetSessionId, string reason,
        DateTime nowUtc)
    {
        FileLog.Write($"[FleetMessageLinkRequestStore] Ask: tenant={tenant.ToLogString()}, from={requesterSessionId}, to={targetSessionId}");
        try
        {
            var from = Canonical(requesterSessionId, nameof(requesterSessionId));
            var to = Canonical(targetSessionId, nameof(targetSessionId));
            if (from == to)
                throw new ArgumentException("A session does not ask to talk to itself.", nameof(targetSessionId));
            var why = (reason ?? "").Trim();
            if (why.Length == 0)
                throw new ArgumentException("A request says why the session needs to talk.", nameof(reason));
            if (why.Length > MaxReasonLength) why = why[..MaxReasonLength];

            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var waiting = ctx.FleetMessageLinkRequests.AsNoTracking()
                    .Where(r => r.RequesterSessionId == from && r.Status == FleetMessageLinkRequestStatuses.Pending)
                    .ToList();
                var same = waiting.FirstOrDefault(r => r.TargetSessionId == to);
                if (same is not null)
                {
                    FileLog.Write($"[FleetMessageLinkRequestStore] Ask: already waiting, request={same.RequestId}");
                    return new FleetMessageLinkAsk(ToRecord(same), Created: false, Refused: false);
                }
                var noSince = Utc(nowUtc) - DeclinedCooldown;
                if (ctx.FleetMessageLinkRequests.AsNoTracking().Any(r => r.RequesterSessionId == from
                        && r.TargetSessionId == to && r.Status == FleetMessageLinkRequestStatuses.Declined
                        && r.AnsweredAtUtc != null && r.AnsweredAtUtc >= noSince))
                {
                    FileLog.Write("[FleetMessageLinkRequestStore] Ask: REFUSED, the owner said no to this pair within the hour");
                    return new FleetMessageLinkAsk(null, Created: false, Refused: false, RecentlyDeclined: true);
                }
                if (waiting.Count >= MaxPendingPerSession)
                {
                    FileLog.Write($"[FleetMessageLinkRequestStore] Ask: REFUSED, {waiting.Count} already waiting");
                    return new FleetMessageLinkAsk(null, Created: false, Refused: true);
                }

                var row = new FleetMessageLinkRequestEntity
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    RequesterSessionId = from,
                    TargetSessionId = to,
                    Reason = why,
                    Status = FleetMessageLinkRequestStatuses.Pending,
                    AskedAtUtc = Utc(nowUtc),
                };
                row.TenantId = ctx.ActiveTenant!;
                ctx.FleetMessageLinkRequests.Add(row);
                ctx.SaveChanges();
                FileLog.Write($"[FleetMessageLinkRequestStore] Ask: request={row.RequestId} waits");
                return new FleetMessageLinkAsk(ToRecord(row), Created: true, Refused: false);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkRequestStore] Ask FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>One request by its id, in any status; null when this account has none with that id.</summary>
    public FleetMessageLinkRequest? Find(TenantId tenant, string? requestId)
    {
        var id = RequestKey(requestId);
        if (id is null) return null;
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.FleetMessageLinkRequests.AsNoTracking().FirstOrDefault(r => r.RequestId == id);
        return row is null ? null : ToRecord(row);
    }

    /// <summary>Every waiting request, and every request answered at or after <paramref name="answeredSinceUtc"/>; newest
    /// first.</summary>
    public IReadOnlyList<FleetMessageLinkRequest> List(TenantId tenant, DateTime answeredSinceUtc)
    {
        var since = Utc(answeredSinceUtc);
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.FleetMessageLinkRequests.AsNoTracking()
            .Where(r => r.Status == FleetMessageLinkRequestStatuses.Pending
                || (r.AnsweredAtUtc != null && r.AnsweredAtUtc >= since))
            .ToList();
        FileLog.Write($"[FleetMessageLinkRequestStore] List: tenant={tenant.ToLogString()}, count={rows.Count}");
        return rows.OrderByDescending(r => r.AskedAtUtc).ThenBy(r => r.RequestId, StringComparer.Ordinal)
            .Select(ToRecord).ToList();
    }

    /// <summary>
    /// Move a WAITING request to <paramref name="status"/> - allowed (with the amount and the link it set up), declined,
    /// or ended. True when this call moved it; false when it had already been answered or ended, and then nothing
    /// changed.
    /// </summary>
    public bool TryAnswer(TenantId tenant, string requestId, string status, string answeredBy, DateTime nowUtc,
        string? amount = null, string? linkId = null)
    {
        FileLog.Write($"[FleetMessageLinkRequestStore] TryAnswer: tenant={tenant.ToLogString()}, request={requestId}, status={status}, by={answeredBy}");
        try
        {
            if (status is not (FleetMessageLinkRequestStatuses.Allowed or FleetMessageLinkRequestStatuses.Declined
                or FleetMessageLinkRequestStatuses.Ended))
                throw new ArgumentException($"'{status}' is not an answer.", nameof(status));
            var id = RequestKey(requestId);
            if (id is null) return false;
            var by = Cap(answeredBy);
            var at = Utc(nowUtc);
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var changed = ctx.FleetMessageLinkRequests
                    .Where(r => r.RequestId == id && r.Status == FleetMessageLinkRequestStatuses.Pending)
                    .ExecuteUpdate(u => u
                        .SetProperty(r => r.Status, status)
                        .SetProperty(r => r.AnsweredBy, by)
                        .SetProperty(r => r.AnsweredAtUtc, at)
                        .SetProperty(r => r.Amount, amount)
                        .SetProperty(r => r.LinkId, linkId)) == 1;
                FileLog.Write($"[FleetMessageLinkRequestStore] TryAnswer: request={id}, changed={changed}");
                return changed;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkRequestStore] TryAnswer FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Put an allowed request back to waiting, because setting up its link failed after the answer claimed it. Changes
    /// the request only while it is allowed and has no link, so a request whose link exists is never reopened. True when
    /// this call reopened it.
    /// </summary>
    public bool Reopen(TenantId tenant, string requestId)
    {
        var id = RequestKey(requestId) ?? throw new ArgumentException($"'{requestId}' is not a request id.", nameof(requestId));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var changed = ctx.FleetMessageLinkRequests
                .Where(r => r.RequestId == id && r.Status == FleetMessageLinkRequestStatuses.Allowed && r.LinkId == null)
                .ExecuteUpdate(u => u
                    .SetProperty(r => r.Status, FleetMessageLinkRequestStatuses.Pending)
                    .SetProperty(r => r.AnsweredBy, (string?)null)
                    .SetProperty(r => r.AnsweredAtUtc, (DateTime?)null)
                    .SetProperty(r => r.Amount, (string?)null)) == 1;
            FileLog.Write($"[FleetMessageLinkRequestStore] Reopen: request={id}, reopened={changed}");
            return changed;
        }
    }

    /// <summary>Note on an allowed request the link its answer set up. The request was moved to allowed first, by
    /// <see cref="TryAnswer"/>, so only the answer that won can set up a link and note it here.</summary>
    public void NoteLink(TenantId tenant, string requestId, string linkId)
    {
        var id = RequestKey(requestId) ?? throw new ArgumentException($"'{requestId}' is not a request id.", nameof(requestId));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            ctx.FleetMessageLinkRequests
                .Where(r => r.RequestId == id && r.Status == FleetMessageLinkRequestStatuses.Allowed)
                .ExecuteUpdate(u => u.SetProperty(r => r.LinkId, linkId));
        }
        FileLog.Write($"[FleetMessageLinkRequestStore] NoteLink: request={id}, link={linkId}");
    }

    private static string Canonical(string sessionId, string param)
        => Guid.TryParse(sessionId, out var parsed)
            ? parsed.ToString("D")
            : throw new ArgumentException($"'{sessionId}' is not a session id.", param);

    private static string? RequestKey(string? requestId)
    {
        var id = requestId?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(id) || id.Length > 32 ? null : id;
    }

    private static string Cap(string value) => value.Length > 256 ? value[..256] : value;

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    // The database hands a stored moment back with no kind; every moment stored here was written as UTC.
    private static DateTime? AsUtc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static FleetMessageLinkRequest ToRecord(FleetMessageLinkRequestEntity e) => new(
        e.RequestId, e.RequesterSessionId, e.TargetSessionId, e.Reason, e.Status,
        DateTime.SpecifyKind(e.AskedAtUtc, DateTimeKind.Utc), e.AnsweredBy, AsUtc(e.AnsweredAtUtc), e.Amount, e.LinkId);
}
