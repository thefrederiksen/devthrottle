using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// The durable record of what the Wingman said each stop MEANS (the Wingman-on-every-turn mission, slice
/// B), over the <c>turn_verdicts</c> table.
///
/// TENANT-PARTITIONED BY CONSTRUCTION. Every method takes the tenant EXPLICITLY and opens its context with
/// <see cref="GatewayDatabase.CreateContext(TenantId)"/>, which fails loud on an invalid tenant and stamps
/// the global query filter. There is deliberately no method that takes a bare session id: a session id
/// arrives from a Director on the push stream and two accounts can run sessions with the same one, so a
/// read that did not name its tenant would answer from whatever partition happened to be ambient. That is
/// the shape in which one account is served another account's terminal, and this store makes it
/// unwritable rather than merely discouraged.
///
/// ONE SET-BASED SNAPSHOT FOR THE ROSTER FOLD. <see cref="SnapshotLatest"/> answers "every session's
/// latest verdict" in ONE query. The fold that colours the roster runs on the hot path for every session
/// in the account at once, so a query per session would multiply the roster read by the size of the fleet
/// - the cost that does not show up on a developer's two sessions and is ruinous on thirty.
///
/// Threading matches the rest of the data layer: single writer under a lock, a fresh pooled context per
/// operation, and reads that take no lock at all.
/// </summary>
public sealed class TurnVerdictStore
{
    /// <summary>How long a judged stop is kept. Seven days: long enough to answer "why did my fleet look
    /// like that last week" and to grade the judge against a week of real stops, short enough that the
    /// table stays small on an account running a large fleet.</summary>
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);

    /// <summary>The most rows one history read returns, whatever the caller asks for.</summary>
    public const int MaxHistoryCount = 50;

    /// <summary>The default history page when the caller names no count.</summary>
    public const int DefaultHistoryCount = 10;

    /// <summary>One shared instance: constructing fresh options per call defeats the serializer's caching.</summary>
    private static readonly JsonSerializerOptions VerdictJsonOptions = new();

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public TurnVerdictStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Record one judged stop. A verdict that was REFUSED by the contract is stored too, with its reason -
    /// see <see cref="TurnVerdictEntity"/> for why a refusal is a fact worth keeping.
    ///
    /// Judging the same session at the same instant twice would be the same key, so the second write
    /// REPLACES the first rather than throwing. That is not a merge and nothing is combined: two answers
    /// stamped with the same judged-at are the same stop judged twice, and the later one is the one the
    /// product acted on.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid, the session id is blank, or the verdict
    /// carries no judged-at moment.</exception>
    public void Store(TenantId tenant, string sessionId, TurnVerdictDto verdict)
    {
        var sid = RequireSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(verdict);
        if (verdict.JudgedAtUtc == default)
            throw new ArgumentException("A verdict carries the moment the judge answered.", nameof(verdict));
        if (verdict.TurnEndObservedAtUtc == default)
            throw new ArgumentException(
                "A verdict carries the moment the DETECTOR observed the turn end - it is the join key into "
                + "the turn log, and a default value would silently match nothing.", nameof(verdict));

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var judgedAt = Utc(verdict.JudgedAtUtc);
            var existing = ctx.TurnVerdicts
                .FirstOrDefault(v => v.SessionId == sid && v.JudgedAtUtc == judgedAt);

            var json = JsonSerializer.Serialize(verdict, VerdictJsonOptions);
            if (existing is null)
            {
                ctx.TurnVerdicts.Add(new TurnVerdictEntity
                {
                    TenantId = ctx.ActiveTenant!,
                    SessionId = sid,
                    JudgedAtUtc = judgedAt,
                    VerdictId = verdict.VerdictId,
                    TurnEndObservedAtUtc = Utc(verdict.TurnEndObservedAtUtc),
                    ScreenHash = verdict.ScreenHash,
                    Failed = verdict.Failed,
                    FailureReason = verdict.FailureReason,
                    VerdictJson = json,
                });
            }
            else
            {
                // The answered moment belongs to a verdict id. A same-moment re-judgement that mints a new id is a
                // new verdict nobody has answered; one that keeps the id keeps its answer.
                if (!string.Equals(existing.VerdictId, verdict.VerdictId, StringComparison.Ordinal))
                    existing.AnsweredAtUtc = null;
                existing.VerdictId = verdict.VerdictId;
                existing.TurnEndObservedAtUtc = Utc(verdict.TurnEndObservedAtUtc);
                existing.ScreenHash = verdict.ScreenHash;
                existing.Failed = verdict.Failed;
                existing.FailureReason = verdict.FailureReason;
                existing.VerdictJson = json;
            }

            ctx.SaveChanges();
        }
    }

    /// <summary>
    /// This session's most recent judged stop in this tenant, or null when it has never been judged (or
    /// its verdicts have aged out, or <see cref="Invalidate"/> cleared them). Null is the "no verdict"
    /// state the roster fold reads as "leave this row exactly as the detector left it".
    /// </summary>
    public TurnVerdictDto? Latest(TenantId tenant, string sessionId)
    {
        var sid = RequireSessionId(sessionId);
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.TurnVerdicts.AsNoTracking()
            .Where(v => v.SessionId == sid)
            .OrderByDescending(v => v.JudgedAtUtc)
            .FirstOrDefault();
        return row is null ? null : Deserialize(row);
    }

    /// <summary>
    /// Find one verdict by its own id, in this tenant, and say WHICH SESSION it belongs to. Null when this tenant
    /// holds no verdict with that id.
    ///
    /// The session is returned rather than taken as a filter on purpose. The answer route has to join the verdict
    /// to the session in its path, and that join is only testable - and only removable in a revert proof - if
    /// the lookup does not already perform it. The tenant partition is not optional in the same way: the context
    /// is tenant-scoped, so another account's verdict is never found at all.
    /// </summary>
    public TurnVerdictLocated? FindById(TenantId tenant, string verdictId)
    {
        if (string.IsNullOrWhiteSpace(verdictId))
            throw new ArgumentException("A verdict id is required.", nameof(verdictId));
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.TurnVerdicts.AsNoTracking()
            .Where(v => v.VerdictId == verdictId)
            .OrderByDescending(v => v.JudgedAtUtc)
            .FirstOrDefault();
        if (row is null) return null;
        var dto = Deserialize(row);
        return dto is null ? null : new TurnVerdictLocated(row.SessionId, dto, row.AnsweredAtUtc);
    }

    /// <summary>
    /// Record that the owner's answer to this verdict was written and confirmed. True when this call marked it;
    /// false when this tenant holds no such verdict or it was already answered - the first mark stands and is
    /// never moved.
    ///
    /// A stored fact rather than a flag in memory, so "this verdict was answered" is answerable by query and
    /// holds across a Gateway restart, and so the answer route's check reads the same record it marks.
    /// </summary>
    public bool MarkAnswered(TenantId tenant, string verdictId, DateTime answeredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(verdictId))
            throw new ArgumentException("A verdict id is required.", nameof(verdictId));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.TurnVerdicts
                .Where(v => v.VerdictId == verdictId)
                .OrderByDescending(v => v.JudgedAtUtc)
                .FirstOrDefault();
            if (row is null || row.AnsweredAtUtc is not null) return false;
            row.AnsweredAtUtc = Utc(answeredAtUtc);
            ctx.SaveChanges();
            return true;
        }
    }

    /// <summary>
    /// This session's judged stops, newest first, capped at <see cref="MaxHistoryCount"/>. The history is
    /// what makes a wrong verdict answerable afterwards - "what did it say about this session all
    /// morning" is not a question one row can answer.
    /// </summary>
    public IReadOnlyList<TurnVerdictDto> History(TenantId tenant, string sessionId, int count = DefaultHistoryCount)
    {
        var sid = RequireSessionId(sessionId);
        var take = count <= 0 ? DefaultHistoryCount : Math.Min(count, MaxHistoryCount);
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.TurnVerdicts.AsNoTracking()
            .Where(v => v.SessionId == sid)
            .OrderByDescending(v => v.JudgedAtUtc)
            .Take(take)
            .ToList();
        var list = new List<TurnVerdictDto>(rows.Count);
        foreach (var row in rows)
        {
            var dto = Deserialize(row);
            if (dto is not null) list.Add(dto);
        }
        return list;
    }

    /// <summary>
    /// EVERY session's latest verdict in this tenant, keyed by session id - ONE query, for the roster fold.
    ///
    /// The fold runs over the whole account at once. Asking per session would issue one round trip per
    /// session on the hot path, which is invisible on a small fleet and is the whole cost of the read on a
    /// large one. See <see cref="SnapshotLatestCore"/> for the query and why it is one.
    /// </summary>
    public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        var rows = SnapshotLatestCore(ctx);
        var map = new Dictionary<string, TurnVerdictDto>(rows.Count, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var dto = Deserialize(row);
            if (dto is not null) map[row.SessionId] = dto;
        }
        return map;
    }

    /// <summary>
    /// The snapshot query itself, as ONE statement: every row whose judged-at equals the newest judged-at
    /// for its own session. The correlated maximum is a single SQL statement on both providers, which a
    /// GroupBy-and-take-first would not reliably be, and the key's uniqueness means a session can never
    /// have two rows at its own maximum - so "one row per session" is a property of the key rather than a
    /// hope about the ordering.
    ///
    /// Both the outer query and the correlated one read through the tenant query filter, so the whole
    /// statement is confined to <see cref="GatewayDbContext.ActiveTenant"/>.
    ///
    /// Internal, and reached from the tests through InternalsVisibleTo, so the "one query" claim can be
    /// COUNTED against a context carrying a command interceptor rather than asserted in a comment.
    /// </summary>
    internal static List<TurnVerdictEntity> SnapshotLatestCore(GatewayDbContext ctx)
        => ctx.TurnVerdicts.AsNoTracking()
            .Where(v => v.JudgedAtUtc == ctx.TurnVerdicts
                .Where(x => x.SessionId == v.SessionId)
                .Max(x => x.JudgedAtUtc))
            .ToList();

    /// <summary>
    /// Forget everything this tenant holds about this session, so its verdict state is NONE again and the
    /// roster fold leaves the row exactly as the detector left it.
    ///
    /// Deleting rather than marking is the honest shape: a verdict is a statement about a screen, and the
    /// reason to invalidate is that the screen the statement was about is gone. Keeping a superseded row
    /// and remembering not to read it is a second rule to get wrong. Returns how many rows went, so a
    /// caller can log the fact rather than assume it.
    /// </summary>
    public int Invalidate(TenantId tenant, string sessionId)
    {
        var sid = RequireSessionId(sessionId);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.TurnVerdicts.Where(v => v.SessionId == sid).ToList();
            if (rows.Count == 0) return 0;
            ctx.TurnVerdicts.RemoveRange(rows);
            ctx.SaveChanges();
            FileLog.Write(
                $"[TurnVerdictStore] Invalidate: sid={sid} tenant={tenant.ToLogString()} removed={rows.Count}");
            return rows.Count;
        }
    }

    /// <summary>
    /// Remove this tenant's judged stops older than <paramref name="cutoffUtc"/>. Called once per tenant by
    /// <see cref="TurnVerdictRetentionSweep"/>. Corrections go with them: a correction about a stop that is
    /// no longer held is a label with nothing to label.
    /// </summary>
    public int PurgeOlderThan(TenantId tenant, DateTime cutoffUtc)
    {
        var cutoff = Utc(cutoffUtc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var stale = ctx.TurnVerdicts.Where(v => v.JudgedAtUtc < cutoff).ToList();
            var staleFeedback = ctx.TurnVerdictFeedback.Where(f => f.ReportedAtUtc < cutoff).ToList();
            if (stale.Count == 0 && staleFeedback.Count == 0) return 0;
            ctx.TurnVerdicts.RemoveRange(stale);
            ctx.TurnVerdictFeedback.RemoveRange(staleFeedback);
            ctx.SaveChanges();
            return stale.Count + staleFeedback.Count;
        }
    }

    /// <summary>
    /// Read one stored answer back. A row whose JSON cannot be read answers NULL rather than throwing: a
    /// verdict written by a newer Gateway, or a row a rollback left behind, must not take the roster fold
    /// down with it - the caller's "no verdict" path is the safe reading, because it leaves the row exactly
    /// as the detector left it, which is red.
    ///
    /// The unreadable row is LOGGED, not swallowed. A silent null here would be indistinguishable from a
    /// session that was never judged, and those are different facts.
    /// </summary>
    private static TurnVerdictDto? Deserialize(TurnVerdictEntity row)
    {
        try
        {
            return JsonSerializer.Deserialize<TurnVerdictDto>(row.VerdictJson, VerdictJsonOptions);
        }
        catch (JsonException ex)
        {
            // A KNOWN GAP, RULED ACCEPTABLE AND NOT A STATE TO DESIGN FOR. A corrupt row is a defect to fix
            // at its source, never a case for the readers to learn to handle - so nothing downstream gets a
            // "corrupt" state, and no test manufactures one. The absence it becomes reads as red, which is
            // the safe direction this whole design leans: a stop nobody could read stays as needing a person.
            // What makes it findable is this line, which names the row's full key - account, session and
            // judged moment - so the one bad row can be looked up rather than inferred.
            FileLog.Write(
                $"[TurnVerdictStore] a stored verdict could not be read: tenant={row.TenantId} sid={row.SessionId} "
                + $"judged={row.JudgedAtUtc:O}: {ex.Message}");
            return null;
        }
    }

    private static string RequireSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        return sessionId;
    }

    /// <summary>The model's UTC converter normalises on write, but the COMPARISON in a query is built from
    /// the value handed in, so a local-kind moment would compare against a different instant than it is
    /// stored as. Normalise here, where the value enters.</summary>
    private static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
