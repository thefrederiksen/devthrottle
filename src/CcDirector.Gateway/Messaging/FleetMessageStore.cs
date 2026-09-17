using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
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
/// <param name="ReplyByUtc">When a wanted reply is due. Set only on a message that asks for a reply; the store
/// then mints its correlation id (slice 3).</param>
/// <param name="CorrelationId">On a reply or a no-reply notice, the correlation id of the message it is about.</param>
/// <param name="InReplyToMessageId">On a reply or a no-reply notice, the id of the message it is about.</param>
public sealed record FleetMessageDraft(
    string RecipientSessionId,
    string? SenderSessionId,
    string? SenderName,
    string? SenderMachine,
    string Kind,
    string Text,
    DateTime? ReplyByUtc = null,
    string? CorrelationId = null,
    string? InReplyToMessageId = null);

/// <summary>The facts about the sender's recent sends the policy needs, read from the table.</summary>
/// <param name="SentBySenderInWindow">Rate-counted messages the sender wrote inside the window.</param>
/// <param name="LastSentToRecipientUtc">When the sender last wrote to this recipient, or null.</param>
/// <param name="RecipientHasUnreadDuplicate">The recipient holds an unread message from this sender with
/// exactly this text.</param>
/// <param name="DuplicateMessageId">The id of that waiting message, when there is one.</param>
/// <param name="DuplicateCorrelationId">That waiting message's correlation id, when it asked for a reply.</param>
public readonly record struct FleetMessageHistory(
    int SentBySenderInWindow,
    DateTime? LastSentToRecipientUtc,
    bool RecipientHasUnreadDuplicate,
    string? DuplicateMessageId = null,
    string? DuplicateCorrelationId = null);

/// <summary>Why a reply found no message to answer (slice 3).</summary>
public enum FleetReplyMiss
{
    /// <summary>The message was found; the policy decided.</summary>
    None,

    /// <summary>No message in this account has that id or correlation id.</summary>
    NotFound,

    /// <summary>The message exists but did not ask for a reply.</summary>
    NoReplyWanted,
}

/// <summary>What one reply came to (slice 3).</summary>
/// <param name="Miss">Whether the original was found and asked for a reply.</param>
/// <param name="Verdict">The policy's ruling; default when <see cref="Miss"/> is not <see cref="FleetReplyMiss.None"/>.</param>
/// <param name="Written">The reply row, when it was queued.</param>
/// <param name="Original">The message the id named, when one was found.</param>
public sealed record FleetReplyResult(
    FleetReplyMiss Miss,
    FleetMessageVerdict Verdict,
    FleetMessageEntity? Written,
    FleetMessageEntity? Original);

