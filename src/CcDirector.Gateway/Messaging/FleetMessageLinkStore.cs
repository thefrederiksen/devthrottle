using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Messaging;

/// <summary>How much talking a message link allows (issue #3548). The owner picks one when he sets the link up.</summary>
public static class FleetMessageLinkAmounts
{
    /// <summary>The sender may send one message, and it may not ask for a reply. Then the link is used up.</summary>
    public const string Once = "once";

    /// <summary>The sender may send one message, which may ask for a reply; the recipient answers it once. Then the link
    /// is used up - the reply belongs to the message, so it is still allowed after.</summary>
    public const string OnceWithReply = "once-with-reply";

    /// <summary>The two may message each other, both ways, as much as they need, until the link is removed or either
    /// session ends.</summary>
    public const string Ongoing = "ongoing";

    /// <summary>Every amount, in the order they are offered.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Once, OnceWithReply, Ongoing };

    /// <summary>True for one of <see cref="All"/>, compared exactly.</summary>
    public static bool IsKnown(string? amount) => amount is Once or OnceWithReply or Ongoing;
}

/// <summary>Where a message link stands.</summary>
public static class FleetMessageLinkStatuses
{
    /// <summary>It carries messages now.</summary>
    public const string Live = "live";

    /// <summary>A one-time link whose one message was sent.</summary>
    public const string Used = "used";

    /// <summary>The owner, or the Fleet Manager for him, removed it - or set up a new link between the same two
    /// sessions, which replaces it.</summary>
    public const string Removed = "removed";

    /// <summary>One of its two sessions ended.</summary>
    public const string Ended = "ended";
}

/// <summary>One message link, as a caller reads it.</summary>
public sealed record FleetMessageLink(
    string LinkId,
    string SenderSessionId,
    string RecipientSessionId,
    string Amount,
    string Status,
    string SetUpBy,
    DateTime SetUpAtUtc,
    string? UsedMessageId,
    DateTime? UsedAtUtc,
    string? EndedBy,
    DateTime? EndedAtUtc);

/// <summary>What setting up a link did.</summary>
/// <param name="Link">The new link.</param>
/// <param name="Replaced">The live links between the same two sessions that it replaced; each is now removed.</param>
public sealed record FleetMessageLinkSetUp(FleetMessageLink Link, IReadOnlyList<FleetMessageLink> Replaced);

