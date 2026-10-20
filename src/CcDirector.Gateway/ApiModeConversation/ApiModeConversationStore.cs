using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Receipts;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.ApiModeConversation;

/// <summary>
/// THE DURABLE CONVERSATION OF AN API-MODE SESSION (mission #4070, phase 2 follow-on). What a reader attaching to
/// <c>GET /sessions/{sid}/live</c> with no cursor is handed as the stream's opening, so the Gateway can give a reader the
/// conversation after a Gateway restart and for a session longer than the in-memory event buffer holds.
///
/// <para><b>What is kept.</b> The FINAL form of every item the Gateway's ruling (<see cref="ApiModeLiveRuling"/>, the
/// project rule 7) makes of an event, except receipts: a reply's final (<c>reply:&lt;messageId&gt;</c>, the id the live
/// reply item has, so the two are one item on screen), a tool card (its start, then replaced by its final state), the
/// line that ends a turn, a connection line and a permission line. The words are the ruling's, through
/// <see cref="ApiModeLiveRuling.ConversationItems"/>; this store words nothing. Receipts are already durable in
/// <see cref="ApiModeReceiptStore"/> and are not copied here: the opening merges the two. Deltas are never stored. The
/// first delta of a reply writes one row with no text, which only holds the reply's place: a live reader sees a reply
/// where its first delta arrived, so the stored conversation puts its final there too.</para>
///
/// <para><b>The order</b> is <see cref="ApiModeConversationItemEntity.Ordinal"/>, the Gateway's own increasing number per
/// session, given when a row is created. A later final with the same item id replaces the row in place and keeps its
/// number. Each row also keeps the position of the event that created it, which is how the opening places the receipts
/// among the items: the receipts are ordered by the same position, and both rise with the order events are ingested.</para>
///
/// <para><b>Written by the ingest</b> (<see cref="ApiModeEventIngest"/>), for each new event, inside the event buffer's
/// session lock and the tenant's scope, right after the receipts. A reader's opening is read under the same lock
/// (<see cref="IApiModeEventFeed.Snapshot"/>), so it reflects exactly the events the feed has handed out by then.
/// Forward only, like the receipts: each row keeps the position of the last event applied, so a re-sent batch writes
/// nothing. One transaction per event that keeps something; the primary key is the item and the order is unique per
/// session, so two Gateways writing the same session at once (a deploy) collide, and the loser re-reads and decides again,
/// once.</para>
///
/// <para><b>A delta costs no database work</b> once its reply's place row exists (the review of pull request #4200,
/// finding 1). Deltas are most of what a streaming session sends, and on the hosted Gateway every statement is a network
/// round trip made under the buffer's session lock. So <see cref="Record"/> asks the ruling first and opens no context
/// when it keeps nothing (thought text), and remembers in memory, per session, the reply ids whose place row is known to
/// exist, so a reply's second and later deltas touch nothing. A reply's first delta still writes its place row; after a
/// Gateway restart, an open reply's next delta costs one query to learn the row is there. The memory is only ever a
/// promise that a row EXISTS, and it is dropped whenever this store lets rows go (the bound, the retention) and when a turn
/// ends; if another Gateway's bound let the row go during a deploy, the reply's final is kept as a new row at the end,
/// which is where it would have gone had its deltas arrived after the drop.</para>
///
/// <para><b>The lock is per session.</b> Every write of a session is made under that session's lock, and also already
/// under the event buffer's session lock, so writes of different sessions never wait for each other here. Nothing in the
/// store spans sessions but the retention sweep, which needs no lock: it re-checks the cutoff in its delete, so a session
/// written between its two statements keeps its new rows. Two writes of one session that somehow raced would still be
/// safe: the order is unique per session, and the loser re-reads and decides again.</para>
///
/// <para><b>An item whose id is longer than <see cref="MaxItemIdLength"/></b> (a tool call id is the model provider's,
/// passed through unchanged) is not kept in the conversation: one log line names the session and the id's length, and
/// everything else - the rest of the batch, the receipts, the live stream - goes on as before. The ingest does not refuse
/// such a batch: a whole Director batch dropped for good would lose receipts and turn ends to protect this one optional
/// table.</para>
///
/// <para><b>THE BOUND.</b> Per session at most <see cref="MaxItemsPerSession"/> items and
/// <see cref="MaxCharactersPerSession"/> characters of item text (the text and the detail). When a write goes over, the
/// oldest items go first (the newest is always kept), and the new oldest row is marked
/// (<see cref="ApiModeConversationItemEntity.EarlierDropped"/>), so the opening then starts with one item saying earlier
/// conversation is not kept - never a silent gap. The check is a count and a sum over the session's rows, run only when a
/// write added a row or made one longer; it is a range read of the unique (tenant, session, order) index. Measured on
/// the test database (SQLite) at 2,000 rows: 0.66 milliseconds for the count and the sum, against 7 to 12 milliseconds
/// for the whole write, so no running totals are kept.</para>
///
/// <para><b>Retention.</b> <see cref="Retention"/>, the receipts' retention, applied by
/// <see cref="History.SessionHistorySweep"/>, per SESSION rather than per row: a session's items go once its newest item
/// was last changed before the cutoff, so a long-running session never loses its start without the marker. The receipts
/// are purged per ROW, and that is deliberate on their side and not copied here: a receipt is an answer about one prompt
/// that stands on its own, while this store is read as one continuous conversation from its first kept item, and a per-row
/// purge here would cut the start of a session still in use with no line saying so - a silent gap at the top of the
/// opening. A session in use for more than 90 days can therefore show replies whose receipts have gone. There is no
/// Gateway-side delete of a session yet: a Director removing a session from its roster (which a move to another Director
/// also does) purges nothing, exactly as for the receipts, so a moved session keeps its conversation.</para>
///
/// <para><b>Scoped by account</b> through the context's global filter, like every store: the caller enters the tenant
/// scope (the hub's bound tenant on a push, the request's resolved tenant on a read).</para>
/// </summary>
public sealed class ApiModeConversationStore
{
    /// <summary>How long a session's conversation lives after its last change: the receipts' retention.</summary>
    public static readonly TimeSpan Retention = ApiModeReceiptStore.Retention;

