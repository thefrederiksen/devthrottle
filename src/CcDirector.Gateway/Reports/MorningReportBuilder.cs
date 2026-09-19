using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Reports;

/// <summary>
/// Assembles ONE account's morning report (issue #2119) from the Gateway's existing tenant-scoped stores.
/// READ-ONLY: it writes nothing and needs no table of its own.
///
/// THE HONESTY RULE, STATED AS CODE. A number is emitted only when its backing store holds data for THIS
/// tenant; otherwise the field is null and never reaches the JSON. The distinction the rule protects is
/// between "no data" and "zero":
///   - a tenant whose event ledger holds NO session rows at all gets NO <c>sessionsRan</c> - the Gateway
///     genuinely does not know how many sessions ran;
///   - a tenant whose ledger holds rows but none inside the window gets <c>sessionsRan: 0</c> - the Gateway
///     looked and the answer is zero.
/// A report that zero-filled the first case would state, in an email, a fact it had never measured. That is
/// the failure this whole slice exists to avoid, and it is also what lets this slice merge and ship BEFORE
/// the repo-state snapshot feed exists: with no repo-state store, the hygiene items are simply absent.
///
/// TENANCY IS EXPLICIT, NEVER AMBIENT. The tenant is resolved by the ROUTE (from the account the caller
/// named) and passed in, and every read goes through <see cref="GatewayDatabase.CreateContext(TenantId)"/>
/// with that exact tenant. There is no AsyncLocal inference here: a service-token request carries no device
/// key, so there is no ambient tenant to accidentally read - and no way for one account's report to contain
/// another account's rows.
/// </summary>
public sealed class MorningReportBuilder
{
    private readonly GatewayDatabase _db;
    private readonly Streaming.PushedSessionStore? _pushedSessions;
    private readonly RepoStateStore? _repoState;
    private readonly TimeSpan _streamStale;
    private readonly Func<DateTime> _utcNow;

    /// <summary>
    /// How far back the waiting-session scan reads the event ledger. A session whose last recorded
    /// transition is older than this is NOT reported as waiting: the Gateway will not claim to know the
    /// current state of something it has not heard about in a month.
    /// </summary>
    public const int WaitingLookbackDays = 30;

    /// <summary>
    /// How recently a wait must have BEGUN for the report to say a session is waiting on you.
    ///
    /// This exists because of what the first real hosted call returned: nine waiting rows aged 69 to 129
    /// hours, for sessions that had been gone for days, shown as raw identifiers because no Director was
    /// connected to name them. Every one was a true statement ABOUT THE LEDGER - the last transition
    /// recorded really was a wait - and every one was a false statement about the owner's morning.
    ///
    /// The ledger only records what it was told. A session that stops without an exit event leaves its
    /// wait open forever, so "waiting since" ages without bound and never resolves. After two days of
    /// silence the honest position is that the Gateway does not know what became of that session, and it
    /// has no business asserting the session is waiting on a person today. So it says nothing about it.
    ///
    /// Deliberately a REPORTING bar, not a data change: the ledger keeps every transition, and
    /// <see cref="WaitingLookbackDays"/> still bounds the scan. This only decides what the email claims.
    /// </summary>
    public static readonly TimeSpan WaitingReportMaxAge = TimeSpan.FromHours(48);

    /// <summary>
    /// The hard ceiling on ledger rows the waiting scan materializes. Reaching it is LOGGED (never silent):
    /// a truncated scan can only under-report waiting sessions, and the log says so, so a short list is
    /// never mistaken for a quiet fleet.
    /// </summary>
    public const int MaxLedgerRowsScanned = 20_000;

    /// <summary>How old a Director's repo-state snapshot may be and still inform a hygiene recommendation. The
    /// Director pushes every six hours, so this is four missed cycles: long enough that one restart or
    /// one offline evening does not blank the section, short enough that the report never recommends
    /// deleting a worktree from a picture of the machine taken last week.</summary>
    public static readonly TimeSpan RepoStateMaxAge = TimeSpan.FromHours(24);

