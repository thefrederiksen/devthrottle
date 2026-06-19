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
}

/// <summary>The result of <see cref="CronEngine.RunNowAsync"/>.</summary>
public sealed record CronRunNowResult(CronFireOutcome Outcome, CronRunRecord? Record);

/// <summary>
/// The cron firing engine (epic #479, part 2 = #483). On each <see cref="EvaluateDueAsync"/> sweep it
/// fires every enabled job whose <see cref="CronJobDto.NextRunUtc"/> is due (per the injected
/// <see cref="IClock"/>), starts a session on the job's target Director via
/// <see cref="ICronSessionStarter"/>, records a <see cref="CronRunRecord"/>, and advances the
/// schedule. The guards:
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

    private readonly CronJobStore _store;
    private readonly CronRunHistoryStore _history;
    private readonly ICronSessionStarter _starter;
    private readonly ICronWorkListRunner _workListRunner;
    private readonly IClock _clock;
    private readonly TimeSpan _catchUpThreshold;

    private readonly object _inFlightGate = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    /// <param name="starter">Starts a single seeded session for a seed-action job.</param>
    /// <param name="workListRunner">Drains a named work list for a work-list-action job (#484).</param>
    /// <param name="catchUpThreshold">
    /// How far past its due time a scheduled fire must be to be labeled a catch-up (default 2 min).
    /// Purely cosmetic on the run record; it does not change whether the job fires.
    /// </param>
    public CronEngine(
        CronJobStore store,
        CronRunHistoryStore history,
        ICronSessionStarter starter,
        ICronWorkListRunner workListRunner,
        IClock clock,
        TimeSpan? catchUpThreshold = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _starter = starter ?? throw new ArgumentNullException(nameof(starter));
        _workListRunner = workListRunner ?? throw new ArgumentNullException(nameof(workListRunner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _catchUpThreshold = catchUpThreshold ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>
    /// Fire every enabled job that is due as of <see cref="IClock.UtcNow"/>, advancing each schedule.
    /// Returns the records produced this sweep. A single job's failure is logged and isolated (this is
    /// the timer boundary) so one bad job never aborts the sweep.
    /// </summary>
    public async Task<IReadOnlyList<CronRunRecord>> EvaluateDueAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var due = _store.ListAll()
            .Where(j => j.Enabled && j.NextRunUtc is not null && j.NextRunUtc.Value <= now)
            .ToList();

        var fired = new List<CronRunRecord>();
        foreach (var job in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await FireAsync(job, job.NextRunUtc ?? now, isManual: false, ct);
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

        return await FireAsync(job, _clock.UtcNow, isManual: true, ct);
    }

    private async Task<CronRunNowResult> FireAsync(CronJobDto job, DateTime scheduledUtc, bool isManual, CancellationToken ct)
    {
        if (job.PreventOverlap && !TryEnterFlight(job.Id))
        {
            FileLog.Write($"[CronEngine] skip overlap: job={job.Id} (a prior run is still in flight)");
            return new CronRunNowResult(CronFireOutcome.SkippedOverlap, null);
        }

        try
        {
            // A work-list action (#484) drains a named list via the runner; a seed action starts a
            // single session. Either way a run is recorded and the schedule advances below.
            var isWorkList = !string.IsNullOrWhiteSpace(job.Action.WorkListName);
            string? sessionId;
            string? error;
            string? resolvedDirectorId = null;   // the Director the machine resolved to (#503)
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
                (sessionId, resolvedDirectorId, error) = await _starter.StartAsync(job, ct);
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
            };
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
                // Recurring: advance to the next FUTURE occurrence from now - this is what makes a
                // missed fire a single catch-up rather than a replay of every missed interval.
                var next = CronSchedule.ComputeNextRunUtc(job, firedUtc);
                _store.MarkFired(job.Id, firedUtc, infraStatus, next, enabled: true);
            }

            if (error is not null)
                FileLog.Write($"[CronEngine] fire start error: job={job.Id}: {error}");
            FileLog.Write($"[CronEngine] fired: job={job.Id}, manual={isManual}, infra={infraStatus}, sid={sessionId}");
            return new CronRunNowResult(CronFireOutcome.Fired, record);
        }
        finally
        {
            if (job.PreventOverlap)
                ExitFlight(job.Id);
        }
    }

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

    private bool TryEnterFlight(string id)
    {
        lock (_inFlightGate)
            return _inFlight.Add(id);
    }

    private void ExitFlight(string id)
    {
        lock (_inFlightGate)
            _inFlight.Remove(id);
    }
}
