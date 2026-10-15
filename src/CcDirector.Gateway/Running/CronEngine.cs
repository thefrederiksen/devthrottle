using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>The outcome of a fire attempt (scheduled sweep or run-now).</summary>
public enum CronFireOutcome
{
    /// <summary>A session start was attempted and a run was recorded (start may have failed - see the record's InfraStatus).</summary>
    Fired,

    /// <summary>Skipped because a prior run of the same job is still in flight and the job forbids overlap.</summary>
    SkippedOverlap,

    /// <summary>No job with the requested id exists (run-now only).</summary>
    NoSuchJob,

    /// <summary>Nothing was started because the account's scheduler is paused (issue #3910; run-now only).</summary>
    Paused,
}

/// <summary>The result of <see cref="CronEngine.RunNowAsync"/>.</summary>
public sealed record CronRunNowResult(CronFireOutcome Outcome, CronRunRecord? Record);

/// <summary>
/// The cron firing engine (epic #479, part 2 = #483). On each <see cref="EvaluateDueAsync"/> sweep it
/// fires every enabled job whose <see cref="CronJobDto.NextRunUtc"/> is due (per the injected
/// <see cref="IClock"/>), starts a session on the job's target Director via
/// <see cref="ICronSessionStarter"/>, records a <see cref="CronRunRecord"/>, and advances the
/// schedule. The guards:
///   - PAUSE EVERYTHING (issue #3910): while the account's switch is on nothing is fired, and no job is changed.
///   - DISABLED jobs are never fired.
///   - OVERLAP: a job already in flight is skipped (when <see cref="CronJobDto.PreventOverlap"/>).
///   - CATCH-UP: a fire missed while the Gateway was down fires AT MOST ONCE on the next sweep - the
///     schedule is recomputed from "now" to the next FUTURE occurrence, never replaying the backlog.
///   - ONE-OFF: a one-off job fires once and then auto-disables.
/// The engine watches nothing to completion (that is the work-list runner's job); it fires and
/// records, leaving <see cref="CronRunRecord.TaskStatus"/> as <c>unknown</c>.
/// </summary>
public sealed class CronEngine
{
    private const string TaskStatusUnknown = "unknown";

    /// <summary>The last status of a window schedule whose missed run was not started because its deadline had passed.</summary>
    public const string SkippedPastDeadline = "skipped: past its deadline";

    private readonly CronJobStore _store;
    private readonly CronRunHistoryStore _history;
    private readonly ICronSessionStarter _starter;
    private readonly ICronWorkListRunner _workListRunner;
    private readonly ICronNotifier _notifier;
    private readonly IClock _clock;
    private readonly TimeSpan _catchUpThreshold;
    private readonly Func<TenantId?> _resolveTenant;
    // Factory Control, step 1 and 6: told of every run the engine records WITH its result already on it (a fire that
    // started no session, a due run that was skipped or missed), so the run's factory hears about it. Production passes
    // CronRunResultService.Announce; a test that does not watch factory rows passes nothing.
    private readonly Action<TenantId, CronJobDto, CronRunRecord>? _onResultRecorded;
    private readonly Func<TenantId, bool> _isPaused;
    private readonly Func<TenantId, bool> _factoryAgentsOn;

    private readonly object _inFlightGate = new();
    // Overlap admission, PARTITIONED BY TENANT (audit MED, gap audit-e). A cron job's id is tenant-relative
    // (the store mints it under the tenant query filter, so two tenants can hold the same cj_ id; the DB
    // identity is (TenantId, Id)). Keying the in-flight set by the bare id alone let one tenant's in-flight
    // run refuse another tenant's run-now of a same-id job as an "overlap". The key is now (tenant, id), so a
    // caller's overlap guard only ever conflicts within its own tenant.
    private readonly HashSet<(TenantId Tenant, string Id)> _inFlight = new();