/// <summary>What one inbox read returned.</summary>
/// <param name="Unread">The messages this read marked read, oldest first. Each is returned exactly once.</param>
/// <param name="Recent">Messages read before this call, newest first, when the caller asked for them - at most
/// the cap the caller passed.</param>
/// <param name="RecentTotal">How many messages were read inside the window, before the cap was applied. Greater
/// than <c>Recent.Count</c> exactly when the answer was truncated.</param>
/// <param name="Originals">For every returned reply and no-reply notice, the question it is about, keyed by
/// message id - only questions the reader itself sent, and only while they are still kept (slice 3). Null means
/// none.</param>
public sealed record FleetInboxRead(IReadOnlyList<FleetMessageEntity> Unread, IReadOnlyList<FleetMessageEntity> Recent,
    int RecentTotal, IReadOnlyDictionary<string, FleetMessageEntity>? Originals = null)
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
public sealed class FleetMessageStore : IFleetInboxLineSource
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

            var row = NewRow(ctx, draft, hash, now);
            ctx.FleetMessages.Add(row);
            ctx.SaveChanges();
            return (verdict, row);
        }
    }

    /// <summary>
    /// ONE REPLY, DECIDED AND WRITTEN IN ONE STEP (slice 3, ruling 10). Finds the message <paramref name="id"/>
    /// names - by its correlation id or its own message id - reads the replier's history, lets
    /// <paramref name="decide"/> rule, and when it queues writes the reply into the ORIGINAL SENDER's inbox and
    /// stamps the original's <see cref="FleetMessageEntity.RepliedAtUtc"/> in the same save, under the store
    /// lock. The original is looked up across the whole account, not only the replier's inbox, so the policy -
    /// not the lookup - is what refuses a reply from a session the message was not sent to, and it says why.
    ///
    /// A reply after the deadline is written like any other; a no-reply notice already sent stays sent.
    /// </summary>
    /// <param name="tenant">The account.</param>
    /// <param name="replierSessionId">The session replying - from its session key.</param>
    /// <param name="replierName">Its roster name, or null.</param>
    /// <param name="replierMachine">Its machine, or null.</param>
    /// <param name="id">The correlation id or the message id of the message being answered.</param>
    /// <param name="text">The reply.</param>
    /// <param name="nowUtc">The moment of the reply.</param>
    /// <param name="decide">The ruling, given the history and the original. Pure: it must not touch the store.</param>
    public FleetReplyResult TryReply(
        TenantId tenant,
        string replierSessionId,
        string? replierName,
        string? replierMachine,
        string id,
        string text,
        DateTime nowUtc,
        Func<FleetMessageHistory, FleetReplyOriginal, FleetMessageVerdict> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        if (string.IsNullOrWhiteSpace(replierSessionId))
            throw new ArgumentException("A reply is sent by a session.", nameof(replierSessionId));
        var key = Id(id);
        if (key is null) return new FleetReplyResult(FleetReplyMiss.NotFound, default, null, null);

        var now = Utc(nowUtc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            // An ORIGINAL is a row that is not itself about another message: a reply and a no-reply notice carry the
            // original's correlation id too, and must never be mistaken for the question.
            var original = ctx.FleetMessages
                .Where(m => m.InReplyToMessageId == null && (m.CorrelationId == key || m.MessageId == key))
                .OrderBy(m => m.CreatedAtUtc)
                .FirstOrDefault();
            if (original is null)
                return new FleetReplyResult(FleetReplyMiss.NotFound, default, null, null);
            if (original.CorrelationId is null)
                return new FleetReplyResult(FleetReplyMiss.NoReplyWanted, default, null, original);

            var draft = new FleetMessageDraft(
                RecipientSessionId: original.SenderSessionId ?? original.RecipientSessionId,
                SenderSessionId: replierSessionId, SenderName: replierName, SenderMachine: replierMachine,
                Kind: FleetMessageKinds.Reply, Text: text ?? "",
                CorrelationId: original.CorrelationId, InReplyToMessageId: original.MessageId);
            var hash = HashText(draft.Text);
            var history = ReadHistory(ctx, draft, hash, now, TimeSpan.Zero);
            var verdict = decide(history, new FleetReplyOriginal(
                original.MessageId, original.SenderSessionId, original.RecipientSessionId,
                AlreadyReplied: original.RepliedAtUtc is not null));
            if (!verdict.Queued) return new FleetReplyResult(FleetReplyMiss.None, verdict, null, original);

            var row = NewRow(ctx, draft, hash, now);
            ctx.FleetMessages.Add(row);
            original.RepliedAtUtc ??= now;
            ctx.SaveChanges();
            return new FleetReplyResult(FleetReplyMiss.None, verdict, row, original);
        }
    }

    private static FleetMessageEntity NewRow(GatewayDbContext ctx, FleetMessageDraft draft, string hash, DateTime now) => new()
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
        // A message that asks for a reply gets its correlation id here, where its message id is minted too; a reply
        // or a notice carries the one it is about.
        ReplyByUtc = draft.ReplyByUtc is { } by ? Utc(by) : null,
        CorrelationId = draft.ReplyByUtc is not null && draft.InReplyToMessageId is null
            ? Guid.NewGuid().ToString("N")
            : Id(draft.CorrelationId),
        InReplyToMessageId = Id(draft.InReplyToMessageId),
    };

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
        // agent that carried it. A reply is an answer the other side asked for, not a new demand (slice 3), so it
        // is not charged either - to the hourly count or to the spacing. Every other kind a session sends counts.
        var count = ctx.FleetMessages.Count(m => m.SenderSessionId == sender
            && m.CreatedAtUtc > since && m.Kind != FleetMessageKinds.Everyone && m.Kind != FleetMessageKinds.Reply);
        var last = ctx.FleetMessages
            .Where(m => m.SenderSessionId == sender && m.RecipientSessionId == recipient
                && m.Kind != FleetMessageKinds.Everyone && m.Kind != FleetMessageKinds.Reply)
            .OrderByDescending(m => m.CreatedAtUtc)
            .Select(m => (DateTime?)m.CreatedAtUtc)
            .FirstOrDefault();
        var dup = ctx.FleetMessages.AsNoTracking()
            .Where(m => m.RecipientSessionId == recipient
                && m.SenderSessionId == sender && m.ReadAtUtc == null && m.TextHash == hash)
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => new { m.MessageId, m.CorrelationId })
            .FirstOrDefault();
        return new FleetMessageHistory(count, last is null ? null : Utc(last.Value), dup is not null,
            dup?.MessageId, dup?.CorrelationId);
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
            return new FleetInboxRead(unread, recent, Math.Max(recentTotal, recent.Count),
                OriginalsFor(ctx, recipient, unread.Concat(recent)));
        }
    }

    /// <summary>
    /// The questions a set of replies and no-reply notices are about, in one read, keyed by message id (slice 3).
    /// Only questions <paramref name="reader"/> itself SENT come back: a reply and its notice go to the original's
    /// sender, so that is the only reader entitled to the question's text.
    /// </summary>
    private static IReadOnlyDictionary<string, FleetMessageEntity>? OriginalsFor(
        GatewayDbContext ctx, string reader, IEnumerable<FleetMessageEntity> rows)
    {
        var ids = rows.Select(m => m.InReplyToMessageId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return null;
        return ctx.FleetMessages.AsNoTracking()
            .Where(m => ids.Contains(m.MessageId) && m.SenderSessionId == reader)
            .ToList()
            .ToDictionary(m => m.MessageId, StringComparer.Ordinal);
    }

    /// <summary>
    /// NO REPLY BY THE DEADLINE: THE MARK AND THE NOTICE ARE ONE WRITE (slice 3, ruling 10), exactly as stuck and
    /// its notice are (<see cref="MarkStuckWithNotices"/>). Every message that asked for a reply, has none, is past
    /// its deadline and is not yet marked, is marked overdue and - in the SAME save - the notice
    /// <paramref name="noticeFor"/> drafts is written into its sender's inbox (null: no notice). Either every mark
    /// and every notice is persisted, or none is, so a failure before the save leaves the messages unmarked and the
    /// next heartbeat marks and notifies each of them exactly once. A marked message is never scanned again, so the
    /// notice is sent ONCE. A notice passes the same policy as any system notice (the text rules and the
    /// unread-duplicate rule).
    /// </summary>
    public IReadOnlyList<(FleetMessageEntity Overdue, FleetMessageEntity? Notice)> MarkReplyOverdueWithNotices(
        TenantId tenant, DateTime nowUtc, Func<FleetMessageEntity, FleetMessageDraft?> noticeFor,
        FleetMessageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(noticeFor);
        var now = Utc(nowUtc);
        var policyLimits = limits ?? FleetMessageLimits.Default;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var overdue = ctx.FleetMessages
                .Where(m => m.ReplyByUtc != null && m.ReplyByUtc <= now && m.InReplyToMessageId == null
                    && m.CorrelationId != null && m.RepliedAtUtc == null && m.ReplyOverdueAtUtc == null)
                .OrderBy(m => m.ReplyByUtc)
                .ToList();
            var result = new List<(FleetMessageEntity, FleetMessageEntity?)>();
            foreach (var m in overdue)
            {
                m.ReplyOverdueAtUtc = now;
                result.Add((m, StageSystemNotice(ctx, noticeFor(m), now, policyLimits)));
            }
            if (result.Count == 0) return result;
            BeforeOverdueSave?.Invoke();
            ctx.SaveChanges();
            return result;
        }
    }

    /// <summary>Test seam: runs after the overdue marks and their notices are staged and before the one save.
    /// Production never sets it.</summary>
    internal Action? BeforeOverdueSave { get; set; }

    /// <summary>Judge a system notice by the policy and, when it queues, add it to <paramref name="ctx"/> without
    /// saving. Null draft, or a notice the policy drops: nothing is staged.</summary>
    private static FleetMessageEntity? StageSystemNotice(GatewayDbContext ctx, FleetMessageDraft? draft, DateTime now,
        FleetMessageLimits limits)
    {
        if (draft is null) return null;
        var hash = HashText(draft.Text);
        var history = ReadHistory(ctx, draft, hash, now, limits.SenderWindow);
        var verdict = FleetMessagePolicy.Decide(new FleetMessageAttempt(
            SenderSessionId: null, SenderControllerSessionId: null,
            RecipientSessionId: draft.RecipientSessionId, RecipientControllerSessionId: null,
            Text: draft.Text, NowUtc: now, SentBySenderInWindow: 0, LastSentToRecipientUtc: null,
            RecipientHasUnreadDuplicate: history.RecipientHasUnreadDuplicate,
            Exemption: FleetMessageExemption.System, Kind: draft.Kind), limits);
        if (!verdict.Queued) return null;
        var notice = NewRow(ctx, draft, hash, now);
        ctx.FleetMessages.Add(notice);
        return notice;
    }

    /// <summary>How many messages wait unread in one session's inbox. Reads, changes nothing.</summary>
    public int CountUnread(TenantId tenant, string recipientSessionId)
    {
        var recipient = Id(recipientSessionId) ?? "";
        using var ctx = _db.CreateContext(tenant);
        return ctx.FleetMessages.Count(m => m.RecipientSessionId == recipient && m.ReadAtUtc == null);
    }

    /// <summary>
    /// Every session's UNREAD messages in this tenant, counted for the row line (slice 4), in ONE grouped query:
    /// no text, no rows, only a count per recipient, kind and stuck mark, and the oldest write time of each group.
    /// Reads, changes nothing. A session with nothing unread is absent from the answer.
    /// </summary>
    public IReadOnlyDictionary<string, FleetInboxCounts> UnreadCountsByRecipient(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        var groups = ctx.FleetMessages.AsNoTracking()
            .Where(m => m.ReadAtUtc == null)
            .GroupBy(m => new { m.RecipientSessionId, m.Kind, Stuck = m.StuckAtUtc != null })
            .Select(g => new { g.Key.RecipientSessionId, g.Key.Kind, g.Key.Stuck, Count = g.Count(), Oldest = g.Min(m => m.CreatedAtUtc) })
            .ToList();

        var byRecipient = new Dictionary<string, FleetInboxCounts>(StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var c = byRecipient.TryGetValue(g.RecipientSessionId, out var had) ? had : FleetInboxCounts.None;
            if (g.Stuck)
            {
                var oldest = Utc(g.Oldest);
                c = c with
                {
                    Stuck = c.Stuck + g.Count,
                    OldestStuckWrittenUtc = c.OldestStuckWrittenUtc is { } o && o <= oldest ? o : oldest,
                };
            }
            else
            {
                c = g.Kind switch
                {
                    FleetMessageKinds.Reply => c with { Replies = c.Replies + g.Count },
                    FleetMessageKinds.System => c with { Notices = c.Notices + g.Count },
                    _ => c with { Waiting = c.Waiting + g.Count },
                };
            }
            byRecipient[g.RecipientSessionId] = c;
        }
        return byRecipient;
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
    /// ring time becomes <paramref name="nowUtc"/>. Returns how many rows were changed.
    ///
    /// THE STORE ENFORCES ITS OWN LIMITS (inspection 4, ruling 5); it does not rely on the caller having
    /// checked. A row is left alone - and every refusal is logged - when it:
    ///  - was read or marked stuck in the moment since the ring was decided;
    ///  - belongs to a session other than <paramref name="recipientSessionId"/>, the one that was rung;
    ///  - has already been rung <paramref name="ringCap"/> times, so no caller - a second Gateway process, a
    ///    stale call - can take a count past the cap.
    /// </summary>
    public int MarkRung(TenantId tenant, string recipientSessionId, IReadOnlyCollection<string> messageIds,
        DateTime nowUtc, int ringCap)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (string.IsNullOrWhiteSpace(recipientSessionId))
            throw new ArgumentException("A ring is recorded for the session that was rung.", nameof(recipientSessionId));
        return MarkRungMany(tenant, [(recipientSessionId, messageIds)], nowUtc, ringCap);
    }

    /// <summary>
    /// <see cref="MarkRung"/> for several rung sessions at once: one read and one save, whatever the number of
    /// sessions (inspection 4, ruling 6). The same refusals apply to every row.
    /// </summary>
    public int MarkRungMany(TenantId tenant, IReadOnlyCollection<(string RecipientSessionId, IReadOnlyCollection<string> MessageIds)> rings,
        DateTime nowUtc, int ringCap)
    {
        ArgumentNullException.ThrowIfNull(rings);
        if (ringCap <= 0)
            throw new ArgumentOutOfRangeException(nameof(ringCap), ringCap, "The ring cap must be positive.");
        var rungFor = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (recipient, ids) in rings)
        {
            if (string.IsNullOrWhiteSpace(recipient))
                throw new ArgumentException("A ring is recorded for the session that was rung.", nameof(rings));
            foreach (var id in ids ?? []) rungFor[id] = Id(recipient)!;
        }
        if (rungFor.Count == 0) return 0;
        var now = Utc(nowUtc);
        var allIds = rungFor.Keys.ToList();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.FleetMessages.AsNoTracking()
                .Where(m => allIds.Contains(m.MessageId) && m.ReadAtUtc == null && m.StuckAtUtc == null)
                .Select(m => new { m.MessageId, m.RecipientSessionId, m.RingCount })
                .ToList();
            var accepted = new List<string>();
            foreach (var m in rows)
            {
                var recipient = rungFor[m.MessageId];
                if (!string.Equals(m.RecipientSessionId, recipient, StringComparison.Ordinal))
                {
                    FileLog.Write($"[FleetMessageStore] MarkRung REFUSED (another session's message): id={m.MessageId} rung={recipient}");
                    continue;
                }
                if (m.RingCount >= ringCap)
                {
                    FileLog.Write($"[FleetMessageStore] MarkRung REFUSED (at the ring cap {ringCap}): id={m.MessageId}");
                    continue;
                }
                accepted.Add(m.MessageId);
            }
            if (accepted.Count == 0) return 0;
            // ONE statement for every accepted row (SQLite would otherwise issue one update per row). The same
            // conditions are repeated in it, so nothing that changed since the read above can be counted.
            return ctx.FleetMessages
                .Where(m => accepted.Contains(m.MessageId) && m.ReadAtUtc == null && m.StuckAtUtc == null && m.RingCount < ringCap)
                .ExecuteUpdate(set => set
                    .SetProperty(m => m.RingCount, m => m.RingCount + 1)
                    .SetProperty(m => m.LastRungAtUtc, now));
        }
    }

    /// <summary>
    /// Every UNREAD message in this tenant - open and stuck - in ONE query, untracked and WITHOUT its text: only
    /// the columns the doorbell schedules with (inspection 4, ruling 6). The heartbeat groups them by recipient,
    /// so it needs no query per session.
    /// </summary>
    public IReadOnlyList<FleetMessageEntity> UnreadForScheduling(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.FleetMessages.AsNoTracking()
            .Where(m => m.ReadAtUtc == null)
            .OrderBy(m => m.CreatedAtUtc)
            .Select(m => new FleetMessageEntity
            {
                TenantId = m.TenantId,
                MessageId = m.MessageId,
                RecipientSessionId = m.RecipientSessionId,
                SenderSessionId = m.SenderSessionId,
                Kind = m.Kind,
                CreatedAtUtc = m.CreatedAtUtc,
                RingCount = m.RingCount,
                LastRungAtUtc = m.LastRungAtUtc,
                StuckAtUtc = m.StuckAtUtc,
            })
            .ToList();
    }

    /// <summary>
    /// Mark stuck every open message in this tenant that <paramref name="isStuck"/> rules stuck, and return the
    /// rows that were marked. Decided and written under the store lock, so a read that lands first wins: a
    /// message read before this runs is not open any more and is never marked. Writes no notice - see
    /// <see cref="MarkStuckWithNotices"/>.
    /// </summary>
    public IReadOnlyList<FleetMessageEntity> MarkStuck(TenantId tenant, DateTime nowUtc, Func<FleetMessageEntity, bool> isStuck) =>
        MarkStuckWithNotices(tenant, nowUtc, isStuck, _ => null).Select(m => m.Stuck).ToList();

    /// <summary>
    /// STUCK AND ITS NOTICE ARE ONE WRITE (inspection 4, ruling 4). Mark stuck every open message
    /// <paramref name="isStuck"/> rules stuck and, in the SAME save, write the system notice
    /// <paramref name="noticeFor"/> drafts for it (null: no notice). Either every mark and every notice is
    /// persisted, or none is - so a process that stops, or a notice that cannot be built, leaves the messages
    /// open, and the next heartbeat marks and notifies them exactly once. A notice passes the same policy as a
    /// system notice sent any other way (the text rules and the unread-duplicate rule).
    /// </summary>
    /// <param name="minRings">Only rows rung at least this many times are read. The heartbeat passes the ring
    /// cap, so the scan never loads a message that cannot be stuck yet (ruling 6).</param>
    /// <param name="limits">The limits a notice's text is judged by; the product's when null.</param>
    public IReadOnlyList<(FleetMessageEntity Stuck, FleetMessageEntity? Notice)> MarkStuckWithNotices(
        TenantId tenant, DateTime nowUtc, Func<FleetMessageEntity, bool> isStuck,
        Func<FleetMessageEntity, FleetMessageDraft?> noticeFor, int minRings = 1, FleetMessageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(isStuck);
        ArgumentNullException.ThrowIfNull(noticeFor);
        var now = Utc(nowUtc);
        var policyLimits = limits ?? FleetMessageLimits.Default;
        var floor = Math.Max(1, minRings);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            // Only rung, open messages can be stuck; which of them ARE is decided by the caller's ruling alone, so
            // the one definition of "stuck" lives in FleetRingSchedule and is not restated in this query.
            var candidates = ctx.FleetMessages
                .Where(m => m.ReadAtUtc == null && m.StuckAtUtc == null && m.RingCount >= floor)
                .ToList();
            var result = new List<(FleetMessageEntity, FleetMessageEntity?)>();
            foreach (var m in candidates.Where(isStuck))
            {
                m.StuckAtUtc = now;
                result.Add((m, StageSystemNotice(ctx, noticeFor(m), now, policyLimits)));
            }
            if (result.Count == 0) return result;
            BeforeStuckSave?.Invoke();
            ctx.SaveChanges();
            return result;
        }
    }

    /// <summary>Test seam: runs after the stuck marks and their notices are staged and before the one save. A
    /// test throws here to prove a failure between the two persists neither. Production never sets it.</summary>
    internal Action? BeforeStuckSave { get; set; }

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
