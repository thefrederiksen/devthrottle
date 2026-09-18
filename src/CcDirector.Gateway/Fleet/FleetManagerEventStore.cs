using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Fleet;

/// <summary>A stop the Gateway has just seen, stored before anything reads it.</summary>
/// <param name="IsCatchUp">The detector saw this stop for the first time already stopped (after a Gateway
/// restart), so it may be one this store already holds.</param>
public sealed record FleetManagerStopSighting(
    string SessionId,
    string SessionName,
    string AddressedTo,
    string? DirectorId,
    DateTime ObservedAtUtc,
    bool IsCatchUp);

/// <summary>A death, as it was learned.</summary>
/// <param name="Detail">How it was learned, and what is not known about it, in plain words.</param>
public sealed record FleetManagerDeath(
    string SessionId,
    string SessionName,
    string AddressedTo,
    string? DirectorId,
    bool Crashed,
    string Detail);

/// <summary>What became of a reading offered to the stops of one session.</summary>
public enum FleetManagerReadingResult
{
    /// <summary>It was attached to the stop that was waiting for it.</summary>
    Attached,

    /// <summary>No stop was waiting, so a new stop event carrying it was stored.</summary>
    StoredNew,

    /// <summary>Nothing changed: no stop was waiting, and this reading is already held (or there was no reading).</summary>
    AlreadyHeld,
}

/// <summary>A session the Gateway last knew alive while a Fleet Manager owned it.</summary>
public sealed record FleetManagerOwnedSession(
    string SessionId,
    string FleetManagerSessionId,
    string SessionName,
    string DirectorId,
    DateTime LastSeenAliveUtc);

/// <summary>What an acknowledgement did.</summary>
public enum FleetManagerEventAckStatus
{
    /// <summary>Applied: every named event is now acknowledged.</summary>
    Applied,

    /// <summary>At least one named id is not an event of this account. Nothing was changed.</summary>
    NotFound,

    /// <summary>At least one named id is a stop still waiting for its Wingman reading. Nothing was changed: that stop
    /// has not been delivered, and it is delivered - and can be acknowledged - once its reading, or the reason there is
    /// none, is stored.</summary>
    ReadingPending,
}

/// <summary>The result of <see cref="FleetManagerEventStore.Acknowledge"/>.</summary>
/// <param name="Missing">The ids that are not events of this account, when <paramref name="Status"/> is NotFound.</param>
/// <param name="Pending">The ids that are stops still waiting for their reading, when <paramref name="Status"/> is
/// ReadingPending.</param>
public sealed record FleetManagerEventAckResult(
    FleetManagerEventAckStatus Status,
    IReadOnlyList<Guid> Acknowledged,
    int AlreadyAcknowledged,
    IReadOnlyList<Guid> Missing,
    IReadOnlyList<Guid> Pending);

/// <summary>One page of <see cref="FleetManagerEventStore.ListPage"/>: the events, and the opaque cursor that continues
/// after them - null when this page is the last.</summary>
public sealed record FleetManagerEventPage(IReadOnlyList<FleetManagerEventDto> Events, string? NextCursor);

/// <summary>What <see cref="FleetManagerEventStore.Owed"/> found: the oldest events owed, and how many more are owed
/// after them.</summary>
public sealed record FleetManagerOwedEvents(IReadOnlyList<FleetManagerEventDto> Events, int MoreOwed);

