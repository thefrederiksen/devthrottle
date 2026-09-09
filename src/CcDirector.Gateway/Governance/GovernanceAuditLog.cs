using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Governance;

/// <summary>
/// The Gateway's append-only governance audit log (issue #1771, spine item 4) over the
/// <c>governance_audit_events</c> table. Every row is one explicitly-recorded intervention or permission/
/// sandbox decision - the agent needing a human and what the human did, a permission request and its ruling,
/// the effective mode, the brackets of an elevated run. It is the audit trail the safety and attention-burden
/// reports read.
///
/// Append-only by contract: no update, no delete. Everything recorded here is an EVENT the caller observed,
/// never something inferred from a prose transcript, and the detail field is a short control-flow note, never
/// prompt content.
///
/// Two write shapes, mirroring the event ledger: <see cref="Append"/> for one event, <see cref="AppendBatch"/>
/// for many in one unit of work (change-detection disabled for the insert, tenant_id still set per row).
/// Threading matches the rest of the data layer: single writer, write lock, fresh pooled context per operation.
/// </summary>
public sealed class GovernanceAuditLog
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>A detail is a short control-flow note (a permission name, a mode, a reason); cap it.</summary>
    public const int MaxDetailChars = 500;

    /// <summary>The hard ceiling on one batched append.</summary>
    public const int MaxBatchSize = 1000;

    /// <summary>The hard ceiling on one list read; the default page is 500.</summary>
    public const int MaxListLimit = 5000;

    /// <summary>The event types that REQUIRE an actor - a human decision, a permission ruling, or a stop:
    /// "who decided" is an audit fact that must never be null on these.</summary>
    private static readonly HashSet<string> ActorRequired = new(StringComparer.Ordinal)
    {
        GovernanceAuditEventType.HumanRescued,
        GovernanceAuditEventType.HumanRedirected,
        GovernanceAuditEventType.HumanCancelled,
        GovernanceAuditEventType.PermissionGranted,
        GovernanceAuditEventType.PermissionDenied,
        // Mission "Stop a session": WHO stopped it is exactly the audit fact this row exists to hold. The
        // owner allowed any session to stop any other on the ground that it is audited, and a stop with a
        // null actor records that something ended a session and nothing about who - which is the one shape
        // of row that would make the ledger worse than no ledger.
        GovernanceAuditEventType.Stopped,
    };

    public GovernanceAuditLog(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Append one audit event. Returns the recorded row.</summary>
    public GovernanceAuditEventDto Append(AppendGovernanceAuditEventRequest request)
    {
        if (request is null)
            throw new GovernanceValidationException("An audit event body is required.");

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var now = DateTime.UtcNow;
            var entity = BuildEntity(request, ctx.ActiveTenant!, now);
            ctx.GovernanceAuditEvents.Add(entity);
            ctx.SaveChanges();

            FileLog.Write($"[GovernanceAuditLog] Append: id={entity.Id}, session={entity.SessionId}, " +
                          $"category={entity.Category}, type={entity.EventType}, actor={entity.Actor ?? "-"}");
            return ToDto(entity);
        }
    }

    /// <summary>
    /// Append many audit events in one unit of work, validated first: one invalid entry rejects the whole
    /// batch (the trail never lands a half-batch). Returns the number of rows written.
    /// </summary>
    public int AppendBatch(IReadOnlyList<AppendGovernanceAuditEventRequest> requests)
    {
        if (requests is null)
            throw new GovernanceValidationException("An events list is required.");
        if (requests.Count == 0)
            return 0;
        if (requests.Count > MaxBatchSize)
            throw new GovernanceValidationException(
                $"A batch carries at most {MaxBatchSize} events (got {requests.Count}).");

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var now = DateTime.UtcNow;
            var tenant = ctx.ActiveTenant!;

            var entities = new List<GovernanceAuditEventEntity>(requests.Count);
            foreach (var request in requests)
            {
                if (request is null)
                    throw new GovernanceValidationException("The events list contains a null entry.");
                entities.Add(BuildEntity(request, tenant, now));
            }

            var previousAutoDetect = ctx.ChangeTracker.AutoDetectChangesEnabled;
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                ctx.GovernanceAuditEvents.AddRange(entities);
                ctx.SaveChanges();
            }
            finally
            {
                ctx.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            }

            FileLog.Write($"[GovernanceAuditLog] AppendBatch: wrote {entities.Count} events");
            return entities.Count;
        }
    }

    /// <summary>
    /// Audit events, newest first, optionally filtered by session, run, category, event type, and time window
    /// (<paramref name="sinceUtc"/> inclusive, <paramref name="untilUtc"/> exclusive on OccurredUtc).
    /// </summary>
    public IReadOnlyList<GovernanceAuditEventDto> List(
        string? sessionId = null, Guid? runId = null, string? category = null, string? eventType = null,
        DateTime? sinceUtc = null, DateTime? untilUtc = null, int limit = 500)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            IQueryable<GovernanceAuditEventEntity> query = ctx.GovernanceAuditEvents.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var id = sessionId.Trim();
                query = query.Where(e => e.SessionId == id);
            }
            if (runId.HasValue)
                query = query.Where(e => e.RunId == runId.Value);
            if (!string.IsNullOrWhiteSpace(category))
            {
                var cat = category.Trim().ToLowerInvariant();
                query = query.Where(e => e.Category == cat);
            }
            if (!string.IsNullOrWhiteSpace(eventType))
            {
                var type = eventType.Trim().ToLowerInvariant();
                query = query.Where(e => e.EventType == type);
            }
            if (sinceUtc.HasValue)
            {
                var since = DateTime.SpecifyKind(sinceUtc.Value, DateTimeKind.Utc);
                query = query.Where(e => e.OccurredUtc >= since);
            }
            if (untilUtc.HasValue)
            {
                var until = DateTime.SpecifyKind(untilUtc.Value, DateTimeKind.Utc);
                query = query.Where(e => e.OccurredUtc < until);
            }

            return query
                .OrderByDescending(e => e.OccurredUtc)
                .ThenByDescending(e => e.RecordedUtc)
                .Take(take)
                .ToList()
                .Select(ToDto)
                .ToList();
        }
    }

    /// <summary>Validate one request and build the immutable row (tenant + timestamps stamped here).</summary>
    private static GovernanceAuditEventEntity BuildEntity(
        AppendGovernanceAuditEventRequest request, string tenant, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            throw new GovernanceValidationException("An audit event needs a sessionId.");

        var category = (request.Category ?? "").Trim().ToLowerInvariant();
        if (!GovernanceAuditCategory.All.Contains(category, StringComparer.Ordinal))
            throw new GovernanceValidationException(
                $"'{request.Category}' is not an audit category. Legal values: " +
                string.Join(", ", GovernanceAuditCategory.All) + ".");

        var eventType = (request.EventType ?? "").Trim().ToLowerInvariant();
        var legalTypes = GovernanceAuditEventType.ForCategory(category);
        if (!legalTypes.Contains(eventType, StringComparer.Ordinal))
            throw new GovernanceValidationException(
                $"'{request.EventType}' is not a legal event type for category '{category}'. Legal values: " +
                string.Join(", ", legalTypes) + ".");

        var actor = string.IsNullOrWhiteSpace(request.Actor) ? null : request.Actor.Trim();
        if (actor is null && ActorRequired.Contains(eventType))
            throw new GovernanceValidationException(
                $"Event '{eventType}' requires an actor - who made the call is an audit fact.");

        if (request.Detail is not null && request.Detail.Length > MaxDetailChars)
            throw new GovernanceValidationException(
                $"The detail is too long (limit {MaxDetailChars} characters).");

        var occurred = request.OccurredUtc.HasValue
            ? DateTime.SpecifyKind(request.OccurredUtc.Value, DateTimeKind.Utc)
            : now;
        if (occurred > now)
            occurred = now;

        return new GovernanceAuditEventEntity
        {
            TenantId = tenant,
            SessionId = request.SessionId.Trim(),
            RunId = request.RunId,
            Category = category,
            EventType = eventType,
            Actor = actor,
            Detail = request.Detail,
            OccurredUtc = occurred,
            RecordedUtc = now,
        };
    }

    private static GovernanceAuditEventDto ToDto(GovernanceAuditEventEntity e) => new()
    {
        Id = e.Id,
        SessionId = e.SessionId,
        RunId = e.RunId,
        Category = e.Category,
        EventType = e.EventType,
        Actor = e.Actor,
        Detail = e.Detail,
        OccurredUtc = e.OccurredUtc,
        RecordedUtc = e.RecordedUtc,
    };
}
