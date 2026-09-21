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

    /// <summary>The most corrections one administrator read returns, whatever the caller asks for. A day of one
    /// person's corrections is a handful; a cap that cannot be raised from outside is what stops the read
    /// becoming a way to walk a whole account's record in one request.</summary>
    public const int MaxFeedbackPage = 500;

    /// <summary>One shared instance: constructing fresh options per call defeats the serializer's caching.</summary>
    private static readonly JsonSerializerOptions VerdictJsonOptions = new();

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly Func<DateTime> _utcNow;

    /// <param name="utcNow">The clock the held snapshot's age is measured on; the system clock when omitted.</param>
    public TurnVerdictStore(GatewayDatabase db, Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
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
                // new verdict nobody has answered; one that keeps the id keeps its answer. The SUPERSEDE stamp
                // goes the same way and for the same reason: a new id is a new statement about the screen, so it
                // is born describing it, while a re-store under the same id is the same statement and keeps
                // whatever has happened to it since.
                if (!string.Equals(existing.VerdictId, verdict.VerdictId, StringComparison.Ordinal))
                {
                    existing.AnsweredAtUtc = null;
                    existing.AnswerJson = null;
                    existing.SupersededAtUtc = null;
                }
                existing.VerdictId = verdict.VerdictId;
                existing.TurnEndObservedAtUtc = Utc(verdict.TurnEndObservedAtUtc);
                existing.ScreenHash = verdict.ScreenHash;
                existing.Failed = verdict.Failed;
                existing.FailureReason = verdict.FailureReason;
                existing.VerdictJson = json;
            }

            ctx.SaveChanges();
            SnapshotChanged(tenant);
        }
    }

    /// <summary>
    /// This session's most recent judged stop in this tenant that still describes the screen, or null when it
    /// has never been judged (or its verdicts have aged out, or <see cref="Invalidate"/> superseded them). Null
    /// is the "no verdict" state the roster fold reads as "leave this row exactly as the detector left it".
    ///
    /// A SUPERSEDED RECORD IS NOT THE LATEST ANYTHING. It is kept so the stop can still be examined and reported
    /// wrong (slice G), and this read answers "what is true now", so it never returns one. That is what makes the
    /// change from deleting to stamping invisible to every caller asking this question - the roster fold, the
    /// carrying-on clock, and the answer route's own superseded check all read through here.
    /// </summary>
    public TurnVerdictDto? Latest(TenantId tenant, string sessionId)
    {
        var sid = RequireSessionId(sessionId);
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.TurnVerdicts.AsNoTracking()
            .Where(v => v.SessionId == sid && v.SupersededAtUtc == null)
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
    ///
    /// IT FINDS A SUPERSEDED VERDICT TOO, and that is the whole point of keeping one (slice G). The owner reports
    /// a verdict wrong AFTER he has answered it, and answering it is what put the session back to work and
    /// superseded it - a lookup that skipped superseded rows would make the report impossible in exactly the case
    /// it exists for. Nothing is weakened by that: the answer route refuses on its own <see cref="Latest"/> check,
    /// which a superseded verdict can never satisfy, so "findable" and "answerable" stay two different questions.
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
        return dto is null ? null : new TurnVerdictLocated(row.SessionId, dto, row.AnsweredAtUtc, ReadAnswer(row));
    }

    /// <summary>
    /// This session's most recently judged stop in this tenant, SUPERSEDED OR NOT, or null when it has none. Where
    /// <see cref="Latest"/> answers "what does the screen show now", this answers "what is the last stop this session
    /// made" - an answered verdict is superseded the moment its session goes back to work, and it is still the last
    /// stop until the session stops again. The walkthrough asks it so that an answer to an earlier stop never closes a
    /// record once a later stop exists.
    /// </summary>
    public TurnVerdictDto? NewestJudged(TenantId tenant, string sessionId)
    {
        var sid = RequireSessionId(sessionId);
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.TurnVerdicts.AsNoTracking()
            .Where(v => v.SessionId == sid)
            .OrderByDescending(v => v.JudgedAtUtc)
            .FirstOrDefault();
        return row is null ? null : Deserialize(row);
    }

    private static TurnVerdictStoredAnswer? ReadAnswer(TurnVerdictEntity row)
        => row.AnswerJson is null ? null : JsonSerializer.Deserialize<TurnVerdictStoredAnswer>(row.AnswerJson, VerdictJsonOptions)
           ?? throw new InvalidOperationException($"verdict {row.VerdictId} carries an answer that does not read back");

    /// <summary>
    /// Record that the owner's answer to this verdict was written and confirmed, and WHAT it was. True when this call
    /// marked it; false when this tenant holds no such verdict, the verdict stored under that id is for another turn
    /// end than the answer names, or it was already answered - the first mark stands and is never moved.
    ///
    /// A stored fact rather than a flag in memory, so "this verdict was answered, and with what" is answerable by
    /// query and holds across a Gateway restart, and so the answer route's check reads the same record it marks. The
    /// moment and the answer are written in one save: there is never an answered verdict without its answer.
    /// </summary>
    public bool MarkAnswered(TenantId tenant, TurnVerdictStoredAnswer answer, DateTime answeredAtUtc)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (string.IsNullOrWhiteSpace(answer.VerdictId))
            throw new ArgumentException("A verdict id is required.", nameof(answer));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.TurnVerdicts
                .Where(v => v.VerdictId == answer.VerdictId)
                .OrderByDescending(v => v.JudgedAtUtc)
                .FirstOrDefault();
            if (row is null || row.AnsweredAtUtc is not null) return false;
            if (row.TurnEndObservedAtUtc != Utc(answer.TurnEndObservedAtUtc))
            {
                FileLog.Write($"[TurnVerdictStore] MarkAnswered: verdict={answer.VerdictId} is stored for turn end " +
                              $"{row.TurnEndObservedAtUtc:O}, not {answer.TurnEndObservedAtUtc:O}; not marked");
                return false;
            }
            row.AnsweredAtUtc = Utc(answeredAtUtc);
            row.AnswerJson = JsonSerializer.Serialize(answer with { TurnEndObservedAtUtc = row.TurnEndObservedAtUtc }, VerdictJsonOptions);
            ctx.SaveChanges();
            // The snapshot carries no answer, so nothing it holds changed. Discarded anyway: every write through
            // this store discards it, which is a rule that needs no reasoning about which column a write touched.
            SnapshotChanged(tenant);
            return true;
        }
    }

    /// <summary>
    /// This session's judged stops, newest first, capped at <see cref="MaxHistoryCount"/>. The history is
    /// what makes a wrong verdict answerable afterwards - "what did it say about this session all
    /// morning" is not a question one row can answer.
    ///
    /// SUPERSEDED RECORDS ARE RETURNED, with <see cref="TurnVerdictDto.SupersededAtUtc"/> stamped from the row, so
    /// a reader can tell one from a record still in force. This is the read slice G exists to make possible: a
    /// verdict the owner answers is superseded in the same breath, and before this the whole row was deleted,
    /// which left nothing here to look at.
    /// </summary>
    public IReadOnlyList<TurnVerdictDto> History(TenantId tenant, string sessionId, int count = DefaultHistoryCount)
    {
        var rows = HistoryWithAnswers(tenant, sessionId, count);
        var list = new List<TurnVerdictDto>(rows.Count);
        foreach (var row in rows) list.Add(row.Verdict);
        return list;
    }

    /// <summary>
    /// The same history, each stop carrying the moment the owner's answer to it was CONFIRMED (the Wingman tab,
    /// version 3, item 1) and the answer the route stored - both null while it is unanswered.
    ///
    /// <see cref="History"/> is this read with the answer dropped, rather than a second query, so there is exactly
    /// one definition of what a session's history is and what order it comes back in.
    ///
    /// THE ANSWER IS THE ONE THE ANSWER ROUTE WROTE, read back from the row's own column by the same
    /// <see cref="ReadAnswer"/> the single-verdict lookup uses. There is one record of what the owner answered and
    /// this read does not make a second: a view that kept its own copy would be free to disagree with the
    /// walkthrough about what he decided.
    ///
    /// THE MOMENT COMES FROM THE ROW'S COLUMN, for the same reason the supersede stamp does: it is written long
    /// after the judge answered, so the serialised verdict cannot carry it. It is handed back BESIDE the verdict
    /// rather than stamped onto it, because a field on the verdict would read null on every other route that serves
    /// one - and "this route does not stamp it" is indistinguishable from "nobody has answered this".
    /// </summary>
    public IReadOnlyList<AnsweredTurnVerdict> HistoryWithAnswers(
        TenantId tenant, string sessionId, int count = DefaultHistoryCount)
    {
        var sid = RequireSessionId(sessionId);
        var take = count <= 0 ? DefaultHistoryCount : Math.Min(count, MaxHistoryCount);
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.TurnVerdicts.AsNoTracking()
            .Where(v => v.SessionId == sid)
            .OrderByDescending(v => v.JudgedAtUtc)
            .Take(take)
            .ToList();
        var list = new List<AnsweredTurnVerdict>(rows.Count);
        foreach (var row in rows)
        {
            var dto = Deserialize(row);
            if (dto is null) continue;
            // From the COLUMN, never from the saved answer. The supersede moment is a fact about the record that
            // was written long after the judge answered, so the serialized answer cannot carry it and a reader
            // that trusted the JSON would see null on every superseded row.
            dto.SupersededAtUtc = row.SupersededAtUtc;
            list.Add(new AnsweredTurnVerdict(dto, row.AnsweredAtUtc, ReadAnswer(row)));
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
        if (!tenant.IsValid) throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        var now = _utcNow();

        // THE ROWS ARE CACHED, THE OBJECTS ARE NOT. The fold asks this on every roster poll, every display sweep
        // and every accepted Director push - several times a second across a fleet - and each answer carries every
        // session's latest verdict JSON (devthrottle_internal#2199). The rows only change when this store writes them, so
        // the answer is kept per account until the next write, and each caller still gets freshly deserialised
        // objects it is free to change.
        var version = _snapshotVersions.GetOrAdd(tenant.Value, 0L);
        List<TurnVerdictEntity> rows;
        if (_snapshots.TryGetValue(tenant.Value, out var held)
            && held.Version == version
            && now - held.ReadAtUtc < SnapshotMaxAge)
        {
            rows = held.Rows;
        }
        else
        {
            using var ctx = _db.CreateContext(tenant);
            rows = SnapshotLatestCore(ctx);
            // Stored under the version read BEFORE the query. A write that lands while the query runs bumps the
            // version, so this entry is already stale and the next read asks the database again rather than
            // serving what the write replaced.
            _snapshots[tenant.Value] = new HeldSnapshot(version, now, rows);
        }

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
    /// SUPERSEDED ROWS ARE EXCLUDED FROM BOTH HALVES, not only the outer one. Filtering just the outer query
    /// would leave the correlated maximum reading superseded rows, so a session whose newest record had been
    /// superseded would match nothing at all - which happens to read as "never judged" and is right by accident,
    /// right up until a live record exists underneath it and is hidden by a maximum it is not allowed to be. Both
    /// halves ask the same question: the newest record that still describes the screen.
    ///
    /// Internal, and reached from the tests through InternalsVisibleTo, so the "one query" claim can be
    /// COUNTED against a context carrying a command interceptor rather than asserted in a comment.
    ///
    /// ONLY THE COLUMNS THE FOLD READS. The verdict is rebuilt from <c>VerdictJson</c> alone; the session and the
    /// judged moment key it and name a row that cannot be read. The owner's answer (<c>AnswerJson</c>) and the
    /// other columns are never used by this read and are not carried off the database.
    /// </summary>
    internal static List<TurnVerdictEntity> SnapshotLatestCore(GatewayDbContext ctx)
        => ctx.TurnVerdicts.AsNoTracking()
            .Where(v => v.SupersededAtUtc == null)
            .Where(v => v.JudgedAtUtc == ctx.TurnVerdicts
                .Where(x => x.SessionId == v.SessionId && x.SupersededAtUtc == null)
                .Max(x => x.JudgedAtUtc))
            .Select(v => new TurnVerdictEntity
            {
                TenantId = v.TenantId,
                SessionId = v.SessionId,
                JudgedAtUtc = v.JudgedAtUtc,
                VerdictJson = v.VerdictJson,
            })
            .ToList();

    /// <summary>
    /// The longest a held snapshot is served without asking the database. Every write through this store
    /// discards the held snapshot at once, so within one Gateway process this bound never decides anything. It
    /// exists for the one writer this process cannot hear: another Gateway process on the same database, which
    /// happens for a few seconds while a deploy replaces the container. Thirty seconds is inside the time a
    /// person takes to notice a row's colour, and it still turns a read several times a second into two a
    /// minute.
    /// </summary>
    internal static readonly TimeSpan SnapshotMaxAge = TimeSpan.FromSeconds(30);

    private sealed record HeldSnapshot(long Version, DateTime ReadAtUtc, List<TurnVerdictEntity> Rows);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _snapshotVersions = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HeldSnapshot> _snapshots = new(StringComparer.Ordinal);

    /// <summary>Called after every committed write in this tenant: the held snapshot no longer describes it.</summary>
    private void SnapshotChanged(TenantId tenant)
        => _snapshotVersions.AddOrUpdate(tenant.Value, 1L, (_, v) => v + 1);

    /// <summary>
    /// This session's stored verdicts no longer describe its screen, so its verdict state is NONE again and the
    /// roster fold leaves the row exactly as the detector left it. Each row is STAMPED with
    /// <paramref name="supersededAtUtc"/>; none is deleted. Returns how many rows were stamped by this call.
    ///
    /// IT USED TO DELETE, AND THAT IS THE DEFECT SLICE G FIXES. A verdict is a statement about a screen and the
    /// reason to invalidate it is that the screen is gone - but the moment the owner ANSWERS a red row, the
    /// session goes back to work and this is called, so the verdict that made the row red was destroyed by the
    /// act of answering it. On the first live evening the owner watched his own Architect row go red with the
    /// Wingman's label and found nothing there by the time he looked: the record he would have examined,
    /// reported wrong, or graded against what he actually did had been deleted in the same breath.
    ///
    /// Stamping costs exactly one rule, and it is a rule the readers already wanted: a read that answers "what is
    /// true NOW" ignores a stamped row (<see cref="Latest"/>, <see cref="SnapshotLatest"/>), and
    /// <see cref="History"/> returns it. Retention is untouched - <see cref="PurgeOlderThan"/> still cuts on the
    /// judged moment, so a superseded row ages out on the same seven-day clock as any other and nothing
    /// accumulates beyond the week the store already keeps.
    ///
    /// ALREADY-STAMPED ROWS ARE LEFT ALONE. The first moment is the one that is true: the screen went away when
    /// it went away, and a session that works, stops and works again must not have its older records re-dated to
    /// the newest interruption.
    /// </summary>
    public int Invalidate(TenantId tenant, string sessionId, DateTime? supersededAtUtc = null)
    {
        var sid = RequireSessionId(sessionId);
        var stampedAt = Utc(supersededAtUtc ?? DateTime.UtcNow);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.TurnVerdicts.Where(v => v.SessionId == sid && v.SupersededAtUtc == null).ToList();
            if (rows.Count == 0) return 0;
            foreach (var row in rows) row.SupersededAtUtc = stampedAt;
            ctx.SaveChanges();
            SnapshotChanged(tenant);
            FileLog.Write(
                $"[TurnVerdictStore] Invalidate: sid={sid} tenant={tenant.ToLogString()} superseded={rows.Count}");
            return rows.Count;
        }
    }

    /// <summary>
    /// Record that a verdict was WRONG, in the words of the person it was about (the Wingman-on-every-turn
    /// mission, slice G). A second report about the same verdict REPLACES the first - see
    /// <see cref="TurnVerdictFeedbackEntity"/> for why one person's second opinion about one stop is not two
    /// facts.
    ///
    /// It writes what it is given and checks nothing: whether the verdict exists, belongs to the session being
    /// answered, and carries a word from the vocabulary are the route's rules, and they are tested where they are
    /// made. This is the durable write underneath them.
    /// </summary>
    public void RecordFeedback(
        TenantId tenant,
        string verdictId,
        string sessionId,
        DateTime turnEndObservedAtUtc,
        string correctedVerdict,
        string? note,
        DateTime reportedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(verdictId))
            throw new ArgumentException("A verdict id is required.", nameof(verdictId));
        var sid = RequireSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(correctedVerdict))
            throw new ArgumentException("A corrected verdict word is required.", nameof(correctedVerdict));

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var existing = ctx.TurnVerdictFeedback.FirstOrDefault(f => f.VerdictId == verdictId);
            if (existing is null)
            {
                ctx.TurnVerdictFeedback.Add(new TurnVerdictFeedbackEntity
                {
                    TenantId = ctx.ActiveTenant!,
                    VerdictId = verdictId,
                    SessionId = sid,
                    TurnEndObservedAtUtc = Utc(turnEndObservedAtUtc),
                    ReportedAtUtc = Utc(reportedAtUtc),
                    CorrectedVerdict = correctedVerdict,
                    Note = note,
                });
            }
            else
            {
                existing.SessionId = sid;
                existing.TurnEndObservedAtUtc = Utc(turnEndObservedAtUtc);
                existing.ReportedAtUtc = Utc(reportedAtUtc);
                existing.CorrectedVerdict = correctedVerdict;
                existing.Note = note;
            }

            ctx.SaveChanges();
            FileLog.Write(
                $"[TurnVerdictStore] RecordFeedback: sid={sid} tenant={tenant.ToLogString()} verdict={verdictId} "
                + $"corrected={correctedVerdict} replaced={existing is not null}");
        }
    }

    /// <summary>
    /// The correction held against this verdict in this tenant, or null when nobody has reported it wrong. For
    /// the route's own "is this a replacement" answer and for the tests.
    /// </summary>
    public TurnVerdictFeedbackEntity? FeedbackFor(TenantId tenant, string verdictId)
    {
        if (string.IsNullOrWhiteSpace(verdictId))
            throw new ArgumentException("A verdict id is required.", nameof(verdictId));
        using var ctx = _db.CreateContext(tenant);
        return ctx.TurnVerdictFeedback.AsNoTracking().FirstOrDefault(f => f.VerdictId == verdictId);
    }

    /// <summary>
    /// Every correction this tenant holds that was reported at or after <paramref name="sinceUtc"/>, oldest
    /// first, at most <paramref name="take"/> of them. The administrator read serves the labelled corpus from
    /// this: the daily pull asks each day for what is new and joins each row into the turn log by
    /// (account, session, observed moment).
    ///
    /// THE ORDER IS (reported moment, verdict id) AND NOT THE MOMENT ALONE, because the moment alone is not
    /// unique - two corrections can carry the same instant - and a page boundary that falls inside a group of
    /// equal moments would either repeat rows or skip them. The identifier is byte-ordinal on both providers
    /// (the Postgres column carries the C collation), so the two databases page in the same order.
    ///
    /// <paramref name="afterReportedAtUtc"/> with <paramref name="afterVerdictId"/> CONTINUES a page: only rows
    /// strictly after that pair in the same order are returned. That is the whole reason a cursor exists rather
    /// than the caller moving <paramref name="sinceUtc"/> forward - a moment cannot say "and the rest of the
    /// rows stamped with it".
    ///
    /// The caller may ask for one row MORE than the page it means to serve, to find out whether another row
    /// exists without serving it; the ceiling here allows exactly that one probe row and no more.
    /// </summary>
    public IReadOnlyList<TurnVerdictFeedbackEntity> FeedbackSince(
        TenantId tenant,
        DateTime sinceUtc,
        int take,
        DateTime? afterReportedAtUtc = null,
        string? afterVerdictId = null)
    {
        var cutoff = Utc(sinceUtc);
        var rows = take <= 0 ? MaxFeedbackPage : Math.Min(take, MaxFeedbackPage + 1);
        using var ctx = _db.CreateContext(tenant);
        var query = ctx.TurnVerdictFeedback.AsNoTracking().Where(f => f.ReportedAtUtc >= cutoff);

        if (afterReportedAtUtc is { } afterAt && !string.IsNullOrEmpty(afterVerdictId))
        {
            var at = Utc(afterAt);
            var id = afterVerdictId;
            query = query.Where(f => f.ReportedAtUtc > at
                || (f.ReportedAtUtc == at && string.Compare(f.VerdictId, id) > 0));
        }

        return query
            .OrderBy(f => f.ReportedAtUtc)
            .ThenBy(f => f.VerdictId)
            .Take(rows)
            .ToList();
    }

    /// <summary>
    /// Remove this tenant's judged stops older than <paramref name="cutoffUtc"/>, and every correction whose
    /// verdict is no longer held. Called once per tenant by <see cref="TurnVerdictRetentionSweep"/>.
    ///
    /// THE CORRECTION GOES WITH ITS VERDICT, and that is a join rather than a second clock. It used to be a
    /// second clock - corrections were cut on their OWN reported moment - and the two clocks do not agree: a
    /// correction is always reported after the stop it is about, so a verdict judged seven days and one minute
    /// ago is purged while a correction made about it this morning stays, pointing at a row that is gone. The
    /// inspection found the comment above claiming otherwise while the code did that. Now the verdicts are cut
    /// on the judged moment and the corrections follow the rows, so a correction can only outlive its verdict
    /// by the length of one sweep.
    ///
    /// Two writes rather than one: the orphan question is asked of the DATABASE after the stale verdicts are
    /// really gone, so a row deleted in this same sweep counts as gone rather than as still present.
    /// </summary>
    public int PurgeOlderThan(TenantId tenant, DateTime cutoffUtc)
    {
        var cutoff = Utc(cutoffUtc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var stale = ctx.TurnVerdicts.Where(v => v.JudgedAtUtc < cutoff).ToList();
            if (stale.Count > 0)
            {
                ctx.TurnVerdicts.RemoveRange(stale);
                ctx.SaveChanges();
                SnapshotChanged(tenant);
            }

            var orphaned = ctx.TurnVerdictFeedback
                .Where(f => !ctx.TurnVerdicts.Any(v => v.VerdictId == f.VerdictId))
                .ToList();
            if (orphaned.Count > 0)
            {
                ctx.TurnVerdictFeedback.RemoveRange(orphaned);
                ctx.SaveChanges();
            }

            return stale.Count + orphaned.Count;
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