    /// <summary>The most items kept per session; past it the oldest go first.</summary>
    public const int MaxItemsPerSession = 2_000;

    /// <summary>The most characters of item text (text and detail) kept per session; past it the oldest go first.</summary>
    public const long MaxCharactersPerSession = 2_000_000;

    /// <summary>The longest item id kept (the column width): a prefix and a message, tool call or permission request
    /// id, or a connection line's generation and sequence. An item whose id is longer is not kept in the conversation
    /// (<see cref="Record"/> logs it and goes on); nothing else about its batch changes.</summary>
    public const int MaxItemIdLength = 160;

    /// <summary>The most reply ids remembered per session as needing no database work for their deltas; past it the
    /// session's memory starts again, which costs each open reply one more query.</summary>
    internal const int MaxSettledRepliesPerSession = 256;

    private readonly GatewayDatabase _db;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<(string Tenant, string Session), SessionState> _sessions = new();

    /// <summary>One session's lock and what the store remembers of it in memory.</summary>
    private sealed class SessionState
    {
        public readonly object Gate = new();

        /// <summary>The reply item ids whose later deltas need no database work: the reply's place row is known to
        /// exist, or its id is too long to keep. An id goes when the reply's final is seen; the whole set goes when a
        /// turn ends, when the bound or the retention lets rows go, and past
        /// <see cref="MaxSettledRepliesPerSession"/>.</summary>
        public readonly HashSet<string> SettledReplies = new(StringComparer.Ordinal);
    }

