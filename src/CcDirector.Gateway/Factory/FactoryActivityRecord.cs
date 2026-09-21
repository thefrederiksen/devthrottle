using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory;

/// <summary>
/// A factory activity row the Gateway refuses, with the sentence the caller reads (a 400 at the route).
/// </summary>
public sealed class FactoryActivityValidationException : Exception
{
    public FactoryActivityValidationException(string message) : base(message) { }
}

/// <summary>
/// The append-only factory activity record over the <c>factory_activity</c> table (Website Business Factory,
/// product track). See <see cref="FactoryActivityEntity"/> for why it is its own table.
///
/// APPEND AND QUERY, AND NOTHING ELSE. There is no update, no delete, and no retention sweep - a test reads
/// this type's public surface and fails if a mutating method other than <see cref="Append"/> appears. A wrong
/// row is corrected by appending a new row whose <c>CorrectsId</c> names it.
///
/// Threading matches the rest of the data layer: a write lock, and a fresh pooled context per operation that
/// carries the request's tenant.
/// </summary>
public sealed class FactoryActivityRecord
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The longest "what happened" sentence the record accepts.</summary>
    public const int MaxWhatChars = 500;

    /// <summary>The longest factory or factory agent identifier.</summary>
    public const int MaxIdChars = 128;

    /// <summary>The longest factory agent version.</summary>
    public const int MaxVersionChars = 64;

    /// <summary>The longest session identifier.</summary>
    public const int MaxSessionIdChars = 64;

    /// <summary>The longest subject.</summary>
    public const int MaxSubjectChars = 256;

    /// <summary>The longest link.</summary>
    public const int MaxLinkChars = 2048;

    /// <summary>The longest actor.</summary>
    public const int MaxActorChars = 256;

    /// <summary>The page size when the caller names none.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>The largest page one query returns.</summary>
    public const int MaxPageSize = 1000;

    public FactoryActivityRecord(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Append one row. <paramref name="callingActor"/> is who the Gateway knows is calling (the session key's
    /// session); it is the actor when the request names none. Throws
    /// <see cref="FactoryActivityValidationException"/> - and writes nothing - when the row is refused.
    /// </summary>
    public FactoryActivityDto Append(AppendFactoryActivityRequest request, string? callingActor)
        => AppendIn(() => _db.CreateContext(), request, callingActor);

    /// <summary>
    /// Append one row for an EXPLICITLY named tenant, never the ambient one - for a Gateway component that
    /// writes on its own account's behalf (the trigger service records every check it decides). Same rules,
    /// same refusals, as the route's <see cref="Append(AppendFactoryActivityRequest, string?)"/>.
    /// </summary>
    public FactoryActivityDto Append(TenantId tenant, AppendFactoryActivityRequest request, string? callingActor)
        => AppendIn(() => _db.CreateContext(tenant), request, callingActor);

    private FactoryActivityDto AppendIn(Func<GatewayDbContext> openContext, AppendFactoryActivityRequest request, string? callingActor)
    {
        FileLog.Write($"[FactoryActivityRecord] Append: factory={request?.Factory}, agent={request?.FactoryAgent}, outcome={request?.Outcome}");
        if (request is null)
            throw new FactoryActivityValidationException("A factory activity body is required.");

        var factory = Required(request.Factory, "factory", MaxIdChars);
        var agent = Required(request.FactoryAgent, "factory agent", MaxIdChars);
        var what = Required(request.What, "what happened", MaxWhatChars);

        var outcome = (request.Outcome ?? "").Trim().ToLowerInvariant();
        if (!FactoryActivityOutcome.All.Contains(outcome, StringComparer.Ordinal))
            throw new FactoryActivityValidationException(
                $"'{request.Outcome}' is not a factory activity outcome. Allowed: " +
                string.Join(", ", FactoryActivityOutcome.All) + ".");

        var version = Optional(request.FactoryAgentVersion, "factory agent version", MaxVersionChars);
        var sessionId = Optional(request.SessionId, "session id", MaxSessionIdChars);
        var subject = Optional(request.Subject, "subject", MaxSubjectChars);
        var link = Optional(request.Link, "link", MaxLinkChars);

        var actor = Optional(request.Actor, "actor", MaxActorChars)
            ?? Optional(callingActor, "actor", MaxActorChars)
            ?? throw new FactoryActivityValidationException(
                "Who acted is not known: name an actor, or call with a session key so the Gateway can stamp the session.");

        lock (_gate)
        {
            using var ctx = openContext();

            if (request.CorrectsId is { } correctsId
                && !ctx.FactoryActivity.AsNoTracking().Any(e => e.Id == correctsId))
                throw new FactoryActivityValidationException(
                    $"There is no factory activity row {correctsId} to correct.");

            var now = DateTime.UtcNow;
            var occurred = request.OccurredUtc.HasValue
                ? DateTime.SpecifyKind(request.OccurredUtc.Value.ToUniversalTime(), DateTimeKind.Utc)
                : now;

            var entity = new FactoryActivityEntity
            {
                TenantId = ctx.ActiveTenant!,
                Factory = factory,
                FactoryAgent = agent,
                FactoryAgentVersion = version,
                SessionId = sessionId,
                What = what,
                Outcome = outcome,
                Subject = subject,
                Link = link,
                Actor = actor,
                CorrectsId = request.CorrectsId,
                OccurredUtc = occurred,
                RecordedUtc = now,
            };
            ctx.FactoryActivity.Add(entity);
            ctx.SaveChanges();

            FileLog.Write($"[FactoryActivityRecord] Append: id={entity.Id}, factory={factory}, agent={agent}, " +
                          $"outcome={outcome}, actor={actor}, corrects={entity.CorrectsId?.ToString() ?? "-"}");
            return ToDto(entity);
        }
    }

    /// <summary>
    /// One page of rows, filtered by factory, factory agent, outcome and a time window on OccurredUtc
    /// (<paramref name="fromUtc"/> inclusive, <paramref name="toUtc"/> exclusive). Newest first unless
    /// <paramref name="oldestFirst"/>.
    /// </summary>
    public FactoryActivityPage Query(
        string? factory = null, string? factoryAgent = null, string? outcome = null,
        DateTime? fromUtc = null, DateTime? toUtc = null, bool oldestFirst = false,
        int offset = 0, int? limit = null)
        => Query(_db.CreateContext, factory, factoryAgent, outcome, fromUtc, toUtc, oldestFirst, offset, limit, sessionIds: null);

    /// <summary>
    /// One page of rows from an account the ROUTE resolved, never the ambient one. Otherwise exactly the other
    /// <c>Query</c>, plus <paramref name="sessionIds"/>: when given, only rows naming one of those sessions - which
    /// is how the roster reads the "started" rows for the sessions it is showing, and no others.
    /// </summary>
    public FactoryActivityPage Query(
        TenantId tenant, string? factory = null, string? factoryAgent = null, string? outcome = null,
        DateTime? fromUtc = null, DateTime? toUtc = null, bool oldestFirst = false,
        int offset = 0, int? limit = null, IReadOnlyCollection<string>? sessionIds = null)
        => Query(() => _db.CreateContext(tenant), factory, factoryAgent, outcome, fromUtc, toUtc, oldestFirst, offset, limit, sessionIds);

    private FactoryActivityPage Query(
        Func<GatewayDbContext> open, string? factory, string? factoryAgent, string? outcome,
        DateTime? fromUtc, DateTime? toUtc, bool oldestFirst, int offset, int? limit,
        IReadOnlyCollection<string>? sessionIds)
    {
        FileLog.Write($"[FactoryActivityRecord] Query: factory={factory}, agent={factoryAgent}, outcome={outcome}, " +
                      $"from={fromUtc:o}, to={toUtc:o}, oldestFirst={oldestFirst}, offset={offset}, limit={limit}, sessions={sessionIds?.Count.ToString() ?? "-"}");

        if (offset < 0)
            throw new FactoryActivityValidationException("offset cannot be negative.");
        var take = limit ?? DefaultPageSize;
        if (take < 1 || take > MaxPageSize)
            throw new FactoryActivityValidationException($"limit must be between 1 and {MaxPageSize}.");

        string? outcomeFilter = null;
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            outcomeFilter = outcome.Trim().ToLowerInvariant();
            if (!FactoryActivityOutcome.All.Contains(outcomeFilter, StringComparer.Ordinal))
                throw new FactoryActivityValidationException(
                    $"'{outcome}' is not a factory activity outcome. Allowed: " +
                    string.Join(", ", FactoryActivityOutcome.All) + ".");
        }

        lock (_gate)
        {
            using var ctx = open();
            IQueryable<FactoryActivityEntity> query = ctx.FactoryActivity.AsNoTracking();

            if (sessionIds is not null)
            {
                var ids = sessionIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct().ToList();
                query = query.Where(e => e.SessionId != null && ids.Contains(e.SessionId));
            }

            if (!string.IsNullOrWhiteSpace(factory))
            {
                var f = factory.Trim();
                query = query.Where(e => e.Factory == f);
            }
            if (!string.IsNullOrWhiteSpace(factoryAgent))
            {
                var a = factoryAgent.Trim();
                query = query.Where(e => e.FactoryAgent == a);
            }
            if (outcomeFilter is not null)
                query = query.Where(e => e.Outcome == outcomeFilter);
            if (fromUtc.HasValue)
            {
                var from = DateTime.SpecifyKind(fromUtc.Value.ToUniversalTime(), DateTimeKind.Utc);
                query = query.Where(e => e.OccurredUtc >= from);
            }
            if (toUtc.HasValue)
            {
                var to = DateTime.SpecifyKind(toUtc.Value.ToUniversalTime(), DateTimeKind.Utc);
                query = query.Where(e => e.OccurredUtc < to);
            }

            query = oldestFirst
                ? query.OrderBy(e => e.OccurredUtc).ThenBy(e => e.RecordedUtc).ThenBy(e => e.Id)
                : query.OrderByDescending(e => e.OccurredUtc).ThenByDescending(e => e.RecordedUtc).ThenByDescending(e => e.Id);

            // One extra row answers "is there more" without a second count query.
            var rows = query.Skip(offset).Take(take + 1).ToList();
            var page = new FactoryActivityPage
            {
                Rows = rows.Take(take).Select(ToDto).ToList(),
                Offset = offset,
                Limit = take,
                HasMore = rows.Count > take,
            };
            FileLog.Write($"[FactoryActivityRecord] Query: returned {page.Rows.Count}, hasMore={page.HasMore}");
            return page;
        }
    }

    private static string Required(string? value, string name, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new FactoryActivityValidationException($"A factory activity row needs {name}.");
        if (trimmed.Length > max)
            throw new FactoryActivityValidationException($"The {name} is too long (limit {max} characters).");
        return trimmed;
    }

    private static string? Optional(string? value, string name, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (trimmed.Length > max)
            throw new FactoryActivityValidationException($"The {name} is too long (limit {max} characters).");
        return trimmed;
    }

    private static FactoryActivityDto ToDto(FactoryActivityEntity e) => new()
    {
        Id = e.Id,
        Factory = e.Factory,
        FactoryAgent = e.FactoryAgent,
        FactoryAgentVersion = e.FactoryAgentVersion,
        SessionId = e.SessionId,
        What = e.What,
        Outcome = e.Outcome,
        Subject = e.Subject,
        Link = e.Link,
        Actor = e.Actor,
        CorrectsId = e.CorrectsId,
        OccurredUtc = DateTime.SpecifyKind(e.OccurredUtc, DateTimeKind.Utc),
        RecordedUtc = DateTime.SpecifyKind(e.RecordedUtc, DateTimeKind.Utc),
    };
}