/// <summary>
/// The events about sessions a Fleet Manager owns, over the <c>fleet_manager_events</c> table (the Fleet Manager
/// mission, step 4). An event is kept until it is acknowledged, across a Gateway restart and across a restart or a
/// move of the Fleet Manager.
///
/// A STOP IS STORED BEFORE IT IS READ (<see cref="RecordStop"/>), and its reading is attached when the reading
/// completes (<see cref="AttachReading"/>), whatever started that reading. So a stop is never lost because the
/// session left the roster, a Director disconnected or the Gateway stopped while it was being read: at worst it is
/// delivered with the reason there is no reading (<see cref="ExpirePendingStops"/>).
///
/// ONE EVENT PER HAPPENING. The store refuses a second copy itself, so no caller has to remember to: a stop sighted
/// again while one is still waiting for its reading, a reading it already holds offered with no stop waiting, a
/// second death of one session, and a stop seen again on a Gateway restart while the session still has an
/// unacknowledged stop are not stored again.
///
/// DEATHS ACROSS A RESTART. The owned sessions the Gateway has seen alive are kept
/// (<see cref="NoteOwnedAlive"/>), so a reconcile after a restart can raise the death of every one that is gone.
///
/// AN ACKNOWLEDGEMENT IS ALL OR NOTHING. If one named id is not an event of this account, nothing is changed and
/// the result names it - another account's id answers exactly as an unknown one does. A STOP STILL WAITING FOR ITS
/// READING CANNOT BE ACKNOWLEDGED: nothing is changed and the result names it, because acknowledging it would close a
/// stop before anyone has seen what the Wingman made of it.
///
/// EVERY EVENT IS REACHABLE. A list is served a page at a time with an opaque cursor, exactly as the outcome records
/// are (<see cref="FleetOutcomeStore.ListPage"/>), and delivery takes the oldest owed events whatever lies before
/// them (<see cref="Owed"/>), so no number of events hides one.
///
/// Tenant-partitioned by construction, like <see cref="FleetOutcomeStore"/>.
/// </summary>
public sealed class FleetManagerEventStore
{
    public const string KindStop = "stop";
    public const string KindDied = "died";

    public const string StatusUnacknowledged = "unacknowledged";
    public const string StatusAll = "all";

    public static readonly IReadOnlyList<string> Kinds = new[] { KindStop, KindDied };
    public static readonly IReadOnlyList<string> Statuses = new[] { StatusUnacknowledged, StatusAll };

    /// <summary>The page when the caller names no count, and the most one read returns.</summary>
    public const int DefaultCount = 50;
    public const int MaxCount = 200;

    /// <summary>The most ids one acknowledgement may name.</summary>
    public const int MaxAckIds = 200;

    /// <summary>The most events one delivery carries; the rest follow at the Fleet Manager's next idle moment.</summary>
    public const int MaxDeliveryBatch = MaxCount;

    /// <summary>THE TIME LIMIT: how long a stop may wait for its reading. After it the stop is given the reason there is
    /// no reading and delivered as that (<see cref="ExpirePendingStops"/>, run by the event service's reconcile), so no
    /// stop waits for ever.</summary>
    public static readonly TimeSpan PendingLimit = TimeSpan.FromMinutes(5);

    /// <summary>What a stop still waiting for its reading says about itself, wherever it is shown.</summary>
    public static readonly string PendingNote =
        "waiting for the Wingman's reading - not delivered yet, and it cannot be acknowledged yet. It is delivered when " +
        $"the reading is stored, or with the reason there is none after {PendingLimit.TotalMinutes:F0} minutes. Do not act on it until then.";

    /// <summary>The reason a stop is given when <see cref="PendingLimit"/> passes with no reading.</summary>
    public static readonly string PendingLimitReason =
        $"no reading of this stop was stored within {PendingLimit.TotalMinutes:F0} minutes";

