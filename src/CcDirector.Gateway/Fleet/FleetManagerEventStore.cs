using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Fleet;

/// <summary>What to record for one event. <see cref="IsCatchUp"/> marks a stop the detector saw for the first
/// time already stopped (after a Gateway restart), which may be one this store already holds.</summary>
public sealed record FleetManagerEventDraft(
    string Kind,
    string SessionId,
    string SessionName,
    string AddressedTo,
    bool? Crashed = null,
    TurnVerdictDto? Verdict = null,
    string? NoVerdictReason = null,
    bool IsCatchUp = false);

/// <summary>What an acknowledgement did.</summary>
public enum FleetManagerEventAckStatus
{
    /// <summary>Applied: every named event is now acknowledged.</summary>
    Applied,

    /// <summary>At least one named id is not an event of this account. Nothing was changed.</summary>
    NotFound,
}

/// <summary>The result of <see cref="FleetManagerEventStore.Acknowledge"/>.</summary>
/// <param name="Missing">The ids that are not events of this account, when <paramref name="Status"/> is NotFound.</param>
public sealed record FleetManagerEventAckResult(
    FleetManagerEventAckStatus Status,
    IReadOnlyList<Guid> Acknowledged,
    int AlreadyAcknowledged,
    IReadOnlyList<Guid> Missing);