    /// <param name="db">The Gateway database.</param>
    /// <param name="utcNow">The Gateway's clock: when a row is created and changed, and what retention measures.</param>
    public ApiModeConversationStore(GatewayDatabase db, Func<DateTime> utcNow)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>
    /// Keep what one event of <paramref name="sessionId"/> adds to the conversation, in the ambient tenant. Answers how
    /// many rows it created or changed: 0 for an event that shows nothing durable (a delta after the first, thought text,
    /// a receipt event, a turn starting), for one already applied, and for an item whose id is too long to keep.
    /// </summary>
    public int Record(string sessionId, ApiModeHeldEvent held)
    {
        RequireId(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(held);
        if (held.Event is PromptQueued or PromptDelivered or PromptRefused or TurnStarted)
            return 0;   // a receipt's or nothing's: the receipt store keeps those

        var state = _sessions.GetOrAdd((_db.AmbientTenant.Value, sessionId), _ => new SessionState());
        lock (state.Gate)
        {
            // The ruling first, with no database: an event it keeps nothing of (thought text) opens no context, and a
            // reply's delta whose place row is known to exist touches nothing. A tool's finish is ruled once its stored
            // card is read (RecordOnce), so its title survives a Gateway restart.
            var items = held.Event is ToolCallFinished ? null : Keepable(sessionId, ApiModeLiveRuling.ConversationItems(held, null), state);
            if (items is not null && items.All(i => i.Mode == ApiModeLiveItemModes.Append && state.SettledReplies.Contains(i.ItemId)))
                return 0;

            int changed;
            try { changed = RecordOnce(sessionId, held, items, state); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[ApiModeConversationStore] Record session={sessionId} at={held.Position}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                changed = RecordOnce(sessionId, held, items, state);
            }
            // Message ids are made per turn, so no reply of an ended turn streams again.
            if (held.Event is TurnEnded)
                state.SettledReplies.Clear();
            return changed;
        }
    }

    /// <summary>One write of one event, in one transaction. <paramref name="items"/> is null for a tool's finish, which is
    /// ruled here once its stored card is read.</summary>
    private int RecordOnce(string sessionId, ApiModeHeldEvent held, IReadOnlyList<ApiModeLiveItem>? items, SessionState state)
    {
        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        var position = held.Position;

        if (items is null)
        {
            var finished = (ToolCallFinished)held.Event;
            var cardId = ApiModeLiveRuling.ToolItemId(finished.ToolCallId);
            ApiModeLiveItem? storedToolCard = null;
            if (cardId.Length <= MaxItemIdLength)
            {
                var card = Find(ctx, sessionId, cardId);
                if (card?.Text is not null) storedToolCard = ToItem(card);
            }
            items = Keepable(sessionId, ApiModeLiveRuling.ConversationItems(held, storedToolCard), state);
        }

        var changed = 0;
        var grew = false;
        long? next = null;
        var placed = new List<string>();
        var finals = new List<string>();
        foreach (var item in items)
        {
            var isFinal = item.Mode == ApiModeLiveItemModes.Replace;
            (isFinal ? finals : placed).Add(item.ItemId);
            var row = Find(ctx, sessionId, item.ItemId);
            if (row is null)
            {
                next ??= NextOrdinal(ctx, sessionId);
                row = NewRow(ctx, sessionId, item, next.Value, position);
                next++;
                if (isFinal) Fill(row, item, position);
                ctx.ApiModeConversationItems.Add(row);
                changed++;
                grew = true;
                FileLog.Write($"[ApiModeConversationStore] Record session={sessionId} item={item.ItemId} at={position}: {(isFinal ? "stored" : "place held for the reply")} as number {row.Ordinal}");
                continue;
            }
            // A reply's later delta changes nothing: only its final is kept, in the place its first delta took.
            if (!isFinal) continue;
            if (!position.IsAfter(row.LastGeneration, row.LastGenerationStartedUtc, row.LastSequence))
                continue;   // a duplicate or a late arrival; a re-sent batch is routine, so it is not logged
            var before = row.Characters;
            Fill(row, item, position);
            grew |= row.Characters > before;
            changed++;
            FileLog.Write($"[ApiModeConversationStore] Record session={sessionId} item={item.ItemId} at={position}: replaced in place, number {row.Ordinal}");
        }
        if (changed == 0)
        {
            Settle(sessionId, state, placed, finals);
            return 0;
        }

        ctx.SaveChanges();
        var dropped = grew && EnforceBound(ctx, sessionId);
        tx.Commit();
        Settle(sessionId, state, placed, finals);
        if (dropped)
            state.SettledReplies.Clear();   // a let-go row may be a reply's place
        return changed;
    }

    /// <summary>After a write is committed (or found nothing to write): the replies whose place row now exists need no
    /// database work for their later deltas; a reply whose final was seen is done streaming.</summary>
    private static void Settle(string sessionId, SessionState state, List<string> placed, List<string> finals)
    {
        foreach (var id in finals) state.SettledReplies.Remove(id);
        foreach (var id in placed) state.SettledReplies.Add(id);
        if (state.SettledReplies.Count > MaxSettledRepliesPerSession)
        {
            FileLog.Write($"[ApiModeConversationStore] session={sessionId}: {state.SettledReplies.Count} open replies remembered, over {MaxSettledRepliesPerSession}; forgetting them, so each costs one query again");
            state.SettledReplies.Clear();
        }
    }

    /// <summary>The items that fit the store. An item whose id is longer than the column is not kept in the conversation:
    /// one log line naming the session and the id's length, and its batch, the receipts and the live stream go on as
    /// before. A reply's over-long id is remembered, so its later deltas log nothing more.</summary>
    private static IReadOnlyList<ApiModeLiveItem> Keepable(string sessionId, IReadOnlyList<ApiModeLiveItem> items, SessionState state)
    {
        if (items.All(i => i.ItemId.Length <= MaxItemIdLength))
            return items;
        var kept = new List<ApiModeLiveItem>(items.Count);
        foreach (var item in items)
        {
            if (item.ItemId.Length <= MaxItemIdLength) { kept.Add(item); continue; }
            if (item.Mode == ApiModeLiveItemModes.Append && !state.SettledReplies.Add(item.ItemId))
                continue;   // already logged at its first delta
            FileLog.Write($"[ApiModeConversationStore] Record session={sessionId}: a {item.Kind} item's id is {item.ItemId.Length} characters, over the {MaxItemIdLength} kept; not kept in the conversation");
        }
        return kept;
    }

    /// <summary>
    /// Every stored item of the session in the ambient tenant, in the Gateway's order: the opening of the live stream,
    /// before the receipts are merged in. A row holding only a reply's place comes back with
    /// <see cref="ApiModeStoredConversationItem.HasFinal"/> false. One query, which the database answers from one
    /// consistent view; what makes the opening exact against the live stream is the caller's lock (the event buffer's
    /// session lock, the one the ingest writes under), not this store's.
    /// </summary>
    public IReadOnlyList<ApiModeStoredConversationItem> Read(string sessionId)
    {
        RequireId(sessionId, nameof(sessionId));
        using var ctx = _db.CreateContext();
        return ctx.ApiModeConversationItems.AsNoTracking()
            .Where(r => r.SessionId == sessionId)
            .OrderBy(r => r.Ordinal)
            .AsEnumerable()
            .Select(r => new ApiModeStoredConversationItem(
                ToItem(r), r.Text is not null, new ApiModeConversationPlace(r.FirstGenerationStartedUtc, r.FirstSequence), r.EarlierDropped))
            .ToList();
    }

    /// <summary>Retention, per session: every session in the ambient tenant whose newest item was last changed before
    /// the cutoff loses all its items. Answers how many rows were removed.</summary>
    public int PurgeOlderThan(DateTime cutoffUtc)
    {
        if (cutoffUtc == default) throw new ArgumentException("cutoffUtc is not set", nameof(cutoffUtc));
        var cutoff = DateTime.SpecifyKind(cutoffUtc.ToUniversalTime(), DateTimeKind.Utc);
        using var ctx = _db.CreateContext();
        var tenant = ctx.ActiveTenant ?? throw new InvalidOperationException("no tenant is active on the conversation store's context");
        var sessions = ctx.ApiModeConversationItems
            .GroupBy(r => r.SessionId)
            .Where(g => g.Max(r => r.UpdatedAtUtc) < cutoff)
            .Select(g => g.Key)
            .ToList();
        if (sessions.Count == 0) return 0;
        // The cutoff again on the delete: a session written between the two statements keeps its new rows, as it would
        // had the sweep run a moment earlier.
        var removed = ctx.ApiModeConversationItems.Where(r => sessions.Contains(r.SessionId) && r.UpdatedAtUtc < cutoff).ExecuteDelete();
        foreach (var session in sessions)
        {
            if (!_sessions.TryGetValue((tenant, session), out var state)) continue;
            lock (state.Gate) state.SettledReplies.Clear();
        }
        FileLog.Write($"[ApiModeConversationStore] PurgeOlderThan: removed {removed} item(s) of {sessions.Count} session(s) last changed before {cutoff:O}");
        return removed;
    }

    // ----- the bound -----

    /// <summary>Over the bound: the oldest rows go (never the newest), and the new oldest is marked so the opening says
    /// earlier conversation is not kept. Inside the write's transaction. Answers whether any row was let go.</summary>
    private static bool EnforceBound(GatewayDbContext ctx, string sessionId)
    {
        var rows = ctx.ApiModeConversationItems.Where(r => r.SessionId == sessionId);
        var count = rows.Count();
        var characters = rows.Sum(r => (long)r.Characters);
        if (count <= MaxItemsPerSession && characters <= MaxCharactersPerSession)
            return false;

        var oldestFirst = rows.OrderBy(r => r.Ordinal).Select(r => new { r.Ordinal, r.Characters }).ToList();
        var drop = 0;
        while (drop < oldestFirst.Count - 1
               && (count - drop > MaxItemsPerSession || characters > MaxCharactersPerSession))
        {
            characters -= oldestFirst[drop].Characters;
            drop++;
        }
        if (drop == 0)
        {
            // The one item is larger than the whole bound on its own: the newest is always kept.
            FileLog.Write($"[ApiModeConversationStore] session={sessionId}: its only item holds {characters} characters, over the {MaxCharactersPerSession} kept; kept, since the newest is never let go");
            return false;
        }
        var through = oldestFirst[drop - 1].Ordinal;
        var keptFrom = oldestFirst[drop].Ordinal;
        var removed = rows.Where(r => r.Ordinal <= through).ExecuteDelete();
        rows.Where(r => r.Ordinal == keptFrom).ExecuteUpdate(u => u.SetProperty(r => r.EarlierDropped, true));
        FileLog.Write($"[ApiModeConversationStore] session={sessionId}: over the bound ({count} item(s), at most {MaxItemsPerSession}; at most {MaxCharactersPerSession} characters); let go of the {removed} oldest, through number {through}, and marked number {keptFrom} as the first kept");
        return true;
    }

    // ----- rows -----

    private static ApiModeConversationItemEntity? Find(GatewayDbContext ctx, string sessionId, string itemId)
        => ctx.ApiModeConversationItems.FirstOrDefault(r => r.SessionId == sessionId && r.ItemId == itemId);

    private static long NextOrdinal(GatewayDbContext ctx, string sessionId)
        => (ctx.ApiModeConversationItems.Where(r => r.SessionId == sessionId).Max(r => (long?)r.Ordinal) ?? 0) + 1;

    private ApiModeConversationItemEntity NewRow(GatewayDbContext ctx, string sessionId, ApiModeLiveItem item, long ordinal, ApiModeEventPosition position)
    {
        var now = Utc(_utcNow());
        return new ApiModeConversationItemEntity
        {
            TenantId = ctx.ActiveTenant ?? throw new InvalidOperationException("no tenant is active on the conversation store's context"),
            SessionId = sessionId,
            ItemId = item.ItemId,
            Ordinal = ordinal,
            Kind = item.Kind,
            Tone = item.Tone,
            TurnId = item.TurnId,
            FirstGenerationStartedUtc = position.GenerationStartedUtc,
            FirstSequence = position.Sequence,
            LastGeneration = position.Generation,
            LastGenerationStartedUtc = position.GenerationStartedUtc,
            LastSequence = position.Sequence,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    /// <summary>The row becomes exactly the ruled final item, as of the event at <paramref name="position"/>.</summary>
    private void Fill(ApiModeConversationItemEntity row, ApiModeLiveItem item, ApiModeEventPosition position)
    {
        row.Kind = item.Kind;
        row.Text = item.Text;
        row.Status = item.Status;
        row.Detail = item.Detail;
        row.Tone = item.Tone;
        row.TurnId = item.TurnId;
        row.Characters = item.Text.Length + (item.Detail?.Length ?? 0);
        row.LastGeneration = position.Generation;
        row.LastGenerationStartedUtc = position.GenerationStartedUtc;
        row.LastSequence = position.Sequence;
        row.UpdatedAtUtc = Utc(_utcNow());
    }

    /// <summary>A stored row as the live item it is: a replace, under the sequence of the last event that changed it.</summary>
    private static ApiModeLiveItem ToItem(ApiModeConversationItemEntity row) => new()
    {
        ItemId = row.ItemId,
        Kind = row.Kind,
        Mode = ApiModeLiveItemModes.Replace,
        Text = row.Text ?? "",
        Status = row.Status,
        Detail = row.Detail,
        Tone = row.Tone,
        TurnId = row.TurnId,
        Sequence = row.LastSequence,
    };

    private static void RequireId(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > ApiModeReceiptStore.MaxIdLength)
            throw new ArgumentException($"{name} is longer than {ApiModeReceiptStore.MaxIdLength} characters", name);
    }

    private static DateTime Utc(DateTime value)
    {
        if (value == default)
            throw new InvalidOperationException("the conversation store's clock answered no time");
        return DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
    }
}

/// <summary>Where an item or a receipt stands in its session: the start of the generation of the event that created it,
/// then that event's sequence - the order <see cref="ApiModeReceiptStore"/> reads receipts in.</summary>
public readonly record struct ApiModeConversationPlace(DateTime GenerationStartedUtc, long Sequence) : IComparable<ApiModeConversationPlace>
{
    public int CompareTo(ApiModeConversationPlace other)
    {
        var started = GenerationStartedUtc.CompareTo(other.GenerationStartedUtc);
        return started != 0 ? started : Sequence.CompareTo(other.Sequence);
    }
}

/// <summary>One stored item as the opening reads it: the live item (a replace), whether it holds a final (false for a
/// row that only holds a reply's place), where it stands against the receipts, and whether older items were let go
/// before it.</summary>
public sealed record ApiModeStoredConversationItem(ApiModeLiveItem Item, bool HasFinal, ApiModeConversationPlace First, bool EarlierDropped);
