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
/// nothing. One transaction per event; the primary key is the item and the order is unique per session, so two Gateways
/// writing the same session at once (a deploy) collide, and the loser re-reads and decides again, once.</para>
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
/// was last changed before the cutoff, so a long-running session never loses its start without the marker. There is no
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

    /// <summary>The longest message, tool call or permission request id an event may carry (the ingest refuses a longer
    /// one, before anything is written): with its prefix it must fit <see cref="MaxItemIdLength"/>.</summary>
    public const int MaxSourceIdLength = 128;

    /// <summary>The longest item id kept (the column width): a prefix and a source id, or a connection line's
    /// generation and sequence.</summary>
    public const int MaxItemIdLength = 160;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly Func<DateTime> _utcNow;

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
    /// a receipt event, a turn starting) and for one already applied.
    /// </summary>
    public int Record(string sessionId, ApiModeHeldEvent held)
    {
        RequireId(sessionId, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(held);
        if (held.Event is PromptQueued or PromptDelivered or PromptRefused or TurnStarted)
            return 0;   // a receipt's or nothing's: the receipt store keeps those

        lock (_gate)
        {
            try { return RecordOnce(sessionId, held); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[ApiModeConversationStore] Record session={sessionId} at={held.Position}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return RecordOnce(sessionId, held);
            }
        }
    }

    private int RecordOnce(string sessionId, ApiModeHeldEvent held)
    {
        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();
        var position = held.Position;

        ApiModeLiveItem? storedToolCard = null;
        if (held.Event is ToolCallFinished finished)
        {
            var card = Find(ctx, sessionId, ApiModeLiveRuling.ToolItemId(finished.ToolCallId));
            if (card?.Text is not null) storedToolCard = ToItem(card);
        }

        var changed = 0;
        var grew = false;
        long? next = null;
        foreach (var item in ApiModeLiveRuling.ConversationItems(held, storedToolCard))
        {
            if (item.ItemId.Length > MaxItemIdLength)
                throw new InvalidOperationException($"ApiModeConversationStore: the item id '{item.ItemId}' is longer than {MaxItemIdLength} characters; the ingest refuses a longer source id before anything is written");
            var row = Find(ctx, sessionId, item.ItemId);
            var isFinal = item.Mode == ApiModeLiveItemModes.Replace;
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
            return 0;

        ctx.SaveChanges();
        if (grew)
            EnforceBound(ctx, sessionId);
        tx.Commit();
        return changed;
    }

    /// <summary>
    /// Every stored item of the session in the ambient tenant, in the Gateway's order: the opening of the live stream,
    /// before the receipts are merged in. A row holding only a reply's place comes back with
    /// <see cref="ApiModeStoredConversationItem.HasFinal"/> false.
    /// </summary>
    public IReadOnlyList<ApiModeStoredConversationItem> Read(string sessionId)
    {
        RequireId(sessionId, nameof(sessionId));
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.ApiModeConversationItems.AsNoTracking()
                .Where(r => r.SessionId == sessionId)
                .OrderBy(r => r.Ordinal)
                .AsEnumerable()
                .Select(r => new ApiModeStoredConversationItem(
                    ToItem(r), r.Text is not null, new ApiModeConversationPlace(r.FirstGenerationStartedUtc, r.FirstSequence), r.EarlierDropped))
                .ToList();
        }
    }

    /// <summary>Retention, per session: every session in the ambient tenant whose newest item was last changed before
    /// the cutoff loses all its items. Answers how many rows were removed.</summary>
    public int PurgeOlderThan(DateTime cutoffUtc)
    {
        if (cutoffUtc == default) throw new ArgumentException("cutoffUtc is not set", nameof(cutoffUtc));
        var cutoff = DateTime.SpecifyKind(cutoffUtc.ToUniversalTime(), DateTimeKind.Utc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var sessions = ctx.ApiModeConversationItems
                .GroupBy(r => r.SessionId)
                .Where(g => g.Max(r => r.UpdatedAtUtc) < cutoff)
                .Select(g => g.Key)
                .ToList();
            if (sessions.Count == 0) return 0;
            var removed = ctx.ApiModeConversationItems.Where(r => sessions.Contains(r.SessionId)).ExecuteDelete();
            FileLog.Write($"[ApiModeConversationStore] PurgeOlderThan: removed {removed} item(s) of {sessions.Count} session(s) last changed before {cutoff:O}");
            return removed;
        }
    }

    // ----- the bound -----

    /// <summary>Over the bound: the oldest rows go (never the newest), and the new oldest is marked so the opening says
    /// earlier conversation is not kept. Inside the write's transaction.</summary>
    private static void EnforceBound(GatewayDbContext ctx, string sessionId)
    {
        var rows = ctx.ApiModeConversationItems.Where(r => r.SessionId == sessionId);
        var count = rows.Count();
        var characters = rows.Sum(r => (long)r.Characters);
        if (count <= MaxItemsPerSession && characters <= MaxCharactersPerSession)
            return;

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
            return;
        }
        var through = oldestFirst[drop - 1].Ordinal;
        var keptFrom = oldestFirst[drop].Ordinal;
        var removed = rows.Where(r => r.Ordinal <= through).ExecuteDelete();
        rows.Where(r => r.Ordinal == keptFrom).ExecuteUpdate(u => u.SetProperty(r => r.EarlierDropped, true));
        FileLog.Write($"[ApiModeConversationStore] session={sessionId}: over the bound ({count} item(s), at most {MaxItemsPerSession}; at most {MaxCharactersPerSession} characters); let go of the {removed} oldest, through number {through}, and marked number {keptFrom} as the first kept");
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
