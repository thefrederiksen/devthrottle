using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Messaging;

/// <summary>What a new message says, before the store has decided whether to write it.</summary>
/// <param name="RecipientSessionId">The inbox it goes into.</param>
/// <param name="SenderSessionId">The sending session, or null for a system notice.</param>
/// <param name="SenderName">The sender's roster name at the moment of sending, or null.</param>
/// <param name="SenderMachine">The sender's machine at the moment of sending, or null.</param>
/// <param name="Kind">One of <see cref="FleetMessageKinds"/>.</param>
/// <param name="Text">The full text.</param>
public sealed record FleetMessageDraft(
    string RecipientSessionId,
    string? SenderSessionId,
    string? SenderName,
    string? SenderMachine,
    string Kind,
    string Text);

/// <summary>The facts about the sender's recent sends the policy needs, read from the table.</summary>
/// <param name="SentBySenderInWindow">Rate-counted messages the sender wrote inside the window.</param>
/// <param name="LastSentToRecipientUtc">When the sender last wrote to this recipient, or null.</param>
/// <param name="RecipientHasUnreadDuplicate">The recipient holds an unread message from this sender with
/// exactly this text.</param>
public readonly record struct FleetMessageHistory(
    int SentBySenderInWindow,
    DateTime? LastSentToRecipientUtc,
    bool RecipientHasUnreadDuplicate);

/// <summary>What one inbox read returned.</summary>
/// <param name="Unread">The messages this read marked read, oldest first. Each is returned exactly once.</param>
/// <param name="Recent">Messages read before this call, newest first, when the caller asked for them - at most
/// the cap the caller passed.</param>
/// <param name="RecentTotal">How many messages were read inside the window, before the cap was applied. Greater
/// than <c>Recent.Count</c> exactly when the answer was truncated.</param>
public sealed record FleetInboxRead(IReadOnlyList<FleetMessageEntity> Unread, IReadOnlyList<FleetMessageEntity> Recent,
    int RecentTotal)
{
    /// <summary>True when more messages were read inside the window than <see cref="Recent"/> holds.</summary>
    public bool RecentTruncated => RecentTotal > Recent.Count;
}