    private static readonly JsonSerializerOptions VerdictJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FleetManagerEventStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Store a stop THE MOMENT IT IS SEEN, before anything reads it: pending until its reading, or the reason there is
    /// none, is attached. Null when this store already holds it - a stop of this session is already waiting for its
    /// reading (the same stop, sighted again), or this is a catch-up sighting and the session still has an
    /// unacknowledged stop.
    /// </summary>
    /// <exception cref="ArgumentException">A field is missing.</exception>
    public FleetManagerEventDto? RecordStop(TenantId tenant, FleetManagerStopSighting stop, DateTime nowUtc)
    {
        FileLog.Write($"[FleetManagerEventStore] RecordStop: tenant={tenant.ToLogString()}, sid={stop?.SessionId}, " +
                      $"to={stop?.AddressedTo}, observed={stop?.ObservedAtUtc:O}, catchUp={stop?.IsCatchUp}");
        try
        {
            if (stop is null) throw new ArgumentException("a stop is required");
            if (string.IsNullOrWhiteSpace(stop.SessionId)) throw new ArgumentException("sessionId is required");
            if (string.IsNullOrWhiteSpace(stop.AddressedTo)) throw new ArgumentException("addressedTo is required");

            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var sid = stop.SessionId;
                var held = ctx.FleetManagerEvents.Any(e => e.SessionId == sid && e.Kind == KindStop
                    && (e.ReadingPending || (stop.IsCatchUp && e.AcknowledgedAtUtc == null)));
                if (held)
                {
                    FileLog.Write($"[FleetManagerEventStore] RecordStop: sid={sid} - this stop is already held, not stored again");
                    return null;
                }

                var entity = new FleetManagerEventEntity
                {
                    Kind = KindStop,
                    SessionId = sid,
                    SessionName = stop.SessionName ?? "",
                    AddressedTo = stop.AddressedTo,
                    DirectorId = stop.DirectorId,
                    ReadingPending = true,
                    StopObservedAtUtc = Utc(stop.ObservedAtUtc),
                    CreatedAtUtc = Utc(nowUtc),
                };
                entity.TenantId = ctx.ActiveTenant!;
                ctx.FleetManagerEvents.Add(entity);
                ctx.SaveChanges();
                FileLog.Write($"[FleetManagerEventStore] RecordStop: stored id={entity.Id}, sid={sid}, pending its reading");
                return ToDto(entity);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventStore] RecordStop FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// A reading of this session has completed: attach it to the stop waiting for it. With no stop waiting, a real
    /// reading this store does not already hold is stored as a stop of its own (a stop the Gateway did not see end
    /// in this process - a snooze expiry after a restart); a reason with no reading changes nothing then, because
    /// no stop is owed one.
    /// </summary>
    /// <param name="verdict">The stored reading (accepted or failed), or null.</param>
    /// <param name="noVerdictReason">Why there is no reading, when <paramref name="verdict"/> is null.</param>
    /// <param name="owner">Who to address a new stop to, and its name and Director; used only when no stop waits.</param>
    /// <exception cref="ArgumentException">Neither a reading nor a reason was given.</exception>
    public (FleetManagerReadingResult Result, IReadOnlyList<FleetManagerEventDto> Events) AttachReading(
        TenantId tenant, string sessionId, TurnVerdictDto? verdict, string? noVerdictReason,
        FleetManagerStopSighting? owner, DateTime nowUtc)
    {
        FileLog.Write($"[FleetManagerEventStore] AttachReading: tenant={tenant.ToLogString()}, sid={sessionId}, " +
                      $"verdict={verdict?.VerdictId}, reason={noVerdictReason}");
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId is required");
            if (verdict is null && string.IsNullOrWhiteSpace(noVerdictReason))
                throw new ArgumentException("a stop with no reading must say why the Wingman did not read it");

            var verdictId = string.IsNullOrEmpty(verdict?.VerdictId) ? null : verdict!.VerdictId;
            var json = verdict is null ? null : JsonSerializer.Serialize(verdict, VerdictJsonOptions);
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var pending = ctx.FleetManagerEvents
                    .Where(e => e.SessionId == sessionId && e.Kind == KindStop && e.ReadingPending)
                    .ToList();
                if (pending.Count > 0)
                {
                    foreach (var row in pending)
                    {
                        row.ReadingPending = false;
                        row.VerdictId = verdictId;
                        row.VerdictJson = json;
                        row.NoVerdictReason = verdict is null ? noVerdictReason : null;
                    }
                    ctx.SaveChanges();
                    FileLog.Write($"[FleetManagerEventStore] AttachReading: sid={sessionId}, attached to {pending.Count} waiting stop(s)");
                    return (FleetManagerReadingResult.Attached, pending.Select(ToDto).ToList());
                }

                if (verdict is null || owner is null
                    || (verdictId is not null && ctx.FleetManagerEvents.Any(
                        e => e.SessionId == sessionId && e.Kind == KindStop && e.VerdictId == verdictId)))
                {
                    FileLog.Write($"[FleetManagerEventStore] AttachReading: sid={sessionId} - no stop waiting and nothing new to store");
                    return (FleetManagerReadingResult.AlreadyHeld, Array.Empty<FleetManagerEventDto>());
                }

                var entity = new FleetManagerEventEntity
                {
                    Kind = KindStop,
                    SessionId = sessionId,
                    SessionName = owner.SessionName ?? "",
                    AddressedTo = owner.AddressedTo,
                    DirectorId = owner.DirectorId,
                    VerdictId = verdictId,
                    VerdictJson = json,
                    StopObservedAtUtc = Utc(owner.ObservedAtUtc),
                    CreatedAtUtc = Utc(nowUtc),
                };
                entity.TenantId = ctx.ActiveTenant!;
                ctx.FleetManagerEvents.Add(entity);
                ctx.SaveChanges();
                FileLog.Write($"[FleetManagerEventStore] AttachReading: sid={sessionId}, no stop waiting - stored id={entity.Id}");
                return (FleetManagerReadingResult.StoredNew, new[] { ToDto(entity) });
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventStore] AttachReading FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// The session went back to work (or exited) before its stop was read: it is not stopped, so the stop waiting for
    /// a reading is removed. Only a PENDING stop is removed, and a pending stop has never been delivered.
    /// </summary>
    /// <returns>How many were removed.</returns>
    public int WithdrawPendingStops(TenantId tenant, string sessionId)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var pending = ctx.FleetManagerEvents
                .Where(e => e.SessionId == sessionId && e.Kind == KindStop && e.ReadingPending)
                .ToList();
            if (pending.Count == 0) return 0;
            ctx.FleetManagerEvents.RemoveRange(pending);
            ctx.SaveChanges();
            FileLog.Write($"[FleetManagerEventStore] WithdrawPendingStops: tenant={tenant.ToLogString()}, sid={sessionId}, removed={pending.Count}");
            return pending.Count;
        }
    }

    /// <summary>
    /// Give every stop still waiting for a reading since before <paramref name="createdBeforeUtc"/> the reason there is
    /// none, so it is delivered rather than held for ever - a Gateway that stopped mid-reading, or a reading that
    /// never reported back.
    /// </summary>
    /// <returns>The stops that were given the reason.</returns>
    public IReadOnlyList<FleetManagerEventDto> ExpirePendingStops(TenantId tenant, DateTime createdBeforeUtc, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("a reason is required", nameof(reason));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var cutoff = Utc(createdBeforeUtc);
            var rows = ctx.FleetManagerEvents
                .Where(e => e.Kind == KindStop && e.ReadingPending && e.CreatedAtUtc < cutoff)
                .ToList();
            foreach (var row in rows)
            {
                row.ReadingPending = false;
                row.NoVerdictReason = reason;
            }
            if (rows.Count > 0)
            {
                ctx.SaveChanges();
                FileLog.Write($"[FleetManagerEventStore] ExpirePendingStops: tenant={tenant.ToLogString()}, expired={rows.Count}: {reason}");
            }
            return rows.Select(ToDto).ToList();
        }
    }

    /// <summary>Record a death. Null when this store already holds a death of this session.</summary>
    /// <exception cref="ArgumentException">A field is missing.</exception>
    public FleetManagerEventDto? RecordDeath(TenantId tenant, FleetManagerDeath death, DateTime nowUtc)
    {
        FileLog.Write($"[FleetManagerEventStore] RecordDeath: tenant={tenant.ToLogString()}, sid={death?.SessionId}, " +
                      $"to={death?.AddressedTo}, crashed={death?.Crashed}, detail={death?.Detail}");
        try
        {
            if (death is null) throw new ArgumentException("a death is required");
            if (string.IsNullOrWhiteSpace(death.SessionId)) throw new ArgumentException("sessionId is required");
            if (string.IsNullOrWhiteSpace(death.AddressedTo)) throw new ArgumentException("addressedTo is required");
            if (string.IsNullOrWhiteSpace(death.Detail)) throw new ArgumentException("a death must say how it was learned");

            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var sid = death.SessionId;
                // Ending the owned-session row and storing the death are one save, so a restart finds either both or neither.
                var tracked = ctx.FleetManagerOwnedSessions.FirstOrDefault(o => o.SessionId == sid);
                if (tracked is not null && tracked.EndedAtUtc is null) tracked.EndedAtUtc = Utc(nowUtc);
                // A died session's stop can no longer be read.
                foreach (var row in ctx.FleetManagerEvents.Where(e => e.SessionId == sid && e.Kind == KindStop && e.ReadingPending))
                {
                    row.ReadingPending = false;
                    row.NoVerdictReason = "the session died before the Wingman's reading of this stop was stored";
                }

                if (ctx.FleetManagerEvents.Any(e => e.SessionId == sid && e.Kind == KindDied))
                {
                    ctx.SaveChanges();
                    FileLog.Write($"[FleetManagerEventStore] RecordDeath: sid={sid} - its death is already held, not stored again");
                    return null;
                }

                var entity = new FleetManagerEventEntity
                {
                    Kind = KindDied,
                    SessionId = sid,
                    SessionName = death.SessionName ?? "",
                    AddressedTo = death.AddressedTo,
                    DirectorId = death.DirectorId,
                    Crashed = death.Crashed,
                    Detail = death.Detail,
                    CreatedAtUtc = Utc(nowUtc),
                };
                entity.TenantId = ctx.ActiveTenant!;
                ctx.FleetManagerEvents.Add(entity);
                ctx.SaveChanges();
                FileLog.Write($"[FleetManagerEventStore] RecordDeath: stored id={entity.Id}, sid={sid}");
                return ToDto(entity);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventStore] RecordDeath FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Remember that this session was seen alive while <see cref="FleetManagerOwnedSession.FleetManagerSessionId"/>
    /// owned it. Written again only when its owner, name or Director changed, or a new session is seen. A session
    /// whose death is already recorded is not brought back.
    /// </summary>
    /// <returns>True when a row was written.</returns>
    public bool NoteOwnedAlive(TenantId tenant, FleetManagerOwnedSession owned, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(owned);
        if (string.IsNullOrWhiteSpace(owned.SessionId)) throw new ArgumentException("sessionId is required", nameof(owned));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.FleetManagerOwnedSessions.FirstOrDefault(o => o.SessionId == owned.SessionId);
            if (row is null)
            {
                row = new FleetManagerOwnedSessionEntity
                {
                    SessionId = owned.SessionId,
                    FirstSeenAliveUtc = Utc(nowUtc),
                };
                row.TenantId = ctx.ActiveTenant!;
                ctx.FleetManagerOwnedSessions.Add(row);
            }
            else if (row.EndedAtUtc is not null
                     || (row.FleetManagerSessionId == owned.FleetManagerSessionId && row.SessionName == owned.SessionName
                         && row.DirectorId == owned.DirectorId))
            {
                return false;
            }

            row.FleetManagerSessionId = owned.FleetManagerSessionId;
            row.SessionName = owned.SessionName ?? "";
            row.DirectorId = owned.DirectorId ?? "";
            row.LastSeenAliveUtc = Utc(nowUtc);
            ctx.SaveChanges();
            FileLog.Write($"[FleetManagerEventStore] NoteOwnedAlive: tenant={tenant.ToLogString()}, sid={owned.SessionId}, " +
                          $"owner={owned.FleetManagerSessionId}, director={owned.DirectorId}");
            return true;
        }
    }

    /// <summary>The owned session this store last knew alive, or null (never seen, or already dead).</summary>
    public FleetManagerOwnedSession? OwnedAlive(TenantId tenant, string sessionId)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.FleetManagerOwnedSessions.AsNoTracking()
            .Where(o => o.SessionId == sessionId && o.EndedAtUtc == null)
            .AsEnumerable()
            .Select(ToOwned)
            .FirstOrDefault();
    }

    /// <summary>Every owned session this account's store still believes alive.</summary>
    public IReadOnlyList<FleetManagerOwnedSession> AllOwnedAlive(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.FleetManagerOwnedSessions.AsNoTracking()
            .Where(o => o.EndedAtUtc == null)
            .AsEnumerable()
            .Select(ToOwned)
            .ToList();
    }

    private static FleetManagerOwnedSession ToOwned(FleetManagerOwnedSessionEntity o)
        => new(o.SessionId, o.FleetManagerSessionId, o.SessionName, o.DirectorId,
            DateTime.SpecifyKind(o.LastSeenAliveUtc, DateTimeKind.Utc));

    /// <summary>The first page of this account's events - <see cref="ListPage"/> with no cursor.</summary>
    /// <exception cref="ArgumentException">A filter is not one of its accepted values.</exception>
    public IReadOnlyList<FleetManagerEventDto> List(TenantId tenant, string status, int count)
        => ListPage(tenant, status, count, cursor: null).Events;

    /// <summary>
    /// One page of this account's events, and the cursor that continues after it.
    ///
    /// UNACKNOWLEDGED EVENTS ARE OLDEST FIRST - those are the ones owed, in the order they happened. ALL EVENTS ARE
    /// NEWEST FIRST - a history, read back from now. The order is <c>(CreatedAtUtc, Id)</c>, unique because the id is,
    /// and a cursor names the last event a page returned, so the next page starts strictly after it. There is no
    /// offset: an event acknowledged between pages (which leaves the unacknowledged list) moves no other event, so
    /// nothing after the cursor is skipped. An event stored after the first page of the unacknowledged list is newer
    /// than every cursor and so is on a later page; an event stored after the first page of the history is not, and
    /// listing again from the start shows it. A cursor is issued for one status and is refused for the other.
    /// </summary>
    /// <param name="status"><c>unacknowledged</c> or <c>all</c>.</param>
    /// <param name="count">How many at most, 1 to <see cref="MaxCount"/>.</param>
    /// <param name="cursor">The <see cref="FleetManagerEventPage.NextCursor"/> of the page before, or null for the first.</param>
    /// <exception cref="ArgumentException">A filter is not one of its accepted values, or the cursor is not one this
    /// Gateway issued for this status.</exception>
    public FleetManagerEventPage ListPage(TenantId tenant, string status, int count, string? cursor)
    {
        FileLog.Write($"[FleetManagerEventStore] ListPage: tenant={tenant}, status={status}, count={count}, " +
                      $"cursor={(cursor is null ? "none" : "given")}");
        if (!Statuses.Contains(status))
            throw new ArgumentException($"status '{status}' is not valid; use one of: {string.Join(", ", Statuses)}");
        if (count < 1 || count > MaxCount)
            throw new ArgumentException($"count must be between 1 and {MaxCount}, got {count}");
        var after = cursor is null ? ((DateTime CreatedAtUtc, Guid Id)?)null : DecodeCursor(cursor, status);

        using var ctx = _db.CreateContext(tenant);
        var query = ctx.FleetManagerEvents.AsNoTracking();
        var oldestFirst = status == StatusUnacknowledged;
        if (oldestFirst) query = query.Where(e => e.AcknowledgedAtUtc == null);
        if (after is { } a)
        {
            var at = a.CreatedAtUtc;
            var id = a.Id;
            query = oldestFirst
                ? query.Where(e => e.CreatedAtUtc > at || (e.CreatedAtUtc == at && e.Id.CompareTo(id) > 0))
                : query.Where(e => e.CreatedAtUtc < at || (e.CreatedAtUtc == at && e.Id.CompareTo(id) < 0));
        }
        var ordered = oldestFirst
            ? query.OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id)
            : query.OrderByDescending(e => e.CreatedAtUtc).ThenByDescending(e => e.Id);
        // One more than asked, so the page knows whether any event remains after it.
        var rows = ordered.Take(count + 1).ToList();
        var more = rows.Count > count;
        if (more) rows.RemoveAt(rows.Count - 1);
        var next = more ? EncodeCursor(status, rows[^1]) : null;
        FileLog.Write($"[FleetManagerEventStore] ListPage: returned={rows.Count}, hasMore={more}");
        return new FleetManagerEventPage(rows.Select(ToDto).ToList(), next);
    }

    /// <summary>How many events of this account have this status, counted by the database - never from a capped
    /// page, so a reader of one page can say how many there are in all.</summary>
    /// <exception cref="ArgumentException">The status is not one of its accepted values.</exception>
    public int Count(TenantId tenant, string status)
    {
        if (!Statuses.Contains(status))
            throw new ArgumentException($"status '{status}' is not valid; use one of: {string.Join(", ", Statuses)}");
        using var ctx = _db.CreateContext(tenant);
        var query = ctx.FleetManagerEvents.AsNoTracking();
        if (status == StatusUnacknowledged) query = query.Where(e => e.AcknowledgedAtUtc == null);
        var total = query.Count();
        FileLog.Write($"[FleetManagerEventStore] Count: tenant={tenant}, status={status}, total={total}");
        return total;
    }

    /// <summary>How many unacknowledged stops of this account are still waiting for their reading.</summary>
    public int CountPending(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        return ctx.FleetManagerEvents.AsNoTracking().Count(e => e.AcknowledgedAtUtc == null && e.ReadingPending);
    }

    /// <summary>The first page of this account's unacknowledged events, oldest first, up to <see cref="MaxCount"/> -
    /// including stops still waiting for their reading, which say so. The rest are reached with
    /// <see cref="ListPage"/>.</summary>
    public IReadOnlyList<FleetManagerEventDto> Unacknowledged(TenantId tenant) => List(tenant, StatusUnacknowledged, MaxCount);

    /// <summary>
    /// What is owed to <paramref name="fleetManagerSessionId"/>, OLDEST FIRST: the unacknowledged events whose reading
    /// is settled and that were not already delivered to that session, at most <paramref name="max"/>, and how many
    /// more are owed after them. Asked of the database, never picked from a capped page, so however many events wait
    /// before them - delivered to this session but not yet acknowledged, or still waiting for a reading - the oldest
    /// owed are found.
    /// </summary>
    public FleetManagerOwedEvents Owed(TenantId tenant, string fleetManagerSessionId, int max)
    {
        if (string.IsNullOrWhiteSpace(fleetManagerSessionId))
            throw new ArgumentException("the Fleet Manager session is required", nameof(fleetManagerSessionId));
        if (max < 1 || max > MaxDeliveryBatch)
            throw new ArgumentException($"max must be between 1 and {MaxDeliveryBatch}, got {max}", nameof(max));

        using var ctx = _db.CreateContext(tenant);
        var target = fleetManagerSessionId.ToLowerInvariant();
        var query = ctx.FleetManagerEvents.AsNoTracking()
            .Where(e => e.AcknowledgedAtUtc == null && !e.ReadingPending
                        && (e.DeliveredTo == null || e.DeliveredTo.ToLower() != target));
        var rows = query.OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id).Take(max).ToList();
        var more = rows.Count < max ? 0 : query.Count() - rows.Count;
        FileLog.Write($"[FleetManagerEventStore] Owed: tenant={tenant}, to={fleetManagerSessionId}, batch={rows.Count}, more={more}");
        return new FleetManagerOwedEvents(rows.Select(ToDto).ToList(), more);
    }

    private const string CursorVersion = "e1";

    private static string EncodeCursor(string status, FleetManagerEventEntity last)
    {
        var text = $"{CursorVersion}:{status}:{DateTime.SpecifyKind(last.CreatedAtUtc, DateTimeKind.Utc).Ticks}:{last.Id:N}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static (DateTime CreatedAtUtc, Guid Id) DecodeCursor(string cursor, string status)
    {
        var refusal = $"cursor '{cursor}' is not one this Gateway issued for status {status}; " +
                      "list again without a cursor to start from the first page";
        var b64 = cursor.Trim().Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            throw new ArgumentException(refusal);
        }
        var parts = text.Split(':');
        if (parts.Length != 4 || parts[0] != CursorVersion || parts[1] != status
            || !long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(parts[3], "N", out var id))
            throw new ArgumentException(refusal);
        return (new DateTime(ticks, DateTimeKind.Utc), id);
    }

    /// <summary>Record that these events were delivered to <paramref name="fleetManagerSessionId"/>.</summary>
    public void MarkDelivered(TenantId tenant, IReadOnlyCollection<Guid> ids, string fleetManagerSessionId, DateTime nowUtc)
    {
        FileLog.Write($"[FleetManagerEventStore] MarkDelivered: tenant={tenant}, count={ids?.Count}, to={fleetManagerSessionId}");
        ArgumentNullException.ThrowIfNull(ids);
        if (string.IsNullOrWhiteSpace(fleetManagerSessionId))
            throw new ArgumentException("the Fleet Manager session is required", nameof(fleetManagerSessionId));
        if (ids.Count == 0) return;

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var wanted = ids.ToList();
            var rows = ctx.FleetManagerEvents.Where(e => wanted.Contains(e.Id)).ToList();
            foreach (var row in rows)
            {
                row.DeliveredAtUtc = Utc(nowUtc);
                row.DeliveredTo = fleetManagerSessionId;
                row.DeliveryCount++;
            }
            ctx.SaveChanges();
            FileLog.Write($"[FleetManagerEventStore] MarkDelivered: marked={rows.Count}");
        }
    }

    /// <summary>
    /// Acknowledge the named events, or, when <paramref name="all"/> is true, every unacknowledged event that was
    /// delivered to <paramref name="deliveredTo"/> - never one that session has not been sent. All or nothing: one id
    /// that is not an event of this account changes nothing.
    /// </summary>
    /// <param name="deliveredTo">The acknowledging Fleet Manager session. Required with <paramref name="all"/>.</param>
    /// <exception cref="ArgumentException">Neither ids nor all were given, or both were, or too many ids, or all
    /// without the acknowledging session.</exception>
    public FleetManagerEventAckResult Acknowledge(TenantId tenant, IReadOnlyCollection<Guid>? ids, bool all,
        string? deliveredTo, DateTime nowUtc)
    {
        FileLog.Write($"[FleetManagerEventStore] Acknowledge: tenant={tenant}, ids={ids?.Count}, all={all}, deliveredTo={deliveredTo}");
        try
        {
            var named = ids?.Distinct().ToList() ?? new List<Guid>();
            if (all && named.Count > 0) throw new ArgumentException("give either ids or all, not both");
            if (!all && named.Count == 0) throw new ArgumentException("give the event ids to acknowledge, or all: true");
            if (all && string.IsNullOrWhiteSpace(deliveredTo))
                throw new ArgumentException("acknowledging all needs the session the events were delivered to");
            if (named.Count > MaxAckIds)
                throw new ArgumentException($"{named.Count} ids given; the most one acknowledgement accepts is {MaxAckIds}");

            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                List<FleetManagerEventEntity> rows;
                if (all)
                {
                    // Only what this session was sent: an event it has not seen is not its to close.
                    rows = ctx.FleetManagerEvents
                        .Where(e => e.AcknowledgedAtUtc == null && !e.ReadingPending && e.DeliveredTo == deliveredTo)
                        .ToList();
                }
                else
                {
                    rows = ctx.FleetManagerEvents.Where(e => named.Contains(e.Id)).ToList();
                    var missing = named.Where(id => rows.All(r => r.Id != id)).ToList();
                    if (missing.Count > 0)
                    {
                        FileLog.Write($"[FleetManagerEventStore] Acknowledge: refused, {missing.Count} id(s) not in this account; nothing changed");
                        return new FleetManagerEventAckResult(FleetManagerEventAckStatus.NotFound,
                            Array.Empty<Guid>(), 0, missing, Array.Empty<Guid>());
                    }
                    // Not yet delivered, and nobody has seen what the Wingman made of it: it is not anyone's to close.
                    var pending = rows.Where(r => r.ReadingPending && r.AcknowledgedAtUtc is null).Select(r => r.Id).ToList();
                    if (pending.Count > 0)
                    {
                        FileLog.Write($"[FleetManagerEventStore] Acknowledge: refused, {pending.Count} stop(s) still waiting for their reading; nothing changed");
                        return new FleetManagerEventAckResult(FleetManagerEventAckStatus.ReadingPending,
                            Array.Empty<Guid>(), 0, Array.Empty<Guid>(), pending);
                    }
                }

                var already = rows.Count(r => r.AcknowledgedAtUtc is not null);
                var acknowledged = new List<Guid>();
                foreach (var row in rows.Where(r => r.AcknowledgedAtUtc is null))
                {
                    row.AcknowledgedAtUtc = Utc(nowUtc);
                    acknowledged.Add(row.Id);
                }
                ctx.SaveChanges();
                FileLog.Write($"[FleetManagerEventStore] Acknowledge: acknowledged={acknowledged.Count}, already={already}");
                return new FleetManagerEventAckResult(FleetManagerEventAckStatus.Applied, acknowledged, already,
                    Array.Empty<Guid>(), Array.Empty<Guid>());
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventStore] Acknowledge FAILED: {ex.Message}");
            throw;
        }
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();

    private static DateTime? Utc(DateTime? t) => t is null ? null : DateTime.SpecifyKind(t.Value, DateTimeKind.Utc);

    private static FleetManagerEventDto ToDto(FleetManagerEventEntity e) => new()
    {
        Id = e.Id.ToString(),
        Kind = e.Kind,
        SessionId = e.SessionId,
        SessionName = e.SessionName,
        AddressedTo = e.AddressedTo,
        Crashed = e.Crashed,
        Verdict = e.VerdictJson is null ? null : JsonSerializer.Deserialize<TurnVerdictDto>(e.VerdictJson, VerdictJsonOptions),
        NoVerdictReason = e.NoVerdictReason,
        CreatedAtUtc = DateTime.SpecifyKind(e.CreatedAtUtc, DateTimeKind.Utc),
        DeliveredAtUtc = Utc(e.DeliveredAtUtc),
        DeliveredTo = e.DeliveredTo,
        DeliveryCount = e.DeliveryCount,
        AcknowledgedAtUtc = Utc(e.AcknowledgedAtUtc),
        ReadingPending = e.ReadingPending,
        ReadingNote = e.ReadingPending ? PendingNote : null,
        StopObservedAtUtc = Utc(e.StopObservedAtUtc),
        DirectorId = e.DirectorId,
        Detail = e.Detail,
    };
}