/// <summary>
/// THE MESSAGE LINKS, per account, over the <c>fleet_message_links</c> table (issue #3548). A link lets two sessions
/// that are not owner and worker message each other, as much as the owner allowed when he set it up.
///
/// TENANT-PARTITIONED BY CONSTRUCTION, like <see cref="FleetMessageStore"/>: every method takes the tenant and opens
/// its context with <see cref="GatewayDatabase.CreateContext(TenantId)"/>.
///
/// WHO WRITES IT. Setting up and removing a link is reached only through routes that admit the owner's own device or a
/// raised session (<c>FleetMessageLinkEndpoints</c>). Using up a one-time link is done by
/// <see cref="FleetMessageStore.TryEnqueue"/>, in the same transaction that writes the message, through
/// <see cref="TryUseIn"/>. Ending links with a session is done when the session's key is revoked.
///
/// ONE LIVE LINK PER PAIR. Setting up a link between two sessions that already have a live one - in either direction -
/// removes the old one, so a send never has two links to choose between.
/// </summary>
public sealed class FleetMessageLinkStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FleetMessageLinkStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Set up a link from <paramref name="senderSessionId"/> to <paramref name="recipientSessionId"/>.</summary>
    /// <exception cref="ArgumentException">An id is not a session id, the two are the same session, or the amount is
    /// not one of <see cref="FleetMessageLinkAmounts"/>.</exception>
    public FleetMessageLinkSetUp SetUp(TenantId tenant, string senderSessionId, string recipientSessionId, string amount,
        string setUpBy, DateTime nowUtc)
    {
        FileLog.Write($"[FleetMessageLinkStore] SetUp: tenant={tenant.ToLogString()}, from={senderSessionId}, to={recipientSessionId}, amount={amount}, by={setUpBy}");
        try
        {
            var from = Canonical(senderSessionId, nameof(senderSessionId));
            var to = Canonical(recipientSessionId, nameof(recipientSessionId));
            if (from == to)
                throw new ArgumentException("A link joins two different sessions.", nameof(recipientSessionId));
            if (!FleetMessageLinkAmounts.IsKnown(amount))
                throw new ArgumentException(
                    $"'{amount}' is not an amount; use one of {string.Join(", ", FleetMessageLinkAmounts.All)}.", nameof(amount));
            var by = Cap(setUpBy);
            var now = Utc(nowUtc);

            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                using var tx = ctx.Database.BeginTransaction();
                var linkId = Guid.NewGuid().ToString("N");

                var old = ctx.FleetMessageLinks
                    .Where(l => l.Status == FleetMessageLinkStatuses.Live
                        && ((l.SenderSessionId == from && l.RecipientSessionId == to)
                            || (l.SenderSessionId == to && l.RecipientSessionId == from)))
                    .ToList();
                foreach (var row in old)
                {
                    row.Status = FleetMessageLinkStatuses.Removed;
                    row.EndedBy = Cap($"{by} (replaced by link {linkId})");
                    row.EndedAtUtc = now;
                }

                var link = new FleetMessageLinkEntity
                {
                    LinkId = linkId,
                    SenderSessionId = from,
                    RecipientSessionId = to,
                    Amount = amount,
                    Status = FleetMessageLinkStatuses.Live,
                    SetUpBy = by,
                    SetUpAtUtc = now,
                };
                link.TenantId = ctx.ActiveTenant!;
                ctx.FleetMessageLinks.Add(link);
                ctx.SaveChanges();
                tx.Commit();

                FileLog.Write($"[FleetMessageLinkStore] SetUp: link={linkId} is live, replaced=[{string.Join(", ", old.Select(o => o.LinkId))}]");
                return new FleetMessageLinkSetUp(ToRecord(link), old.Select(ToRecord).ToList());
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkStore] SetUp FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>One link by its id, in any status; null when this account has none with that id.</summary>
    public FleetMessageLink? Find(TenantId tenant, string? linkId)
    {
        var id = LinkKey(linkId);
        if (id is null) return null;
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.FleetMessageLinks.AsNoTracking().FirstOrDefault(l => l.LinkId == id);
        return row is null ? null : ToRecord(row);
    }

    /// <summary>Every live link, and every link that stopped at or after <paramref name="stoppedSinceUtc"/>; newest
    /// first.</summary>
    public IReadOnlyList<FleetMessageLink> List(TenantId tenant, DateTime stoppedSinceUtc)
    {
        var since = Utc(stoppedSinceUtc);
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.FleetMessageLinks.AsNoTracking()
            .Where(l => l.Status == FleetMessageLinkStatuses.Live
                || (l.EndedAtUtc != null && l.EndedAtUtc >= since)
                || (l.UsedAtUtc != null && l.UsedAtUtc >= since))
            .ToList();
        FileLog.Write($"[FleetMessageLinkStore] List: tenant={tenant.ToLogString()}, count={rows.Count}");
        return rows.OrderByDescending(l => l.SetUpAtUtc).ThenBy(l => l.LinkId, StringComparer.Ordinal)
            .Select(ToRecord).ToList();
    }

    /// <summary>
    /// Remove a live link. Returns the link as it now stands, or null when this account has no link with that id.
    /// A link that is no longer live is returned unchanged: removing it again changes nothing.
    /// </summary>
    public FleetMessageLink? Remove(TenantId tenant, string linkId, string removedBy, DateTime nowUtc)
    {
        FileLog.Write($"[FleetMessageLinkStore] Remove: tenant={tenant.ToLogString()}, link={linkId}, by={removedBy}");
        try
        {
            var id = LinkKey(linkId);
            if (id is null) return null;
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var row = ctx.FleetMessageLinks.FirstOrDefault(l => l.LinkId == id);
                if (row is null) return null;
                if (row.Status == FleetMessageLinkStatuses.Live)
                {
                    row.Status = FleetMessageLinkStatuses.Removed;
                    row.EndedBy = Cap(removedBy);
                    row.EndedAtUtc = Utc(nowUtc);
                    ctx.SaveChanges();
                }
                FileLog.Write($"[FleetMessageLinkStore] Remove: link={id}, status={row.Status}");
                return ToRecord(row);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkStore] Remove FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>A session ended, so every live link it is part of ends with it. Returns the links ended. An id that is
    /// not a session id has none.</summary>
    public IReadOnlyList<FleetMessageLink> EndWithSession(TenantId tenant, string sessionId, DateTime nowUtc)
    {
        FileLog.Write($"[FleetMessageLinkStore] EndWithSession: tenant={tenant.ToLogString()}, session={sessionId}");
        try
        {
            if (!Guid.TryParse(sessionId, out var parsed)) return Array.Empty<FleetMessageLink>();
            var sid = parsed.ToString("D");
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var rows = ctx.FleetMessageLinks
                    .Where(l => l.Status == FleetMessageLinkStatuses.Live
                        && (l.SenderSessionId == sid || l.RecipientSessionId == sid))
                    .ToList();
                if (rows.Count == 0) return Array.Empty<FleetMessageLink>();
                var now = Utc(nowUtc);
                foreach (var row in rows)
                {
                    row.Status = FleetMessageLinkStatuses.Ended;
                    row.EndedBy = $"gateway: session {sid} ended";
                    row.EndedAtUtc = now;
                }
                ctx.SaveChanges();
                FileLog.Write($"[FleetMessageLinkStore] EndWithSession: session={sid}, ended=[{string.Join(", ", rows.Select(r => r.LinkId))}]");
                return rows.Select(ToRecord).ToList();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetMessageLinkStore] EndWithSession FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// The live link that may carry a message from <paramref name="senderSessionId"/> to
    /// <paramref name="recipientSessionId"/>, read inside the caller's context: a link set up in that direction, or an
    /// <see cref="FleetMessageLinkAmounts.Ongoing"/> link set up the other way. Null when there is none, or when either
    /// id is not a session id.
    /// </summary>
    internal static FleetMessageLinkFacts? FindLiveIn(GatewayDbContext ctx, string? senderSessionId, string? recipientSessionId)
    {
        if (!Guid.TryParse(senderSessionId, out var s) || !Guid.TryParse(recipientSessionId, out var r)) return null;
        var from = s.ToString("D");
        var to = r.ToString("D");
        var row = ctx.FleetMessageLinks.AsNoTracking()
            .Where(l => l.Status == FleetMessageLinkStatuses.Live
                && ((l.SenderSessionId == from && l.RecipientSessionId == to)
                    || (l.SenderSessionId == to && l.RecipientSessionId == from && l.Amount == FleetMessageLinkAmounts.Ongoing)))
            .OrderByDescending(l => l.SetUpAtUtc)
            .FirstOrDefault();
        return row is null ? null : new FleetMessageLinkFacts(row.LinkId, row.Amount, Reversed: row.SenderSessionId != from);
    }

    /// <summary>
    /// Use up a one-time link for <paramref name="messageId"/>, inside the caller's context and transaction. The row
    /// changes only while it is still live, so a link removed a moment ago is not used, and two messages can never both
    /// use one link. True when this call used it.
    /// </summary>
    internal static bool TryUseIn(GatewayDbContext ctx, string linkId, string messageId, DateTime nowUtc)
    {
        var now = Utc(nowUtc);
        var changed = ctx.FleetMessageLinks
            .Where(l => l.LinkId == linkId && l.Status == FleetMessageLinkStatuses.Live)
            .ExecuteUpdate(u => u
                .SetProperty(l => l.Status, FleetMessageLinkStatuses.Used)
                .SetProperty(l => l.UsedMessageId, messageId)
                .SetProperty(l => l.UsedAtUtc, now));
        return changed == 1;
    }

    private static string Canonical(string sessionId, string param)
        => Guid.TryParse(sessionId, out var parsed)
            ? parsed.ToString("D")
            : throw new ArgumentException($"'{sessionId}' is not a session id.", param);

    private static string? LinkKey(string? linkId)
    {
        var id = linkId?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(id) || id.Length > 32 ? null : id;
    }

    private static string Cap(string value) => value.Length > 256 ? value[..256] : value;

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    // The database hands a stored moment back with no kind; every moment stored here was written as UTC.
    private static DateTime? AsUtc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static FleetMessageLink ToRecord(FleetMessageLinkEntity e) => new(
        e.LinkId, e.SenderSessionId, e.RecipientSessionId, e.Amount, e.Status, e.SetUpBy,
        DateTime.SpecifyKind(e.SetUpAtUtc, DateTimeKind.Utc), e.UsedMessageId, AsUtc(e.UsedAtUtc), e.EndedBy, AsUtc(e.EndedAtUtc));
}