    /// <summary>Assembles one account's report from the stores this Gateway already holds. Read-only.</summary>
    /// <param name="db">The Gateway EF database.</param>
    /// <param name="pushedSessions">The live pushed-session cache, used ONLY to put a friendly name and a
    /// repository path on a waiting row. Null (or a session it has never seen) costs the row nothing but
    /// those two labels - the waiting fact itself comes from the durable ledger.</param>
    /// <param name="streamStale">How old a Director's pushed roster may be and still be believed.</param>
    /// <param name="utcNow">Clock seam for tests.</param>
    public MorningReportBuilder(
        GatewayDatabase db,
        Streaming.PushedSessionStore? pushedSessions = null,
        TimeSpan? streamStale = null,
        Func<DateTime>? utcNow = null,
        RepoStateStore? repoState = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _pushedSessions = pushedSessions;
        _repoState = repoState;
        _streamStale = streamStale ?? TimeSpan.FromMinutes(5);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Build the report for <paramref name="tenant"/> over <paramref name="window"/>.</summary>
    /// <param name="account">The account string the caller named, echoed into the report.</param>
    public MorningReportDto Build(string account, TenantId tenant, MorningReportWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));

        var now = _utcNow();
        using var ctx = _db.CreateContext(tenant);

        var report = new MorningReportDto
        {
            Account = account,
            Window = new MorningReportWindowDto
            {
                StartUtc = window.StartUtc,
                EndUtc = window.EndUtc,
                Date = window.Date,
                Tz = window.Tz,
            },
            Attention = WaitingSessions(ctx, tenant, now),
        };

        // The hygiene rows (issue #2118): stale worktrees and unmerged branches, from the repo-state
        // snapshots this tenant's Directors pushed. THE HONESTY RULE APPLIES HERE TOO, AND IT IS WHY THE
        // ENDPOINT COULD SHIP BEFORE THIS FEED EXISTED: no repo-state store, or no fresh snapshot for this
        // tenant, means NO hygiene rows at all - not empty ones. "We have never been told about your
        // repositories" and "your repositories are tidy" are different statements, and only one of them
        // has been measured.
        report.Attention.AddRange(HygieneItems(tenant, now));

