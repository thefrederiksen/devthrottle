using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;

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
/// <param name="Recent">Messages read before this call, newest first, when the caller asked for them.</param>
public sealed record FleetInboxRead(IReadOnlyList<FleetMessageEntity> Unread, IReadOnlyList<FleetMessageEntity> Recent);

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
    /// <summary>The most already-read messages one inbox read returns with <c>all</c>.</summary>
    public const int RecentReadCount = 20;

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
                RecipientSessionId = draft.RecipientSessionId.Trim(),
                SenderSessionId = string.IsNullOrWhiteSpace(draft.SenderSessionId) ? null : draft.SenderSessionId.Trim(),
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
        var sender = string.IsNullOrWhiteSpace(draft.SenderSessionId) ? null : draft.SenderSessionId.Trim();
        var recipient = draft.RecipientSessionId.Trim();
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
    /// so the read IS the acknowledgement. With <paramref name="includeRecent"/>, the most recent messages
    /// read BEFORE this call come back too, newest first, and are not changed.
    /// </summary>
    public FleetInboxRead ReadInbox(TenantId tenant, string recipientSessionId, DateTime nowUtc, bool includeRecent)
    {
        if (string.IsNullOrWhiteSpace(recipientSessionId))
            throw new ArgumentException("An inbox belongs to a session.", nameof(recipientSessionId));
        var recipient = recipientSessionId.Trim();
        var now = Utc(nowUtc);

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var recent = includeRecent
                ? ctx.FleetMessages
                    .Where(m => m.RecipientSessionId == recipient && m.ReadAtUtc != null)
                    .OrderByDescending(m => m.ReadAtUtc)
                    .ThenByDescending(m => m.CreatedAtUtc)
                    .Take(RecentReadCount)
                    .ToList()
                : new List<FleetMessageEntity>();

            var unread = ctx.FleetMessages
                .Where(m => m.RecipientSessionId == recipient && m.ReadAtUtc == null)
                .OrderBy(m => m.CreatedAtUtc)
                .ToList();
            foreach (var m in unread) m.ReadAtUtc = now;
            if (unread.Count > 0) ctx.SaveChanges();
            return new FleetInboxRead(unread, recent);
        }
    }

    /// <summary>How many messages wait unread in one session's inbox. Reads, changes nothing.</summary>
    public int CountUnread(TenantId tenant, string recipientSessionId)
    {
        var recipient = (recipientSessionId ?? "").Trim();
        using var ctx = _db.CreateContext(tenant);
        return ctx.FleetMessages.Count(m => m.RecipientSessionId == recipient && m.ReadAtUtc == null);
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

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