/// <summary>
/// The fleet message inbox (the Message Load mission, slice 1), over the <c>fleet_messages</c> table.
///
/// TENANT-PARTITIONED BY CONSTRUCTION, like <see cref="Wingman.TurnVerdictStore"/>: every method takes the
/// tenant explicitly and opens its context with <see cref="GatewayDatabase.CreateContext(TenantId)"/>. A
/// session id is minted by a Director and two accounts can hold the same one, so no method takes a bare id.
///
/// CHECK AND WRITE ARE ONE STEP. <see cref="TryEnqueue"/> reads the sender's history, asks the caller's
/// decision, and writes the row under one lock. Read-then-write as two calls would let two sends from one
/// session both see five messages in the hour and both be written, which is the limit not holding. The lock
/// is per process; the hosted Gateway runs one instance, and a staging slot that is warming serves no
/// traffic until the swap.
///
/// THE LIMITS ARE COUNTED FROM THE ROWS. There is no counter held in memory, so a Gateway restart does not
/// hand every session a fresh hour.
/// </summary>
public sealed class FleetMessageStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FleetMessageStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>The hash stored beside a message's text, used to find an unread identical message.</summary>
    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();

    /// <summary>
    /// Read the sender's history, let <paramref name="decide"/> rule on it, and write the record when the
    /// ruling is <see cref="FleetMessageOutcome.Queued"/> - all under one lock. Returns the ruling and, when
    /// it queued, the row that was written.
    /// </summary>
    /// <param name="tenant">The account.</param>
    /// <param name="draft">The message.</param>
    /// <param name="nowUtc">The moment of the send; also the row's created moment.</param>
    /// <param name="senderWindow">How far back the sender's rate count looks.</param>
    /// <param name="decide">The ruling, given the history. Pure: it must not touch the store.</param>
    public (FleetMessageVerdict Verdict, FleetMessageEntity? Written) TryEnqueue(
        TenantId tenant,
        FleetMessageDraft draft,
        DateTime nowUtc,
        TimeSpan senderWindow,
        Func<FleetMessageHistory, FleetMessageVerdict> decide)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(decide);
        if (string.IsNullOrWhiteSpace(draft.RecipientSessionId))
            throw new ArgumentException("A message names the session it is for.", nameof(draft));
        if (string.IsNullOrWhiteSpace(draft.Kind))
            throw new ArgumentException("A message names its kind.", nameof(draft));

        var now = Utc(nowUtc);
        var hash = HashText(draft.Text);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var history = ReadHistory(ctx, draft, hash, now, senderWindow);
            var verdict = decide(history);
            if (!verdict.Queued) return (verdict, null);

            var row = new FleetMessageEntity
            {
                TenantId = ctx.ActiveTenant!,
                MessageId = Guid.NewGuid().ToString("N"),
                RecipientSessionId = Id(draft.RecipientSessionId)!,
                SenderSessionId = Id(draft.SenderSessionId),
                SenderName = draft.SenderName,
                SenderMachine = draft.SenderMachine,
                Kind = draft.Kind,
                Text = draft.Text,
                TextHash = hash,
                CreatedAtUtc = now,
            };
            ctx.FleetMessages.Add(row);
            ctx.SaveChanges();
            return (verdict, row);
        }
    }

    private static FleetMessageHistory ReadHistory(
        GatewayDbContext ctx, FleetMessageDraft draft, string hash, DateTime now, TimeSpan senderWindow)
    {
        var sender = Id(draft.SenderSessionId);
        var recipient = Id(draft.RecipientSessionId)!;
        if (sender is null)
        {
            // A system notice has no sender, so it has no rate history. It can still repeat itself: a notice
            // identical to one the recipient has not read yet adds nothing.
            var dupSystem = ctx.FleetMessages.Any(m => m.RecipientSessionId == recipient
                && m.SenderSessionId == null && m.ReadAtUtc == null && m.TextHash == hash);
            return new FleetMessageHistory(0, null, dupSystem);
        }

        var since = now - senderWindow;
        // A whole-account broadcast is a human's act, authorized by a grant, so it is not charged to the
        // agent that carried it. Every other kind a session sends counts.
        var count = ctx.FleetMessages.Count(m => m.SenderSessionId == sender
            && m.CreatedAtUtc > since && m.Kind != FleetMessageKinds.Everyone);
        var last = ctx.FleetMessages
            .Where(m => m.SenderSessionId == sender && m.RecipientSessionId == recipient
                && m.Kind != FleetMessageKinds.Everyone)
            .OrderByDescending(m => m.CreatedAtUtc)
            .Select(m => (DateTime?)m.CreatedAtUtc)
            .FirstOrDefault();
        var dup = ctx.FleetMessages.Any(m => m.RecipientSessionId == recipient
            && m.SenderSessionId == sender && m.ReadAtUtc == null && m.TextHash == hash);
        return new FleetMessageHistory(count, last is null ? null : Utc(last.Value), dup);
    }

    /// <summary>
    /// Read one session's inbox: every unread message is returned in full and marked read in the same step,
    /// so the read IS the acknowledgement. With <paramref name="includeRecent"/>, messages read BEFORE this
    /// call and no longer ago than <paramref name="recentWindow"/> (the product's
    /// <see cref="FleetMessageLimits.RecentReadWindow"/> when omitted) come back too, newest first, unchanged,
    /// at most <paramref name="recentCap"/> of them (<see cref="FleetMessageLimits.RecentReadCap"/> when
    /// omitted), with the uncapped count beside them (inspection 2, ruling 1).
    ///
    /// The recent rows are read BEFORE the lock, untracked: they are never written, and holding the one store
    /// lock while a large answer is materialised would stall every send and read on the Gateway behind it.
    ///
    /// KNOWN GAP, ACCEPTED (inspection 2, ruling 2): the unread rows are marked read and saved here, before the
    /// caller has built or sent the response. If that response is lost - the connection drops, the process
    /// dies - the next plain read will not return those messages. They are recoverable with
    /// <paramref name="includeRecent"/> for <see cref="FleetMessageLimits.RecentReadWindow"/> after the read,
    /// and only if the recipient knows to ask; after that they are gone from every read. Read-marks-read is the
    /// protocol of design ruling 9, so this interval is disclosed rather than closed.
    /// </summary>
    public FleetInboxRead ReadInbox(TenantId tenant, string recipientSessionId, DateTime nowUtc, bool includeRecent,
        TimeSpan? recentWindow = null, int? recentCap = null)
    {
        if (string.IsNullOrWhiteSpace(recipientSessionId))
            throw new ArgumentException("An inbox belongs to a session.", nameof(recipientSessionId));
        var window = recentWindow ?? FleetMessageLimits.Default.RecentReadWindow;
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recentWindow), window, "The recent-read window must be positive.");
        var cap = recentCap ?? FleetMessageLimits.Default.RecentReadCap;
        if (cap <= 0)
            throw new ArgumentOutOfRangeException(nameof(recentCap), cap, "The recent-read cap must be positive.");
        var recipient = Id(recipientSessionId)!;
        var now = Utc(nowUtc);
        var readSince = now - window;

        var recent = new List<FleetMessageEntity>();
        var recentTotal = 0;
        if (includeRecent)
        {
            using var readCtx = _db.CreateContext(tenant);
            var inWindow = readCtx.FleetMessages.AsNoTracking()
                .Where(m => m.RecipientSessionId == recipient && m.ReadAtUtc != null && m.ReadAtUtc >= readSince);
            recentTotal = inWindow.Count();
            recent = inWindow
                .OrderByDescending(m => m.ReadAtUtc)
                .ThenByDescending(m => m.CreatedAtUtc)
                .Take(cap)
                .ToList();
        }

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var unread = ctx.FleetMessages
                .Where(m => m.RecipientSessionId == recipient && m.ReadAtUtc == null)
                .OrderBy(m => m.CreatedAtUtc)
                .ToList();
            foreach (var m in unread)
            {
                m.ReadAtUtc = now;
                // A READ UNDOES STUCK (slice 2, ruling 11). Stuck means "rung three times and never read"; a
                // message that has now been read is not that any more. The sender's stuck notice stays in its
                // inbox - it was true when it was written - and no second notice is sent.
                m.StuckAtUtc = null;
            }
            if (unread.Count > 0) ctx.SaveChanges();
            return new FleetInboxRead(unread, recent, Math.Max(recentTotal, recent.Count));
        }
    }

    /// <summary>How many messages wait unread in one session's inbox. Reads, changes nothing.</summary>
    public int CountUnread(TenantId tenant, string recipientSessionId)
    {
        var recipient = Id(recipientSessionId) ?? "";
        using var ctx = _db.CreateContext(tenant);
        return ctx.FleetMessages.Count(m => m.RecipientSessionId == recipient && m.ReadAtUtc == null);
    }

    /// <summary>
    /// The sessions in this tenant holding at least one OPEN message - unread and not stuck. These are the
    /// only sessions the doorbell (slice 2) has anything to ring for. Reads, changes nothing.
    /// </summary>
    public IReadOnlyList<string> RecipientsWithOpenMessages(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.FleetMessages.AsNoTracking()
            .Where(m => m.ReadAtUtc == null && m.StuckAtUtc == null)
            .Select(m => m.RecipientSessionId)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// One session's OPEN messages (unread, not stuck), oldest first, untracked. The doorbell reads their ring
    /// counts to decide which are due; it never reads their text.
    /// </summary>
    public IReadOnlyList<FleetMessageEntity> OpenMessagesFor(TenantId tenant, string recipientSessionId)
    {
        var recipient = Id(recipientSessionId) ?? "";
        using var ctx = _db.CreateContext(tenant);
        return ctx.FleetMessages.AsNoTracking()
            .Where(m => m.RecipientSessionId == recipient && m.ReadAtUtc == null && m.StuckAtUtc == null)
            .OrderBy(m => m.CreatedAtUtc)
            .ToList();
    }

    /// <summary>
    /// Record that the doorbell was rung for these messages: each one's ring count goes up by one and its last
    /// ring time becomes <paramref name="nowUtc"/>. A message that was read or marked stuck in the moment since
    /// the ring was decided is left alone. Returns how many rows were changed.
    /// </summary>
    public int MarkRung(TenantId tenant, IReadOnlyCollection<string> messageIds, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0) return 0;
        var now = Utc(nowUtc);
        var ids = messageIds.ToList();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.FleetMessages
                .Where(m => ids.Contains(m.MessageId) && m.ReadAtUtc == null && m.StuckAtUtc == null)
                .ToList();
            foreach (var m in rows)
            {
                m.RingCount++;
                m.LastRungAtUtc = now;
            }
            if (rows.Count > 0) ctx.SaveChanges();
            return rows.Count;
        }
    }

    /// <summary>
    /// Mark stuck every open message in this tenant that <paramref name="isStuck"/> rules stuck, and return the
    /// rows that were marked. Decided and written under the store lock, so a read that lands first wins: a
    /// message read before this runs is not open any more and is never marked.
    /// </summary>
    public IReadOnlyList<FleetMessageEntity> MarkStuck(TenantId tenant, DateTime nowUtc, Func<FleetMessageEntity, bool> isStuck)
    {
        ArgumentNullException.ThrowIfNull(isStuck);
        var now = Utc(nowUtc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            // Only rung, open messages can be stuck; which of them ARE is decided by the caller's ruling alone, so
            // the one definition of "stuck" lives in FleetRingSchedule and is not restated in this query.
            var candidates = ctx.FleetMessages
                .Where(m => m.ReadAtUtc == null && m.StuckAtUtc == null && m.RingCount > 0)
                .ToList();
            var stuck = candidates.Where(isStuck).ToList();
            foreach (var m in stuck) m.StuckAtUtc = now;
            if (stuck.Count > 0) ctx.SaveChanges();
            return stuck;
        }
    }

    /// <summary>
    /// Delete this tenant's messages written before <paramref name="cutoffUtc"/> that are read or stuck.
    /// An open message is kept whatever its age: deleting a message nobody has read would make it look as
    /// though it had never been sent. Returns the number deleted.
    /// </summary>
    public int PurgeOlderThan(TenantId tenant, DateTime cutoffUtc)
    {
        var cutoff = Utc(cutoffUtc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var old = ctx.FleetMessages
                .Where(m => m.CreatedAtUtc < cutoff && (m.ReadAtUtc != null || m.StuckAtUtc != null))
                .ToList();
            if (old.Count == 0) return 0;
            ctx.FleetMessages.RemoveRange(old);
            ctx.SaveChanges();
            return old.Count;
        }
    }

    /// <summary>
    /// One spelling of a session id. The recipient's id arrives from the roster and the reader's from its session
    /// key; a Director writes both as lower-case identifiers today, but an inbox keyed on two spellings of one id
    /// would hold messages its owner can never read, so every id is trimmed and lower-cased on the way in.
    /// </summary>
    private static string? Id(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim().ToLowerInvariant();

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
