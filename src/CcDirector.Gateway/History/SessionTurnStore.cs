using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.History;

/// <summary>
/// THE conversation store (the turn-push mission, <c>docs/missions/turn-push-2026-09-01/brief.md</c>).
/// A Director pushes each session's messages here once; Chat, the transcript view, and the wingman read
/// them from here. Tenant-scoped through the context's global filter like every other store, so one
/// account's rows can never answer another account's read.
///
/// The properties the later phases lean on, and how each is kept:
///
///  - IDEMPOTENT. A message is keyed by (session, generation, ordinal); ordinals already held are skipped.
///  - ATOMIC, ACROSS PROCESSES. One transaction per push, and the head row carries a concurrency token
///    (<see cref="SessionTurnHeadEntity.Revision"/>). Two Gateways writing at once - a deploy swap has two
///    processes for a moment - cannot both act on the same stale head: the second commit fails, and the
///    push re-reads and decides again, once. A key collision on the rows is handled the same way. The
///    in-process gate on top only keeps one process's own writers from queueing on the database.
///  - CONTIGUOUS WATERMARK. The head's count is the length of the contiguous prefix held, advanced
///    incrementally from the previous count in bounded pages - a lost batch in the middle is asked for
///    again rather than papered over by a later one.
///  - GENERATION SWITCH IS ORDERED AND DETERMINISTIC. A push for a different generation switches the
///    session only when its start time is LATER than the current generation's (ties broken by the
///    generation key, so two Gateways given the same two batches decide the same way); a late batch from a
///    source the session has already left stores its rows and changes nothing else. Without this a re-sent
///    pre-/clear batch would switch Chat back to the old conversation.
///  - RETENTION IS WHOLE-SESSION AND RACE-FREE. Expiry is judged on the head (last push). The head is
///    deleted first, conditionally on the very timestamp that made it expired, so a push that refreshed it
///    in between keeps it; turns are then deleted only for sessions that have NO head at all. A session's
///    stored prefix is therefore never cut from the middle. Turns of a generation the session has LEFT
///    expire on their own age, which cannot touch the current prefix.
/// </summary>
public sealed class SessionTurnStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The most turns one push may carry. A Director backfilling a long session sends several
    /// batches; a single oversized push is refused rather than accepted into one transaction.</summary>
    public const int MaxBatchSize = 500;

    /// <summary>The longest generation source text accepted (a transcript path).</summary>
    public const int MaxGenerationSourceLength = 1024;

    /// <summary>How many ordinals one page of the contiguous-watermark scan reads.</summary>
    private const int WatermarkPage = 1000;

    private readonly Func<DateTime> _utcNow;

    /// <param name="utcNow">The clock a held conversation's idleness is measured on; the system clock when omitted.</param>
    public SessionTurnStore(GatewayDatabase db, Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Store one push and answer the watermark of the generation the session is on afterwards. Throws
    /// <see cref="ArgumentException"/> for a batch that disagrees with itself (see <see cref="Validate"/>);
    /// the caller logs and refuses the push, and nothing is written.
    /// </summary>
    public TurnWatermark Append(string directorId, TurnPushBatch batch, DateTime nowUtc)
    {
        Validate(directorId, batch);
        var now = Utc(nowUtc);
        var generationKey = GenerationKey(batch.Generation);

        lock (_gate)
        {
            // One retry when another writer got there first - a concurrency-token failure on the head, or a
            // key collision on the rows. Re-reading makes the second attempt see what they wrote and decide
            // on current facts; a second failure is a fault and surfaces.
            try { return AppendOnce(directorId, batch, generationKey, now); }
            catch (DbUpdateException ex)
            {
                FileLog.Write($"[SessionTurnStore] session={batch.SessionId}: write lost a race with another writer ({ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}); re-reading and retrying once");
                return AppendOnce(directorId, batch, generationKey, now);
            }
        }
    }

    private TurnWatermark AppendOnce(string directorId, TurnPushBatch batch, string generationKey, DateTime now)
    {
        using var ctx = _db.CreateContext();
        using var tx = ctx.Database.BeginTransaction();

        var head = ctx.SessionTurnHeads.FirstOrDefault(h => h.SessionId == batch.SessionId);
        var started = StartPrecision(Utc(batch.GenerationStartedUtc));
        var isNewHead = head is null;
        if (head is null)
        {
            head = new SessionTurnHeadEntity
            {
                TenantId = ctx.ActiveTenant!,
                SessionId = batch.SessionId,
                Generation = generationKey,
                GenerationSource = batch.Generation,
                GenerationStartedUtc = started,
                Count = 0,
            };
            ctx.SessionTurnHeads.Add(head);
            FileLog.Write($"[SessionTurnStore] first turns for session={batch.SessionId} generation={Short(batch.Generation)} agent={batch.Agent}");
        }

        // Does this batch's generation become the session's current one? Yes when it already is, or when it
        // is a LATER source than the one the session is on (ties broken by the key, so the decision is the
        // same whichever process makes it). A batch from an earlier source is stored - it is real history -
        // but does not move the head: that is the delayed re-send of a pre-/clear batch, and switching to it
        // would put the old conversation back on the reader's screen.
        var sameGeneration = string.Equals(head.Generation, generationKey, StringComparison.Ordinal);
        var later = started > head.GenerationStartedUtc
                    || (started == head.GenerationStartedUtc && string.CompareOrdinal(generationKey, head.Generation) > 0);
        var switches = !sameGeneration && !isNewHead && later;
        if (switches)
        {
            FileLog.Write($"[SessionTurnStore] session={batch.SessionId} moved from generation={Short(head.GenerationSource)} to generation={Short(batch.Generation)}; earlier rows kept until retention");
            head.Generation = generationKey;
            head.GenerationSource = batch.Generation;
            head.GenerationStartedUtc = started;
            head.Count = 0;
        }
        var isCurrent = sameGeneration || switches || isNewHead;
        if (!isCurrent)
            FileLog.Write($"[SessionTurnStore] session={batch.SessionId}: late batch for generation={Short(batch.Generation)} (started {started:O}) while the session is on generation={Short(head.GenerationSource)} (started {head.GenerationStartedUtc:O}); rows stored, head unchanged");

        var added = 0;
        if (batch.Turns.Count > 0)
        {
            var first = batch.StartOrdinal;
            var last = batch.StartOrdinal + batch.Turns.Count - 1;
            var present = ctx.SessionTurns
                .Where(t => t.SessionId == batch.SessionId && t.Generation == generationKey && t.Ordinal >= first && t.Ordinal <= last)
                .Select(t => t.Ordinal)
                .ToHashSet();
            foreach (var turn in batch.Turns)
            {
                if (present.Contains(turn.Ordinal)) continue;
                ctx.SessionTurns.Add(new SessionTurnEntity
                {
                    TenantId = ctx.ActiveTenant!,
                    SessionId = batch.SessionId,
                    Generation = generationKey,
                    Ordinal = turn.Ordinal,
                    DirectorId = directorId,
                    Role = turn.Role,
                    PartsJson = JsonSerializer.Serialize(turn.Parts, Json),
                    TimestampUtc = turn.Timestamp?.UtcDateTime,
                    ContextId = turn.ContextId,
                    IsMeta = turn.IsMeta,
                    IsSidechain = turn.IsSidechain,
                    ReceivedAtUtc = now,
                });
                added++;
            }
        }
        // Rows first, so the watermark scan below sees them; the head write - with its concurrency token -
        // is in the same transaction, so a lost race rolls the rows back with it.
        ctx.SaveChanges();

        if (isCurrent)
        {
            head.DirectorId = directorId;
            head.Agent = batch.Agent;
            head.IsSupported = batch.IsSupported;
            head.IsRawText = batch.IsRawText;
            head.HistoryState = batch.HistoryState;
            head.UpdatedAtUtc = now;
            // Advance the contiguous watermark of the CURRENT generation from where it was, over the rows
            // now stored - incremental and paged, so a long backfill costs a short scan per batch, not a
            // re-count of the whole generation each time.
            head.Count = AdvanceContiguous(ctx, batch.SessionId, head.Generation, head.Count);
        }
        head.Revision++;
        ctx.SaveChanges();
        tx.Commit();

        if (batch.Turns.Count > 0)
            FileLog.Write($"[SessionTurnStore] session={batch.SessionId} generation={Short(batch.Generation)}: stored {added} of {batch.Turns.Count} pushed turn(s) from ordinal {batch.StartOrdinal}; watermark={head.Count} directorTotal={batch.TotalCount}{(isCurrent ? "" : " (late batch, not current)")}");
        return new TurnWatermark { SessionId = batch.SessionId, Generation = head.GenerationSource, Count = head.Count };
    }

    /// <summary>The session's head row (generation, watermark, agent facts), or null when nothing has been
    /// pushed for it. Cheap - one row - so a reader can ask "has anything changed?" before paying for
    /// <see cref="ReadCurrent"/>.</summary>
    public SessionTurnHeadEntity? ReadHead(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionTurnHeads.AsNoTracking().FirstOrDefault(h => h.SessionId == sessionId);
        }
    }

    /// <summary>The watermark of every session this Director has pushed, for its <c>Hello</c>.</summary>
    public IReadOnlyList<TurnWatermark> WatermarksFor(string directorId)
    {
        ArgumentException.ThrowIfNullOrEmpty(directorId);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionTurnHeads.AsNoTracking()
                .Where(h => h.DirectorId == directorId)
                .Select(h => new TurnWatermark { SessionId = h.SessionId, Generation = h.GenerationSource, Count = h.Count })
                .ToList();
        }
    }

    /// <summary>
    /// The session's current conversation - the contiguous prefix of its current generation, in order,
    /// as the <see cref="HistoryMessageDto"/> rows Chat renders. Null when nothing has been pushed for the
    /// session; an empty list when the head exists but no turn has arrived yet.
    /// </summary>
    public (SessionTurnHeadEntity Head, List<HistoryMessageDto> Messages)? ReadCurrent(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var head = ctx.SessionTurnHeads.AsNoTracking().FirstOrDefault(h => h.SessionId == sessionId);
            var key = (ctx.ActiveTenant ?? "", sessionId);
            if (head is null)
            {
                _held.Remove(key);
                return null;
            }

            var rows = HeldRows(ctx, key, head);
            var messages = new List<HistoryMessageDto>(rows.Count);
            foreach (var row in rows)
            {
                messages.Add(new HistoryMessageDto
                {
                    Role = row.Role,
                    Timestamp = row.TimestampUtc is { } ts ? new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)) : null,
                    Parts = JsonSerializer.Deserialize<List<HistoryPartDto>>(row.PartsJson, Json) ?? new List<HistoryPartDto>(),
                });
            }
            return (head, messages);
        }
    }

    // ----- the held conversations (devthrottle_internal#2199) -----
    //
    // Chat polls a session's conversation every 2.5 seconds per open screen, and the narration, the turn log and the
    // Wingman Now route read it on every stop. Each of those used to read the WHOLE current generation - every row
    // with its parts JSON, the widest rows in the database - although a stored turn never changes once it is held:
    // rows are insert-only (a push skips ordinals already present) and the head's count is the length of a
    // contiguous prefix that retention never cuts from the middle. So the rows already read are kept, and a read
    // asks the database only for the ordinals past them. The head is still read every time - it is one small row,
    // and it is what says whether anything changed.
    //
    // What is held is the stored row (role, time, parts JSON), never the objects handed out: every read
    // deserialises fresh messages, so a caller that changes what it was given cannot change the next caller's answer.

    /// <summary>One stored turn as it is held: exactly the three columns a reader is built from.</summary>
    private sealed record HeldTurn(string Role, DateTime? TimestampUtc, string PartsJson);

    private sealed class HeldConversation
    {
        public required string Generation { get; init; }
        public required List<HeldTurn> Turns { get; init; }
        public DateTime LastReadUtc { get; set; }
    }

    /// <summary>The most stored turns held across every conversation. Past it the least recently read conversations
    /// are let go. Fifty thousand rows of parts JSON is tens of megabytes - a few dozen long conversations, which is
    /// more than are ever open or being narrated at once.</summary>
    internal const int MaxHeldTurns = 50_000;

    /// <summary>A conversation nobody has read for this long is let go.</summary>
    internal static readonly TimeSpan HeldIdleLimit = TimeSpan.FromMinutes(10);

    private readonly Dictionary<(string Tenant, string SessionId), HeldConversation> _held = new();
    private int _heldTurnCount;

    /// <summary>How many stored turns are held right now, for the tests.</summary>
    internal int HeldTurnCount { get { lock (_gate) return _heldTurnCount; } }

    /// <summary>
    /// The current generation's contiguous prefix, as held rows: the held ones plus whatever the database has past
    /// them. The held rows are discarded - and the prefix read whole - when they cannot be the start of what the head
    /// now describes: another generation, or a head that counts fewer rows than are held (retention removed the
    /// session and it came back).
    /// </summary>
    private List<HeldTurn> HeldRows(GatewayDbContext ctx, (string Tenant, string SessionId) key, SessionTurnHeadEntity head)
    {
        var now = _utcNow();
        EvictIdle(now);

        if (_held.TryGetValue(key, out var held)
            && (!string.Equals(held.Generation, head.Generation, StringComparison.Ordinal) || held.Turns.Count > head.Count))
        {
            Release(key, held);
            held = null;
        }

        var from = held?.Turns.Count ?? 0;
        var sessionId = key.SessionId;
        var generation = head.Generation;
        var added = from < head.Count
            ? ctx.SessionTurns.AsNoTracking()
                .Where(t => t.SessionId == sessionId && t.Generation == generation && t.Ordinal >= from && t.Ordinal < head.Count)
                .OrderBy(t => t.Ordinal)
                .Select(t => new HeldTurn(t.Role, t.TimestampUtc, t.PartsJson))
                .ToList()
            : new List<HeldTurn>();

        var expected = head.Count - from;
        if (added.Count != expected)
        {
            // The head says rows [0, Count) are all present and they are not. That is a store fault (the watermark and
            // the rows are written in one transaction), so it is logged and the read answers exactly what the database
            // holds, as it always did - nothing is held on top of a prefix with a hole in it.
            FileLog.Write($"[SessionTurnStore] ReadCurrent session={sessionId}: head counts {head.Count} turn(s) but ordinals {from}..{head.Count - 1} returned {added.Count}; answering what is stored and holding nothing");
            if (held is not null) Release(key, held);
            if (from == 0) return added;
            return ctx.SessionTurns.AsNoTracking()
                .Where(t => t.SessionId == sessionId && t.Generation == generation && t.Ordinal < head.Count)
                .OrderBy(t => t.Ordinal)
                .Select(t => new HeldTurn(t.Role, t.TimestampUtc, t.PartsJson))
                .ToList();
        }

        if (held is null)
        {
            if (added.Count > MaxHeldTurns) return added;   // too long to hold at all; served, not kept
            held = new HeldConversation { Generation = generation, Turns = added, LastReadUtc = now };
            _held[key] = held;
            _heldTurnCount += added.Count;
        }
        else
        {
            held.Turns.AddRange(added);
            held.LastReadUtc = now;
            _heldTurnCount += added.Count;
        }

        var answer = new List<HeldTurn>(held.Turns);
        EvictOverCap(keep: key);
        return answer;
    }

    private void Release((string Tenant, string SessionId) key, HeldConversation held)
    {
        if (_held.Remove(key)) _heldTurnCount -= held.Turns.Count;
    }

    private void EvictIdle(DateTime now)
    {
        if (_held.Count == 0) return;
        List<(string, string)>? idle = null;
        foreach (var (key, held) in _held)
            if (now - held.LastReadUtc >= HeldIdleLimit) (idle ??= new()).Add(key);
        if (idle is null) return;
        foreach (var key in idle) Release(key, _held[key]);
    }

    private void EvictOverCap((string Tenant, string SessionId) keep)
    {
        if (_heldTurnCount <= MaxHeldTurns) return;
        foreach (var (key, held) in _held.Where(kv => kv.Key != keep).OrderBy(kv => kv.Value.LastReadUtc).ToList())
        {
            Release(key, held);
            if (_heldTurnCount <= MaxHeldTurns) return;
        }
    }

    /// <summary>
    /// Retention, whole sessions at a time: a session whose last push is older than the cutoff loses its
    /// head and every turn; a session still being pushed keeps its current generation intact and loses
    /// only the turns of generations it has LEFT that are older than the cutoff. Ninety days, the
    /// session-history retention, applied by <see cref="SessionHistorySweep"/>.
    /// </summary>
    public int PurgeOlderThan(DateTime cutoffUtc)
    {
        var cutoff = Utc(cutoffUtc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            using var tx = ctx.Database.BeginTransaction();
            // Heads first, and conditionally on the timestamp that makes them expired: a push that refreshed a
            // head between our read and our delete keeps its head, and therefore keeps its turns below.
            var heads = ctx.SessionTurnHeads.Where(h => h.UpdatedAtUtc < cutoff).ExecuteDelete();
            // Turns of sessions that now have NO head at all - whole sessions, never a prefix. Then the aged
            // rows of generations a live session has left; the current generation is never touched here.
            var turns = ctx.SessionTurns
                .Where(t => !ctx.SessionTurnHeads.Any(h => h.SessionId == t.SessionId))
                .ExecuteDelete();
            turns += ctx.SessionTurns
                .Where(t => t.ReceivedAtUtc < cutoff
                            && !ctx.SessionTurnHeads.Any(h => h.SessionId == t.SessionId && h.Generation == t.Generation))
                .ExecuteDelete();
            tx.Commit();
            if (turns + heads > 0)
                FileLog.Write($"[SessionTurnStore] PurgeOlderThan: removed {heads} session(s) and {turns} turn row(s) older than {cutoff:O}");
            return turns + heads;
        }
    }

    /// <summary>Refuse a batch that disagrees with itself before anything is written. A malformed push is a
    /// bug in the Director that sent it, and the honest answer is an error it can log, not a silently
    /// re-numbered conversation. Covers the whole object graph, because a push arrives deserialized and a
    /// null where a list belongs must read as "malformed", not as a crash.</summary>
    internal static void Validate(string directorId, TurnPushBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrEmpty(directorId);
        ArgumentException.ThrowIfNullOrEmpty(batch.SessionId);
        ArgumentException.ThrowIfNullOrEmpty(batch.Generation);
        if (batch.Agent is null) throw new ArgumentException("Agent is null", nameof(batch));
        if (batch.Turns is null) throw new ArgumentException("Turns is null", nameof(batch));
        if (batch.SessionId.Length > 64) throw new ArgumentException("session id longer than 64 characters", nameof(batch));
        if (directorId.Length > 64) throw new ArgumentException("director id longer than 64 characters", nameof(directorId));
        if (batch.Generation.Length > MaxGenerationSourceLength) throw new ArgumentException($"generation longer than {MaxGenerationSourceLength} characters", nameof(batch));
        if (batch.Agent.Length > 32) throw new ArgumentException("agent name longer than 32 characters", nameof(batch));
        if (batch.GenerationStartedUtc == default) throw new ArgumentException("GenerationStartedUtc is not set", nameof(batch));
        if (batch.StartOrdinal < 0) throw new ArgumentException("StartOrdinal is negative", nameof(batch));
        if (batch.Turns.Count > MaxBatchSize) throw new ArgumentException($"a turn push may carry at most {MaxBatchSize} turns; this one carries {batch.Turns.Count}", nameof(batch));
        if ((long)batch.StartOrdinal + batch.Turns.Count > batch.TotalCount)
            throw new ArgumentException($"the batch reaches ordinal {(long)batch.StartOrdinal + batch.Turns.Count - 1} but TotalCount is {batch.TotalCount}", nameof(batch));
        for (var i = 0; i < batch.Turns.Count; i++)
        {
            var turn = batch.Turns[i];
            if (turn is null) throw new ArgumentException($"turn {i} is null", nameof(batch));
            if (turn.Ordinal != batch.StartOrdinal + i)
                throw new ArgumentException($"turn {i} carries ordinal {turn.Ordinal}; expected {batch.StartOrdinal + i}", nameof(batch));
            if (turn.Role is not ("User" or "Assistant"))
                throw new ArgumentException($"turn {turn.Ordinal} has role '{turn.Role ?? "(null)"}'; expected User or Assistant", nameof(batch));
            if (turn.Parts is null) throw new ArgumentException($"turn {turn.Ordinal} has null Parts", nameof(batch));
            foreach (var part in turn.Parts)
            {
                if (part is null) throw new ArgumentException($"turn {turn.Ordinal} has a null part", nameof(batch));
                if (part.Kind is null || part.Text is null) throw new ArgumentException($"turn {turn.Ordinal} has a part with a null Kind or Text", nameof(batch));
            }
        }
    }

    /// <summary>The fixed-width key a generation source is stored under.</summary>
    internal static string GenerationKey(string generationSource)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generationSource))).ToLowerInvariant();

    /// <summary>Advance the contiguous prefix from <paramref name="from"/> over the stored ordinals, one
    /// bounded page at a time, stopping at the first gap - so the cost is the length of the NEW contiguous
    /// run, never the whole generation and never the rows beyond a gap.</summary>
    private static int AdvanceContiguous(GatewayDbContext ctx, string sessionId, string generationKey, int from)
    {
        var count = from;
        while (true)
        {
            var next = count;
            var page = ctx.SessionTurns
                .Where(t => t.SessionId == sessionId && t.Generation == generationKey && t.Ordinal >= next)
                .OrderBy(t => t.Ordinal)
                .Select(t => t.Ordinal)
                .Take(WatermarkPage)
                .ToList();
            var contiguous = true;
            foreach (var o in page)
            {
                if (o != count) { contiguous = false; break; }
                count++;
            }
            if (!contiguous || page.Count < WatermarkPage) return count;
        }
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    /// <summary>The precision a generation start is compared AND stored at: whole milliseconds. Postgres keeps
    /// microseconds and .NET keeps hundred-nanosecond ticks, so a start compared at full precision and then
    /// stored rounded could read as "later" before the write and "equal" after it - and the tie-break would
    /// then decide the next push differently (found in review). One precision on both sides, chosen coarser
    /// than either store, removes the disagreement.</summary>
    internal static DateTime StartPrecision(DateTime utc)
        => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static string Short(string generation)
        => generation.Length <= 40 ? generation : "..." + generation[^40..];
}