    /// <param name="starter">Starts a single seeded session for a seed-action job.</param>
    /// <param name="workListRunner">Drains a named work list for a work-list-action job (#484).</param>
    /// <param name="notifier">
    /// Delivers the per-job run-complete notification (issue #622) when the job opts in via
    /// <see cref="CronJobDto.NotifyOn"/>. Best-effort: a notification failure never affects a fire.
    /// </param>
    /// <param name="catchUpThreshold">
    /// How far past its due time a scheduled fire must be to be labeled a catch-up (default 2 min).
    /// Purely cosmetic on the run record; it does not change whether the job fires.
    /// </param>
    /// <param name="resolveTenant">
    /// Resolves the tenant of the current unit of work - the tenant the overlap guard is partitioned by
    /// (audit MED, gap audit-e). Production passes the background-loop/request tenant seam
    /// (<c>() =&gt; _tenantPass.Current</c>): the run-now request scope on hosted, the per-tenant pass or the
    /// single Local scope on self-host - exactly the tenant the store scoped the job read to. Omitted (tests,
    /// self-host wiring) it is <see cref="TenantId.Local"/>, so the single-tenant behaviour is unchanged. A
    /// fire is only ever reached with a resolved scope in effect (the store read that produced the job already
    /// required one); an unresolved tenant fails loud rather than defaulting.
    /// </param>
    /// <param name="isPaused">
    /// Whether the account's pause-everything switch is on (issue #3910). Read at the top of every sweep and every
    /// run-now, so a pause takes effect on the next sweep and survives a restart with the stored switch. Omitted
    /// (tests that are not about the switch) nothing is ever paused.
    /// </param>
    public CronEngine(
        CronJobStore store,
        CronRunHistoryStore history,
        ICronSessionStarter starter,
        ICronWorkListRunner workListRunner,
        ICronNotifier notifier,
        IClock clock,
        TimeSpan? catchUpThreshold = null,
        Func<TenantId?>? resolveTenant = null,
        Action<TenantId, CronJobDto, CronRunRecord>? onResultRecorded = null,
        Func<TenantId, bool>? isPaused = null,
        Func<TenantId, bool>? factoryAgentsOn = null)
    {
        _onResultRecorded = onResultRecorded;
        _factoryAgentsOn = factoryAgentsOn ?? (_ => true);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _starter = starter ?? throw new ArgumentNullException(nameof(starter));
        _workListRunner = workListRunner ?? throw new ArgumentNullException(nameof(workListRunner));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _catchUpThreshold = catchUpThreshold ?? TimeSpan.FromMinutes(2);
        _resolveTenant = resolveTenant ?? (() => TenantId.Local);
        _isPaused = isPaused ?? (_ => false);
    }

    /// <summary>
    /// Fire every enabled job that is due as of <see cref="IClock.UtcNow"/>, advancing each schedule.
    /// Returns the records produced this sweep. A single job's failure is logged and isolated (this is
    /// the timer boundary) so one bad job never aborts the sweep.
    /// </summary>
    public async Task<IReadOnlyList<CronRunRecord>> EvaluateDueAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        // PAUSE EVERYTHING: nothing is fired and nothing is written - no schedule is moved on, switched off or
        // marked, so resuming finds every schedule as it was. Resuming moves the missed runs on (CronPauseEndpoint).
        if (_resolveTenant() is { } sweepTenant && _isPaused(sweepTenant))
        {
            FileLog.Write($"[CronEngine] EvaluateDueAsync: tenant={sweepTenant.ToLogString()} is paused; nothing fired");
            return Array.Empty<CronRunRecord>();
        }
        FailStalledDueStarts(now);
        var due = _store.ListAll()
            .Where(j => j.Enabled && j.NextRunUtc is not null && j.NextRunUtc.Value <= now)
            .ToList();

