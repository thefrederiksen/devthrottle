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
    private readonly Func<TenantId, IReadOnlyCollection<DirectorDto>>? _directors;
    private readonly Func<string?>? _newestRelease;
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
    /// <param name="directors">One tenant's Directors, for the out-of-date row. Null means no such row.</param>
    /// <param name="newestRelease">The newest published release as this Gateway last read it, or null while
    /// it has not been read. Null - the function or its answer - means nobody is called behind.</param>
    public MorningReportBuilder(
        GatewayDatabase db,
        Streaming.PushedSessionStore? pushedSessions = null,
        TimeSpan? streamStale = null,
        Func<DateTime>? utcNow = null,
        RepoStateStore? repoState = null,
        Func<TenantId, IReadOnlyCollection<DirectorDto>>? directors = null,
        Func<string?>? newestRelease = null)
    {
        _directors = directors;
        _newestRelease = newestRelease;
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
            WorkedInWindow = WorkedInWindow(ctx, window),
        };
        // Set as a side effect of WaitingSessions above, so it is read after that call, not before.
        report.LostContactCount = LostContact > 0 ? LostContact : null;

        // The hygiene rows (issue #2118): stale worktrees and unmerged branches, from the repo-state
        // snapshots this tenant's Directors pushed. THE HONESTY RULE APPLIES HERE TOO, AND IT IS WHY THE
        // ENDPOINT COULD SHIP BEFORE THIS FEED EXISTED: no repo-state store, or no fresh snapshot for this
        // tenant, means NO hygiene rows at all - not empty ones. "We have never been told about your
        // repositories" and "your repositories are tidy" are different statements, and only one of them
        // has been measured.
        report.Attention.AddRange(HygieneItems(tenant, now));

        if (LongRunningSessions(ctx, tenant, now) is { } longRunning)
            report.Attention.Add(longRunning);

        if (UsageLimitStops(ctx, tenant, now) is { } limited)
            report.Attention.Add(limited);

        if (OutdatedDirectors(tenant, now) is { } outdated)
            report.Attention.Add(outdated);

        // NO YESTERDAY-STATS, BY OWNER RULING (2026-09-20, issue #3124): "telling me how much shit I did
        // yesterday is not going to help me today." The daily report answers WHAT NEEDS YOU TODAY; the
        // scoreboard - sessions run, work accepted, spend - is weekly-report material. There is nothing
        // here that counts what happened, and the stats plumbing (freshness bars, ceil rounding) went with
        // it - resurrect it from git history if a weekly personal-stats surface ever needs the pattern.
        FileLog.Write($"[MorningReportBuilder] Build: tenant={tenant.ToLogString()} window={window.StartUtc:o}..{window.EndUtc:o} " +
                      $"attention={report.Attention.Count}");
        return report;
    }

    /// <summary>How recently a Director must have been heard from to be named in the out-of-date row. The
    /// email goes out at seven in the morning, when a laptop is often still shut, so "connected right now"
    /// would hide exactly the machines it is for; a day covers last night's work and nothing older.</summary>
    public static readonly TimeSpan DirectorSeenWithin = TimeSpan.FromHours(24);

    /// <summary>
    /// The out-of-date Directors row (#3124), or NULL. The comparison is the Cockpit fleet view's own
    /// (<see cref="Api.FleetMachinesFold.CompareToNewest"/>), so the email and the Machines page can never
    /// disagree about what "behind" means.
    ///
    /// THE HONESTY RULE: no registry, no release watch, or a newest release that has not been read yet
    /// means NO row - "we do not know the newest release" is not "you are up to date", and it is certainly
    /// not "you are behind". A Director whose version cannot be parsed is likewise never called behind.
    /// A Director that was stopped, or not heard from in a day, is not nagged about.
    /// </summary>
    private OutdatedDirectorsAttentionDto? OutdatedDirectors(TenantId tenant, DateTime now)
    {
        if (_directors is null || _newestRelease is null)
            return null;

        var newest = _newestRelease();
        if (string.IsNullOrWhiteSpace(newest))
        {
            FileLog.Write("[MorningReportBuilder] OutdatedDirectors: the newest release has not been read yet - " +
                          "the row is OMITTED, nothing is called behind or current");
            return null;
        }

        var behind = _directors(tenant)
            .Where(d => d.StoppedAtUtc is null)
            .Where(d => d.LastSeen is { } seen && now - seen <= DirectorSeenWithin)
            .Where(d => Api.FleetMachinesFold.CompareToNewest(d.Version, newest).Behind)
            .Select(d => new OutdatedDirectorDto
            {
                Machine = string.IsNullOrWhiteSpace(d.MachineName) ? d.DirectorId : d.MachineName,
                Version = d.Version,
            })
            .OrderBy(d => d.Machine, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (behind.Count == 0)
            return null;

        return new OutdatedDirectorsAttentionDto { Newest = newest, Directors = behind };
    }

    /// <summary>How far back a usage-limit stop is still worth a line. The limits that matter reset within a
    /// week at most, and the row drops a session the moment it works again, so this only bounds the scan and
    /// retires a stop nobody ever came back to.</summary>
    public static readonly TimeSpan UsageLimitLookback = TimeSpan.FromHours(48);

    /// <summary>
    /// The usage-limit row (#3124), or NULL: sessions whose turn ended on a usage-limit block and that have
    /// not been active since.
    ///
    /// WHERE THE FACT COMES FROM. The session supervisor classifies the live screen when a turn ends and
    /// writes a <c>supervisor-fault-detected</c> line to the recovery log carrying the matched SIGNATURE
    /// (never screen content). This reads those lines for exactly
    /// <see cref="Supervision.TerminatingFaultClassifier.UsageLimitSignatures"/>. An account with the
    /// supervisor switched off has no such lines and gets no row - absent, never "none stopped".
    ///
    /// STILL STOPPED means: no <c>active</c> transition in the governance ledger after the stop, and, where
    /// the Gateway can see the session live, it has not exited. A session that picked its work back up after
    /// the reset is not news.
    /// </summary>
    private UsageLimitStopsAttentionDto? UsageLimitStops(GatewayDbContext ctx, TenantId tenant, DateTime now)
    {
        var since = now - UsageLimitLookback;
        var faults = ctx.ActivityEvents.AsNoTracking()
            .Where(e => e.EventType == ActivityEventTypes.SupervisorFaultDetected &&
                        e.Cause == ActivityCauses.NonRecoverable &&
                        e.OccurredUtc >= since &&
                        e.Detail != null)
            .OrderByDescending(e => e.OccurredUtc)
            .Select(e => new { e.SessionId, e.OccurredUtc, e.Detail })
            .Take(MaxLedgerRowsScanned)
            .ToList();

        // Newest first, so the first line kept per session is its latest stop.
        var latestStop = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var f in faults)
        {
            if (string.IsNullOrWhiteSpace(f.SessionId) || latestStop.ContainsKey(f.SessionId))
                continue;
            if (!IsUsageLimitDetail(f.Detail!))
                continue;
            latestStop[f.SessionId] = DateTime.SpecifyKind(f.OccurredUtc, DateTimeKind.Utc);
        }
        if (latestStop.Count == 0)
            return null;

        var ids = latestStop.Keys.ToList();
        var lastActive = ctx.GovernanceEvents.AsNoTracking()
            .Where(e => e.SubjectKind == GovernanceEventSubject.Session &&
                        e.SessionId != null && ids.Contains(e.SessionId) &&
                        e.State == GovernanceEventState.Active &&
                        e.OccurredUtc >= since)
            .GroupBy(e => e.SessionId!)
            .Select(g => new { SessionId = g.Key, At = g.Max(e => e.OccurredUtc) })
            .ToList()
            .ToDictionary(x => x.SessionId, x => DateTime.SpecifyKind(x.At, DateTimeKind.Utc), StringComparer.Ordinal);

        var live = LiveSessionsById(tenant);
        var sessions = new List<UsageLimitStopDto>();
        foreach (var (sessionId, stoppedUtc) in latestStop)
        {
            if (lastActive.TryGetValue(sessionId, out var activeAt) && activeAt > stoppedUtc)
                continue; // it worked again after the stop
            live.TryGetValue(sessionId, out var liveSession);
            if (liveSession is not null &&
                string.Equals(liveSession.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                continue; // closed - there is nothing left to resume
            sessions.Add(new UsageLimitStopDto
            {
                Session = string.IsNullOrWhiteSpace(liveSession?.Name) ? sessionId : liveSession!.Name!,
                StoppedUtc = stoppedUtc,
            });
        }
        if (sessions.Count == 0)
            return null;

        return new UsageLimitStopsAttentionDto
        {
            Sessions = sessions.OrderBy(s => s.StoppedUtc).ThenBy(s => s.Session, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>True when a recovery-log detail ("signature=... [ (model verdict)]") names a usage-limit block.</summary>
    internal static bool IsUsageLimitDetail(string detail)
    {
        const string prefix = "signature=";
        if (!detail.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var signature = detail[prefix.Length..];
        const string modelSuffix = " (model verdict)";
        if (signature.EndsWith(modelSuffix, StringComparison.Ordinal))
            signature = signature[..^modelSuffix.Length];
        return Supervision.TerminatingFaultClassifier.UsageLimitSignatures
            .Contains(signature, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Did this account do anything at all in the reported window (#3124)?
    ///
    /// TWO SIGNALS, EITHER OF WHICH MEANS YES, because one alone is wrong in a way that would silence
    /// somebody's email on a day they worked:
    ///
    ///   * A TURN SUBMITTED is a person driving an agent. It is the closest thing the Gateway holds to
    ///     "they used DevThrottle", and on a normal working day there are many.
    ///   * A SESSION BECOMING ACTIVE covers the day that produced no turn record - a session opened and
    ///     worked without the turn feed reaching this Gateway, which is the hosted case when a Director
    ///     is mid-reconnect.
    ///
    /// Anything found inside the window counts; nothing outside it does. The window is the reader's own
    /// calendar day (see MorningReportWindow), so this asks about THEIR yesterday and not about UTC's.
    /// </summary>
    private static bool WorkedInWindow(GatewayDbContext ctx, MorningReportWindow window)
    {
        var start = window.StartUtc;
        var end = window.EndUtc;

        var turned = ctx.ActivityEvents.AsNoTracking()
            .Any(e => e.EventType == ActivityEventTypes.TurnSubmitted &&
                      e.OccurredUtc >= start && e.OccurredUtc < end);
        if (turned) return true;

        return ctx.GovernanceEvents.AsNoTracking()
            .Any(e => e.SubjectKind == GovernanceEventSubject.Session &&
                      e.State == GovernanceEventState.Active &&
                      e.OccurredUtc >= start && e.OccurredUtc < end);
    }

    /// <summary>The floor of the running-too-long rule: a stretch shorter than this is never flagged,
    /// whatever the account's usual is. Owner's rule, 20 September 2026.</summary>
    public static readonly TimeSpan LongRunningFloor = TimeSpan.FromHours(3);

    /// <summary>The multiple of the running-too-long rule: the stretch must also be at least this many times
    /// the account's own usual. Owner's rule, 20 September 2026.</summary>
    public const double LongRunningMultiple = 10;

    /// <summary>How many FINISHED working stretches the account must have in the lookback before its usual
    /// is known. Below this the row is absent - a median of three turns is an anecdote.</summary>
    public const int LongRunningMinimumHistory = 20;

    /// <summary>How far back the usual is measured.</summary>
    public static readonly TimeSpan LongRunningLookback = TimeSpan.FromDays(14);

    /// <summary>
    /// The running-too-long row (#3124), or NULL.
    ///
    /// A WORKING STRETCH is the time from an <c>active</c> transition in the governance ledger to the next
    /// transition of the same session. Finished stretches give the account's usual (their median); a session
    /// whose LAST transition is <c>active</c> has an open stretch, measured to now.
    ///
    /// THE LEDGER ALONE CANNOT BE TRUSTED FOR "STILL WORKING", and this is the trap the row is built
    /// around: the state emitter closes an open WAIT when a session exits, but it writes nothing when a
    /// session that was active simply vanishes - the Director was closed, the laptop lid shut. That session
    /// stays "active" in the ledger for ever. So an open stretch is only reported when the Gateway can SEE
    /// the session live, right now, and it says Working. A session it cannot see is not claimed to be
    /// running: no live store, or a session missing from it, means no row for that session.
    /// </summary>
    private LongRunningSessionsAttentionDto? LongRunningSessions(GatewayDbContext ctx, TenantId tenant, DateTime now)
    {
        var live = LiveSessionsById(tenant);
        if (live.Count == 0)
            return null;

        var since = now - LongRunningLookback;
        var rows = ctx.GovernanceEvents.AsNoTracking()
            .Where(e => e.SubjectKind == GovernanceEventSubject.Session &&
                        e.SessionId != null &&
                        e.OccurredUtc >= since)
            .OrderByDescending(e => e.OccurredUtc)
            .ThenByDescending(e => e.RecordedUtc)
            .Select(e => new { e.SessionId, e.State, e.OccurredUtc })
            .Take(MaxLedgerRowsScanned)
            .ToList();

        var finishedMinutes = new List<double>();
        var openSince = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(r => r.SessionId!, StringComparer.Ordinal))
        {
            // Oldest first within the session, so each transition's successor is the next element.
            var events = group.Reverse().ToList();
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].State != GovernanceEventState.Active)
                    continue;
                var start = DateTime.SpecifyKind(events[i].OccurredUtc, DateTimeKind.Utc);
                if (i + 1 < events.Count)
                    finishedMinutes.Add((DateTime.SpecifyKind(events[i + 1].OccurredUtc, DateTimeKind.Utc) - start).TotalMinutes);
                else
                    openSince[group.Key] = start;
            }
        }

        if (finishedMinutes.Count < LongRunningMinimumHistory)
        {
            FileLog.Write($"[MorningReportBuilder] LongRunningSessions: tenant={tenant.ToLogString()} has " +
                          $"{finishedMinutes.Count} finished working stretch(es) in {LongRunningLookback.TotalDays:0} days, " +
                          $"under the {LongRunningMinimumHistory} needed to know its usual - the row is OMITTED");
            return null;
        }

        finishedMinutes.Sort();
        var mid = finishedMinutes.Count / 2;
        var usualMinutes = finishedMinutes.Count % 2 == 1
            ? finishedMinutes[mid]
            : (finishedMinutes[mid - 1] + finishedMinutes[mid]) / 2.0;

        var sessions = new List<LongRunningSessionDto>();
        foreach (var (sessionId, start) in openSince)
        {
            if (!live.TryGetValue(sessionId, out var liveSession) ||
                !string.Equals(liveSession.ActivityState, "Working", StringComparison.OrdinalIgnoreCase))
                continue; // not provably working right now - see the summary
            var running = now - start;
            if (running < LongRunningFloor || running.TotalMinutes < usualMinutes * LongRunningMultiple)
                continue;
            sessions.Add(new LongRunningSessionDto
            {
                Session = string.IsNullOrWhiteSpace(liveSession.Name) ? sessionId : liveSession.Name!,
                RunningHours = Math.Round(running.TotalHours, 1),
            });
        }
        if (sessions.Count == 0)
            return null;

        return new LongRunningSessionsAttentionDto
        {
            UsualMinutes = Math.Round(usualMinutes, 1),
            Sessions = sessions.OrderByDescending(s => s.RunningHours).ThenBy(s => s.Session, StringComparer.Ordinal).ToList(),
        };
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
    /// <summary>How many open waits named a session this Gateway can no longer see, from the most
    /// recent <see cref="WaitingSessions"/> call. Counted, never listed - see the note there.</summary>
    private int LostContact { get; set; }

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
        // Open waits whose session the Gateway can no longer see. Reported as a COUNT, never as rows.
        var lostContact = 0;

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

            // A SESSION THE GATEWAY CANNOT SEE IS NOT REPORTED (#3124, owner's ruling 20 September
            // 2026). This used to report it from the ledger as-is, on the reasoning that a durable
            // record is the whole point. What that produced was 93 rows naming sessions that no longer
            // existed, while the 15 that really were waiting went unmentioned - because the checks just
            // below can only run on a session we can see, so an unseeable one was reported as waiting
            // purely because nothing contradicted it. We also cannot name it or link to it. A row that
            // cannot be named, opened, or vouched for is not something to put in front of somebody at
            // 7am, so it is counted and left out.
            if (!live.TryGetValue(sessionId, out var liveSession))
            {
                lostContact++;
                continue;
            }
            // HELD (snoozed) was deliberately parked by the owner and is not "waiting on you" this
            // morning; EXITED is not waiting on anybody.
            if (HoldStates.IsHeld(liveSession.HoldState))
                continue;
            if (string.Equals(liveSession.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                continue;

            var since = DateTime.SpecifyKind(row.OccurredUtc, DateTimeKind.Utc);
            items.Add(new WaitingSessionAttentionDto
            {
                // No id fallback any more: we only get here when the session is live, and a live
                // session has a name. If it somehow has none, the id is still better than an empty
                // row - but a test asserts the live path always supplies a name.
                Session = string.IsNullOrWhiteSpace(liveSession.Name) ? sessionId : liveSession.Name!,
                SessionId = sessionId,
                Repo = string.IsNullOrWhiteSpace(liveSession.RepoName) ? null : liveSession.RepoName,
                Machine = string.IsNullOrWhiteSpace(liveSession.MachineName) ? null : liveSession.MachineName,
                Number = liveSession.Number,
                ControllerSessionId = string.IsNullOrWhiteSpace(liveSession.ControllerSessionId)
                    ? null : liveSession.ControllerSessionId,
                WaitingSinceUtc = since,
                AgeHours = Math.Round(Math.Max(0, (now - since).TotalHours), 1),
            });
        }

        if (staleWaits > 0)
            FileLog.Write($"[MorningReportBuilder] WaitingSessions: {staleWaits} open wait(s) older than " +
                          $"{WaitingReportMaxAge.TotalHours:0}h were NOT reported for tenant={tenant.ToLogString()} - " +
                          "the Gateway has not heard about those sessions since, so it does not claim they are waiting.");

        if (lostContact > 0)
            FileLog.Write($"[MorningReportBuilder] WaitingSessions: {lostContact} open wait(s) name a session this " +
                          $"Gateway can no longer see for tenant={tenant.ToLogString()} - counted, not listed.");
        LostContact = lostContact;

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