/// <summary>
/// The events about sessions a Fleet Manager owns, over the <c>fleet_manager_events</c> table (the Fleet Manager
/// mission, step 4). An event is kept until it is acknowledged, across a Gateway restart and across a restart or a
/// move of the Fleet Manager.
///
/// ONE EVENT PER HAPPENING. The store refuses a second copy itself, so no caller has to remember to: a stop whose
/// stored reading this store already holds, a second death of one session, and a stop seen again on a Gateway
/// restart while the session still has an unacknowledged stop are not stored again.
///
/// AN ACKNOWLEDGEMENT IS ALL OR NOTHING. If one named id is not an event of this account, nothing is changed and
/// the result names it - another account's id answers exactly as an unknown one does.
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

    private static readonly JsonSerializerOptions VerdictJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FleetManagerEventStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Record one event. Null when this store already holds it (see the class remarks).</summary>
    /// <exception cref="ArgumentException">A field is missing or wrong.</exception>
    public FleetManagerEventDto? Enqueue(TenantId tenant, FleetManagerEventDraft draft, DateTime nowUtc)
    {
        FileLog.Write($"[FleetManagerEventStore] Enqueue: tenant={tenant}, kind={draft?.Kind}, sid={draft?.SessionId}, " +
                      $"to={draft?.AddressedTo}, verdict={draft?.Verdict?.VerdictId}, catchUp={draft?.IsCatchUp}");
        try
        {
            if (draft is null) throw new ArgumentException("an event is required");
            if (!Kinds.Contains(draft.Kind))
                throw new ArgumentException($"kind '{draft.Kind}' is not valid; use one of: {string.Join(", ", Kinds)}");
            if (string.IsNullOrWhiteSpace(draft.SessionId)) throw new ArgumentException("sessionId is required");
            if (string.IsNullOrWhiteSpace(draft.AddressedTo)) throw new ArgumentException("addressedTo is required");
            if (draft.Kind == KindStop && draft.Verdict is null && string.IsNullOrWhiteSpace(draft.NoVerdictReason))
                throw new ArgumentException("a stop with no reading must say why the Wingman did not read it");

            var verdictId = draft.Verdict?.VerdictId;
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var sid = draft.SessionId;
                var duplicate = draft.Kind switch
                {
                    KindDied => ctx.FleetManagerEvents.Any(e => e.SessionId == sid && e.Kind == KindDied),
                    _ when !string.IsNullOrEmpty(verdictId) =>
                        ctx.FleetManagerEvents.Any(e => e.SessionId == sid && e.Kind == KindStop && e.VerdictId == verdictId),
                    _ when draft.IsCatchUp =>
                        ctx.FleetManagerEvents.Any(e => e.SessionId == sid && e.Kind == KindStop && e.AcknowledgedAtUtc == null),
                    _ => false,
                };
                if (duplicate)
                {
                    FileLog.Write($"[FleetManagerEventStore] Enqueue: sid={sid}, kind={draft.Kind} - already held, not stored again");
                    return null;
                }

                var entity = new FleetManagerEventEntity
                {
                    Kind = draft.Kind,
                    SessionId = sid,
                    SessionName = draft.SessionName ?? "",
                    AddressedTo = draft.AddressedTo,
                    Crashed = draft.Kind == KindDied ? draft.Crashed ?? false : null,
                    VerdictId = string.IsNullOrEmpty(verdictId) ? null : verdictId,
                    VerdictJson = draft.Verdict is null ? null : JsonSerializer.Serialize(draft.Verdict, VerdictJsonOptions),
                    NoVerdictReason = draft.Verdict is null ? draft.NoVerdictReason : null,
                    CreatedAtUtc = Utc(nowUtc),
                };
                entity.TenantId = ctx.ActiveTenant!;
                ctx.FleetManagerEvents.Add(entity);
                ctx.SaveChanges();
                FileLog.Write($"[FleetManagerEventStore] Enqueue: stored id={entity.Id}, kind={entity.Kind}, sid={sid}");
                return ToDto(entity);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetManagerEventStore] Enqueue FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>This account's events, OLDEST FIRST.</summary>
    /// <param name="status"><c>unacknowledged</c> or <c>all</c>.</param>
    /// <exception cref="ArgumentException">A filter is not one of its accepted values.</exception>
    public IReadOnlyList<FleetManagerEventDto> List(TenantId tenant, string status, int count)
    {
        FileLog.Write($"[FleetManagerEventStore] List: tenant={tenant}, status={status}, count={count}");
        if (!Statuses.Contains(status))
            throw new ArgumentException($"status '{status}' is not valid; use one of: {string.Join(", ", Statuses)}");
        if (count < 1 || count > MaxCount)
            throw new ArgumentException($"count must be between 1 and {MaxCount}, got {count}");

        using var ctx = _db.CreateContext(tenant);
        var query = ctx.FleetManagerEvents.AsNoTracking();
        if (status == StatusUnacknowledged) query = query.Where(e => e.AcknowledgedAtUtc == null);
        // All events: the newest page, shown oldest first. Unacknowledged: the oldest page - those are the ones owed.
        var rows = status == StatusAll
            ? query.OrderByDescending(e => e.CreatedAtUtc).ThenByDescending(e => e.Id).Take(count).ToList()
                .OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id).ToList()
            : query.OrderBy(e => e.CreatedAtUtc).ThenBy(e => e.Id).Take(count).ToList();
        FileLog.Write($"[FleetManagerEventStore] List: returned={rows.Count}");
        return rows.Select(ToDto).ToList();
    }

    /// <summary>Every unacknowledged event of this account, oldest first, up to <see cref="MaxCount"/>.</summary>
    public IReadOnlyList<FleetManagerEventDto> Unacknowledged(TenantId tenant) => List(tenant, StatusUnacknowledged, MaxCount);

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
    /// Acknowledge the named events, or every unacknowledged one when <paramref name="all"/> is true. All or nothing:
    /// one id that is not an event of this account changes nothing.
    /// </summary>
    /// <exception cref="ArgumentException">Neither ids nor all were given, or both were, or too many ids.</exception>
    public FleetManagerEventAckResult Acknowledge(TenantId tenant, IReadOnlyCollection<Guid>? ids, bool all, DateTime nowUtc)
    {
        FileLog.Write($"[FleetManagerEventStore] Acknowledge: tenant={tenant}, ids={ids?.Count}, all={all}");
        try
        {
            var named = ids?.Distinct().ToList() ?? new List<Guid>();
            if (all && named.Count > 0) throw new ArgumentException("give either ids or all, not both");
            if (!all && named.Count == 0) throw new ArgumentException("give the event ids to acknowledge, or all: true");
            if (named.Count > MaxAckIds)
                throw new ArgumentException($"{named.Count} ids given; the most one acknowledgement accepts is {MaxAckIds}");

            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                List<FleetManagerEventEntity> rows;
                if (all)
                {
                    rows = ctx.FleetManagerEvents.Where(e => e.AcknowledgedAtUtc == null).ToList();
                }
                else
                {
                    rows = ctx.FleetManagerEvents.Where(e => named.Contains(e.Id)).ToList();
                    var missing = named.Where(id => rows.All(r => r.Id != id)).ToList();
                    if (missing.Count > 0)
                    {
                        FileLog.Write($"[FleetManagerEventStore] Acknowledge: refused, {missing.Count} id(s) not in this account; nothing changed");
                        return new FleetManagerEventAckResult(FleetManagerEventAckStatus.NotFound,
                            Array.Empty<Guid>(), 0, missing);
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
                    Array.Empty<Guid>());
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
    };
}