        var fired = new List<CronRunRecord>();
        foreach (var job in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // A window schedule promises to be done by its deadline. A fire the sweep reaches after that moment -
                // the Gateway stayed up but the sweep came late, as after the machine slept - is not started late; the
                // schedule moves on to its next run, and its last status says so. (A Gateway restart moves the schedules
                // its load reads on from now and records what they missed in the same write - CronJobStore. A hosted
                // customer account's schedules are not in that load, so they are reached here late instead, and the
                // times they skip are recorded below.)
                var dueUtc = job.NextRunUtc ?? now;
                if (WindowSchedule.DeadlineFor(job, dueUtc) is { } deadline && now >= deadline)
                {
                    // Recorded BEFORE the schedule moves on, so a failure in between leaves the run due and the next sweep
                    // records it then (once - a due time already on record is not recorded again), never a silent skip.
                    RecordDidNotRun(job, dueUtc, now,
                        $"it was reached only after its deadline ({deadline:yyyy-MM-dd HH:mm} UTC), so it was not started late");
                    RecordLaterDueTimes(job, dueUtc, now, CronSchedule.MaxMissedRunsRecorded - 1);
                    var next = CronSchedule.ComputeNextRunUtc(job, now);
                    _store.SkipRun(job.Id, SkippedPastDeadline, next);
                    FileLog.Write($"[CronEngine] skip past deadline: job={job.Id}, due={dueUtc:o}, deadline={deadline:o}, next={next:o}");
                    continue;
                }
                // A sweep that reaches a schedule late fires it once, for the time it was first due, and moves it on to its
                // next FUTURE time - so every other time it fell due in between never runs. Each is recorded as a run that
                // did not run (Factory Control, step 1), before the fire moves the schedule on.
                RecordLaterDueTimes(job, dueUtc, now, CronSchedule.MaxMissedRunsRecorded);
                var result = await FireAsync(job, dueUtc, isManual: false, ct);
                if (result.Record is not null)
                    fired.Add(result.Record);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Timer boundary: isolate one job's failure so the sweep continues for the rest.
                FileLog.Write($"[CronEngine] EvaluateDueAsync job FAILED: id={job.Id}: {ex.Message}");
            }
        }

        if (fired.Count > 0)
            FileLog.Write($"[CronEngine] EvaluateDueAsync: fired {fired.Count} job(s) at {now:o}");
        return fired;
    }

    /// <summary>
    /// Fire a job immediately, independent of its schedule (the run-now surface). Records a run and
    /// updates the job's last-run metadata, but does NOT advance the schedule or disable a one-off -
    /// a manual run is out-of-band. Returns <see cref="CronFireOutcome.NoSuchJob"/> if the id is unknown.
    /// </summary>
    public async Task<CronRunNowResult> RunNowAsync(string id, CancellationToken ct)
    {
        var job = _store.Get(id);
        if (job is null)
            return new CronRunNowResult(CronFireOutcome.NoSuchJob, null);
        if (_resolveTenant() is { } tenant && _isPaused(tenant))
        {
            FileLog.Write($"[CronEngine] RunNowAsync: job={id} not started, tenant={tenant.ToLogString()} is paused");
            return new CronRunNowResult(CronFireOutcome.Paused, null);
        }

        return await FireAsync(job, _clock.UtcNow, isManual: true, ct);
    }

    private async Task<CronRunNowResult> FireAsync(CronJobDto job, DateTime scheduledUtc, bool isManual, CancellationToken ct)
    {
        // Resolve the tenant of THIS fire ONCE, up front, and key the overlap guard by it. This is the same
        // tenant the store scoped the job read to (a fire is only ever reached inside a resolved scope), so an
        // unresolved tenant here is a boundary bug and fails loud (deny-by-default) rather than defaulting.
        var tenant = _resolveTenant()
            ?? throw new InvalidOperationException(
                "A cron fire ran with no tenant scope in effect. The overlap guard is partitioned by tenant " +
                "and cannot key an unresolved fire; the caller path was not bound at its tenant boundary.");

        if (job.PreventOverlap && !TryEnterFlight(tenant, job.Id))
        {
            FileLog.Write($"[CronEngine] skip overlap: tenant={tenant.ToLogString()} job={job.Id} kind={job.ScheduleKind} (a prior run is still in flight)");
            return new CronRunNowResult(CronFireOutcome.SkippedOverlap, null);
        }

        try
        {
            // A WORK-LIST action (#484) drains a named list via the runner; a seed action starts a single session. A
            // seed schedule WITH A CHECK (Factory Definition, phase 5b) starts neither: it records the due check and
            // returns, so nothing in the sweep ever waits on a Director.
            var isWorkList = !string.IsNullOrWhiteSpace(job.Action.WorkListName);
            if (job.HasCheck && !isWorkList)
                return FireCheck(tenant, job, scheduledUtc, isManual);

            string? sessionId;
            string? error;
            string? resolvedDirectorId = null;   // the Director the machine resolved to (#503)
            int? briefVersion = null;            // the seat's brief the Gateway gave the run, when it gave one
            string? briefVersionKind = null;     // and which kind of version that is: the brief's own, or the definition's
            bool started;
            string baseStatus;
            if (isWorkList)
            {
                var outcome = await _workListRunner.TriggerAsync(job, ct);
                sessionId = null;                                  // a drain starts many sessions, not one
                started = outcome == CronWorkListOutcome.Started;
                error = started ? null : outcome.ToString();
                baseStatus = "worklist-" + WorkListStatusSuffix(outcome);
            }
            else
            {
                (sessionId, resolvedDirectorId, error, briefVersion, briefVersionKind) = await _starter.StartRunAsync(job, ct);
                started = sessionId is not null;
                baseStatus = started ? "started" : "not-started";
            }

            var firedUtc = _clock.UtcNow;
            var isCatchUp = !isManual && started && (firedUtc - scheduledUtc) > _catchUpThreshold;

            var infraStatus = isCatchUp ? "catch-up" : baseStatus;
            var record = new CronRunRecord
            {
                ScheduledUtc = scheduledUtc,
                FiredUtc = firedUtc,
                Machine = job.Target.Machine,
                TargetDirectorId = resolvedDirectorId ?? "",
                SessionId = sessionId,
                InfraStatus = infraStatus,
                TaskStatus = TaskStatusUnknown,
                // Which brief this run was given, fixed on the run: the brief can be published again a minute later.
                BriefVersion = briefVersion,
                BriefVersionKind = briefVersionKind,
            };
            // How the run went (Factory Control, step 1). A started session owes its report. A fire that started
            // nothing did not run. A work-list drain starts many sessions and records none, so no one session can
            // report for it - it is untracked, as is a drain that found its list empty (nothing was due).
            if (!started)
            {
                record.Result = CronRunResults.Problem;
                record.Problem = CronRunProblems.DidNotRun;
                record.ResultReason = DidNotStartReason(isWorkList, error);
                record.ResultUtc = firedUtc;
            }
            else
            {
                record.Result = isWorkList ? CronRunResults.Untracked : CronRunResults.Pending;
            }
            // The schedule's factory, seat and name are fixed on the run when it is created: what the run owes its
            // factory, and where its problems are listed and who may resolve them, never moves with a later edit or
            // delete of the schedule.
            CronRunResultFold.StampFactoryLink(record, job);
            _history.Append(job.Id, record);

            if (isManual)
            {
                // Run-now is out-of-band: record the fire, refresh last-run metadata, leave the
                // schedule and enabled-state untouched.
                _store.MarkFired(job.Id, firedUtc, infraStatus, job.NextRunUtc, job.Enabled);
            }
            else if (CronSchedule.KindOneOff.Equals(job.ScheduleKind?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // A one-off fires once, then auto-disables (no next run).
                _store.MarkFired(job.Id, firedUtc, infraStatus, nextRunUtc: null, enabled: false);
            }
            else
            {
                // Recurring, and random (#3622), which walks its day plan the same way: advance to the next
                // FUTURE occurrence from now - this is what makes a missed fire a single catch-up rather than a
                // replay of every missed interval.
                var next = CronSchedule.ComputeNextRunUtc(job, firedUtc);
                _store.MarkFired(job.Id, firedUtc, infraStatus, next, enabled: true);
            }

            // Only once the schedule has moved on: a failure telling the factory must never leave the job due again.
            if (!started)
                _onResultRecorded?.Invoke(tenant, job, record);

            if (error is not null)
                FileLog.Write($"[CronEngine] fire start error: job={job.Id}: {error}");
            FileLog.Write($"[CronEngine] fired: job={job.Id}, manual={isManual}, infra={infraStatus}, sid={sessionId}");

            // Issue #622: deliver the per-job run-complete notification when the job opts in. This is
            // the terminal event a supervisor wants to be told about - it rides the existing fleet
            // channel (and an optional webhook). Best-effort and isolated: a notification failure must
            // never affect the fire, whose outcome is already in run history above.
            await NotifyIfOptedInAsync(job, record, started, error, resolvedDirectorId, ct);

            return new CronRunNowResult(CronFireOutcome.Fired, record);
        }
        finally
        {
            if (job.PreventOverlap)
                ExitFlight(tenant, job.Id);
        }
    }

    /// <summary>The infrastructure status of a run whose check is due and has not reported.</summary>
    public const string CheckDueStatus = "check-due";

    /// <summary>
    /// A due check whose check counted work and whose session start never returned (phase 5b, review 2, finding 3) is
    /// recorded as did-not-run once <see cref="CronRunHistoryStore.DueStartGrace"/> has passed - read on every sweep,
    /// and so also on the first sweep after a Gateway start, never left reading "checking" for good.
    /// </summary>
    private void FailStalledDueStarts(DateTime now)
    {
        if (_resolveTenant() is not { } tenant) return;
        foreach (var stalled in _history.FailStalledDueStarts(tenant, now - CronRunHistoryStore.DueStartGrace, now))
        {
            FileLog.Write($"[CronEngine] due start never returned: job={stalled.JobId}, run={stalled.Run.RunId}: {stalled.Run.ResultReason}");
            if (_store.Get(stalled.JobId) is { } job)
                _onResultRecorded?.Invoke(tenant, job, stalled.Run);
        }
    }

    /// <summary>The infrastructure status of a due check fired while the account's Factory Agents switch is off: no
    /// Director can be handed it.</summary>
    public const string CheckDueSwitchedOffStatus = "check-due-switched-off";

    /// <summary>Why a due check cannot run on an account whose Factory Agents switch is off.</summary>
    public const string FactoryAgentsOffSentence =
        "factory agents are switched off for this account, so no Director is handed the check";

    /// <summary>
    /// THE FIRE OF A SCHEDULE WITH A CHECK (Factory Definition, phase 5b, design 3.3). It starts no session and calls no
    /// Director: it appends the run row with result <c>checking</c> - that row IS the due check, handed to a Director on
    /// the schedule's machine by its next trigger pull - and moves the schedule on, exactly as any fire does. The
    /// Gateway decides whether a session starts when the check reports (<c>DueCheckService</c>).
    ///
    /// SUPERSEDED: when the schedule's previous run is still <c>checking</c> at this fire, no Director ran its check
    /// before its next time. That row becomes a <c>check-not-run</c> problem first, so a machine that is off for days
    /// shows one problem per missed time, never a pile of rows still waiting.
    /// </summary>
    private CronRunNowResult FireCheck(TenantId tenant, CronJobDto job, DateTime scheduledUtc, bool isManual)
    {
        var firedUtc = _clock.UtcNow;
        // A Director is handed a due check only through the factory routes, which answer nothing while the account's
        // Factory Agents switch is off - so then the check cannot run, and the run and the schedule say why.
        var switchedOff = !_factoryAgentsOn(tenant);
        // The reason is the superseded run's own truth: whether the switch was off when IT fired.
        var superseded = _history.SupersedeUnreportedCheck(tenant, job.Id, infra => infra == CheckDueSwitchedOffStatus
            ? $"{FactoryAgentsOffSentence}; the check of {job.Name} did not run before its next time"
            : $"no Director on {job.Target.Machine} ran the check of {job.Name} before its next time", firedUtc);
        if (superseded is not null)
        {
            FileLog.Write($"[CronEngine] check not run: job={job.Id}, run={superseded.RunId}: {superseded.ResultReason}");
            _onResultRecorded?.Invoke(tenant, job, superseded);
        }
        var infraStatus = switchedOff ? CheckDueSwitchedOffStatus : CheckDueStatus;
        var lastStatus = switchedOff ? $"{CheckDueStatus}: {FactoryAgentsOffSentence}" : CheckDueStatus;

        var record = new CronRunRecord
        {
            ScheduledUtc = scheduledUtc,
            FiredUtc = firedUtc,
            Machine = job.Target.Machine,
            TargetDirectorId = "",
            SessionId = null,
            InfraStatus = infraStatus,
            TaskStatus = TaskStatusUnknown,
            Result = CronRunResults.Checking,
        };
        CronRunResultFold.StampFactoryLink(record, job);
        _history.Append(job.Id, record);

        if (isManual)
            _store.MarkFired(job.Id, firedUtc, lastStatus, job.NextRunUtc, job.Enabled);
        else if (CronSchedule.KindOneOff.Equals(job.ScheduleKind?.Trim(), StringComparison.OrdinalIgnoreCase))
            _store.MarkFired(job.Id, firedUtc, lastStatus, nextRunUtc: null, enabled: false);
        else
            _store.MarkFired(job.Id, firedUtc, lastStatus, CronSchedule.ComputeNextRunUtc(job, firedUtc), enabled: true);

        FileLog.Write($"[CronEngine] fired a due check: job={job.Id}, manual={isManual}, run={record.RunId}, check={job.CheckPath}, machine={job.Target.Machine}, factory agents on={!switchedOff}");
        return new CronRunNowResult(CronFireOutcome.Fired, record);
    }

    /// <summary>
    /// Deliver the run-complete notification when <paramref name="job"/> opts in via its
    /// <see cref="CronJobDto.NotifyOn"/> policy (issue #622). Builds the payload - job name, outcome
    /// (infra-status vs task-status), machine, session id, and a deep link to the session - and hands
    /// it to the notifier, which rides the existing fleet channel and the optional webhook. The
    /// notifier is best-effort and swallows its own delivery failures, so this never disturbs the fire.
    /// </summary>
    private async Task NotifyIfOptedInAsync(
        CronJobDto job, CronRunRecord record, bool started, string? error, string? directorId, CancellationToken ct)
    {
        if (!CronNotify.ShouldNotify(job.NotifyOn, started))
            return;

        var payload = new CronRunCompletedPayload
        {
            JobId = job.Id,
            JobName = job.Name,
            Succeeded = started,
            InfraStatus = record.InfraStatus,
            TaskStatus = record.TaskStatus,
            Machine = record.Machine,
            SessionId = record.SessionId,
            SessionLink = _notifier.BuildSessionLink(record.SessionId),
            Reason = started ? null : (error ?? "the scheduled run did not start"),
            FiredUtc = record.FiredUtc,
        };

        await _notifier.NotifyRunCompletedAsync(job, directorId ?? "", payload, ct);
    }

    private static string DidNotStartReason(bool isWorkList, string? error) =>
        string.IsNullOrWhiteSpace(error)
            ? (isWorkList ? "its work list did not start" : "it did not start a session")
            : (isWorkList ? $"its work list did not start: {error}" : $"it did not start a session: {error}");

    /// <summary>
    /// Record a due run that never fired as a run with the problem "did not run" (Factory Control, step 1), so it is
    /// seen rather than silently skipped. A due time already on record for this schedule is not recorded again, so a
    /// sweep that retries after a failure cannot write it twice.
    /// </summary>
    private void RecordDidNotRun(CronJobDto job, DateTime dueUtc, DateTime nowUtc, string reason, int missedNotListed = 0)
    {
        var tenant = _resolveTenant()
            ?? throw new InvalidOperationException("A missed cron run was recorded with no tenant scope in effect.");
        if (_history.HasRunScheduledAt(job.Id, dueUtc))
        {
            FileLog.Write($"[CronEngine] did not run: job={job.Id}, due={dueUtc:o} is already on record");
            return;
        }
        var record = CronRunResultFold.DidNotRunRecord(job, dueUtc, nowUtc, reason, missedNotListed);
        _history.Append(job.Id, record);
        FileLog.Write($"[CronEngine] did not run: job={job.Id}, due={dueUtc:o}, reason={reason}");
        _onResultRecorded?.Invoke(tenant, job, record);
    }

    // Every time after dueUtc and up to now that the schedule fell due: none of them will run, because the schedule is
    // about to move on to its next future time.
    private void RecordLaterDueTimes(CronJobDto job, DateTime dueUtc, DateTime nowUtc, int maxListed)
    {
        var (later, more) = CronSchedule.DueBetween(job, dueUtc, nowUtc, maxListed);
        for (var i = 0; i < later.Count; i++)
        {
            var last = i == later.Count - 1 && more > 0;
            var reason = last
                ? $"{LateSweepReason}; {more} later runs were also due before then, past the {CronSchedule.MaxMissedRunsRecorded} recorded one by one"
                : LateSweepReason;
            RecordDidNotRun(job, later[i], nowUtc, reason, last ? more : 0);
        }
    }

    private const string LateSweepReason =
        "the schedule was reached late, so it ran once for an earlier time and moved on past this one";

    /// <summary>The infrastructure status of a run that was due and never fired.</summary>
    public const string DidNotRunStatus = CronRunResultFold.DidNotRunStatus;

    private static string WorkListStatusSuffix(CronWorkListOutcome outcome) => outcome switch
    {
        CronWorkListOutcome.Started => "started",
        CronWorkListOutcome.EmptyList => "empty",
        CronWorkListOutcome.NoSuchList => "no-list",
        CronWorkListOutcome.AlreadyClaimed => "already-claimed",
        CronWorkListOutcome.NoSuchDirector => "no-director",
        CronWorkListOutcome.MachineBusy => "machine-busy",
        _ => "unknown",
    };

    private bool TryEnterFlight(TenantId tenant, string id)
    {
        lock (_inFlightGate)
            return _inFlight.Add((tenant, id));
    }

    private void ExitFlight(TenantId tenant, string id)
    {
        lock (_inFlightGate)
            _inFlight.Remove((tenant, id));
    }
}