        // NO YESTERDAY-STATS, BY OWNER RULING (2026-09-20, issue #3124): "telling me how much shit I did
        // yesterday is not going to help me today." The daily report answers WHAT NEEDS YOU TODAY; the
        // scoreboard - sessions run, work accepted, spend - is weekly-report material. There is nothing
        // here that counts what happened, and the stats plumbing (freshness bars, ceil rounding) went with
        // it - resurrect it from git history if a weekly personal-stats surface ever needs the pattern.
        FileLog.Write($"[MorningReportBuilder] Build: tenant={tenant.ToLogString()} window={window.StartUtc:o}..{window.EndUtc:o} " +
                      $"attention={report.Attention.Count}");
        return report;
    }

    /// <summary>
    /// The stale-worktree and unmerged-branch rows, or NOTHING when this Gateway holds no fresh
    /// repo-state for the tenant. A stale snapshot is excluded by the store rather than aged into a
    /// recommendation.
    /// </summary>
    private List<MorningAttentionItemDto> HygieneItems(TenantId tenant, DateTime now)
    {
        if (_repoState is null)
            return new List<MorningAttentionItemDto>();

        var repositories = _repoState.ReadFresh(tenant, RepoStateMaxAge, now);
        if (repositories.Count == 0)
        {
            FileLog.Write("[MorningReportBuilder] HygieneItems: no repo-state fresher than " +
                          $"{RepoStateMaxAge.TotalHours:0}h for tenant={tenant.ToLogString()} - the hygiene " +
                          "rows are OMITTED, not emptied");
            return new List<MorningAttentionItemDto>();
        }

        return RepoHygieneFold.Items(repositories, now);
    }

    /// <summary>
    /// The waiting-session rows: every session whose LAST recorded transition is a wait on the human (or on
    /// a permission grant), with the instant it entered that state and how long ago that was.
    ///
    /// The ledger appends only on a REAL transition, so the last event IS the start of the current state -
    /// the waiting-since instant is read, not inferred. A session that has exited, recovered, gone active or
    /// idle has a later event of that kind and so is not here.
    /// </summary>
    private List<MorningAttentionItemDto> WaitingSessions(GatewayDbContext ctx, TenantId tenant, DateTime now)
    {
        var lookback = now - TimeSpan.FromDays(WaitingLookbackDays);

        // Newest first, capped. Grouping in memory after this ordering makes the FIRST row per session its
        // latest event, which is the state it is in now.
        var rows = ctx.GovernanceEvents.AsNoTracking()
            .Where(e => e.SubjectKind == GovernanceEventSubject.Session &&
                        e.SessionId != null &&
                        e.OccurredUtc >= lookback)
            .OrderByDescending(e => e.OccurredUtc)
            .ThenByDescending(e => e.RecordedUtc)
            .Take(MaxLedgerRowsScanned)
            .Select(e => new { e.SessionId, e.State, e.OccurredUtc })
            .ToList();

        if (rows.Count == MaxLedgerRowsScanned)
            FileLog.Write($"[MorningReportBuilder] WaitingSessions: the {MaxLedgerRowsScanned}-row scan cap was reached for " +
                          $"tenant={tenant.ToLogString()}; sessions whose last transition falls outside the scanned rows are " +
                          "NOT reported. The waiting list may be short - it is never padded.");

        var live = LiveSessionsById(tenant);
        var items = new List<MorningAttentionItemDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Counted and logged, never silently dropped: a short waiting list must be explainable.
        var staleWaits = 0;

        foreach (var row in rows)
        {
            var sessionId = row.SessionId!;
            if (!seen.Add(sessionId))
                continue; // an older event for a session whose latest we already ruled on

            if (row.State != GovernanceEventState.WaitingOnHuman &&
                row.State != GovernanceEventState.WaitingOnPermission)
                continue;

            // The silence bar. An open wait older than this is not reported at all - see
            // WaitingReportMaxAge for why a true statement about the ledger is the wrong thing to put
            // in a person's inbox.
            if (now - DateTime.SpecifyKind(row.OccurredUtc, DateTimeKind.Utc) > WaitingReportMaxAge)
            {
                staleWaits++;
                continue;
            }

            // A session the Gateway can currently see is judged on what it can see: one that is HELD
            // (snoozed) was deliberately parked by the owner and is not "waiting on you" this morning, and
            // one that has EXITED is not waiting on anybody. A session the Gateway cannot see is reported
            // from the ledger as-is - that is the whole point of a durable record.
            if (live.TryGetValue(sessionId, out var liveSession))
            {
                if (HoldStates.IsHeld(liveSession.HoldState))
                    continue;
                if (string.Equals(liveSession.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var since = DateTime.SpecifyKind(row.OccurredUtc, DateTimeKind.Utc);
            items.Add(new WaitingSessionAttentionDto
            {
                Session = string.IsNullOrWhiteSpace(liveSession?.Name) ? sessionId : liveSession!.Name!,
                Repo = string.IsNullOrWhiteSpace(liveSession?.RepoPath) ? null : liveSession!.RepoPath,
                WaitingSinceUtc = since,
                AgeHours = Math.Round(Math.Max(0, (now - since).TotalHours), 1),
            });
        }

        if (staleWaits > 0)
            FileLog.Write($"[MorningReportBuilder] WaitingSessions: {staleWaits} open wait(s) older than " +
                          $"{WaitingReportMaxAge.TotalHours:0}h were NOT reported for tenant={tenant.ToLogString()} - " +
                          "the Gateway has not heard about those sessions since, so it does not claim they are waiting.");

        // Longest wait first - the row the owner most needs to see is the one at the top of the email.
        items.Sort((a, b) => ((WaitingSessionAttentionDto)b).AgeHours.CompareTo(((WaitingSessionAttentionDto)a).AgeHours));
        return items;
    }

    /// <summary>
    /// The tenant's live sessions by id, from every Director that has pushed fresh data. Labels only - the
    /// waiting VERDICT never comes from here, so an offline Director costs a row its name, not its place in
    /// the report.
    /// </summary>
    private Dictionary<string, SessionDto> LiveSessionsById(TenantId tenant)
    {
        var map = new Dictionary<string, SessionDto>(StringComparer.Ordinal);
        if (_pushedSessions is null)
            return map;

        foreach (var (_, session) in _pushedSessions.SnapshotFresh(tenant, _streamStale))
        {
            if (!string.IsNullOrEmpty(session.SessionId))
                map[session.SessionId] = session;
        }
        return map;
    }
}
