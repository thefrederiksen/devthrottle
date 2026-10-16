using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway;

/// <summary>
/// The run-history store for cron jobs (epic #479, #483): one <see cref="CronRunRecord"/> per fire, keyed by
/// job id, newest-first, capped per job.
///
/// PERSISTENCE (Hosted Gateway mission, Step 1b): runs live in the EF data layer's <c>cron_runs</c> table
/// (SQLite locally), NOT the old hand-rolled <c>cronruns.json</c>. The public API and observable behavior
/// are unchanged - newest-first ordering and the per-job cap of <see cref="MaxRecordsPerJob"/> hold exactly
/// as before. Newest-first is served by ordering on the code-assigned <see cref="CronRunEntity.Sequence"/>
/// (highest = newest), which reproduces the old prepend-on-append order and survives a restart and the
/// one-time import.
///
/// ONE-TIME IMPORT: on first run after the upgrade, if a legacy <c>cronruns.json</c> exists and the table is
/// empty, every job's run list is imported (newest-first order preserved, capped at <see cref="MaxRecordsPerJob"/>)
/// inside one transaction, then the JSON file is renamed aside as a backup. Fail-loud and all-or-nothing, the
/// same contract as <see cref="CronJobStore"/>.
/// </summary>
public sealed class CronRunHistoryStore
{
    /// <summary>Max records retained per job; older runs are pruned (keeps the store bounded).</summary>
    public const int MaxRecordsPerJob = 50;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly string _legacyJsonPath;

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <param name="legacyJsonPath">The legacy <c>cronruns.json</c> path to import ONCE if it exists and the
    /// table is empty. REQUIRED (no silent default).</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    /// <exception cref="ArgumentException">The legacy path is null/empty/whitespace.</exception>
    public CronRunHistoryStore(GatewayDatabase db, string legacyJsonPath)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrWhiteSpace(legacyJsonPath))
            throw new ArgumentException("legacy json path is required", nameof(legacyJsonPath));
        _legacyJsonPath = legacyJsonPath;

        lock (_gate)
            ImportLegacyJsonIfNeeded();
    }

    /// <summary>
    /// Record one run for a job (newest first), pruning to <see cref="MaxRecordsPerJob"/>, and persist.
    /// </summary>
    public void Append(string jobId, CronRunRecord record)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("job id is required", nameof(jobId));
        if (record is null)
            throw new ArgumentNullException(nameof(record));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            // The insert and the prune are decided together and committed in ONE SaveChanges, so a crash can never
            // leave the job holding more than the cap (the atomicity regression the earlier two-save shape had).
            var entity = AddInContext(ctx, ctx.ActiveTenant!, jobId, record);
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] Append: job={jobId}, run={entity.Id}, firedUtc={record.FiredUtc:o}, infra={record.InfraStatus}, task={record.TaskStatus}, result={entity.Result}");
        }
    }

    /// <summary>
    /// Add one run to <paramref name="ctx"/> and remove what the cap lets go, WITHOUT saving: the caller's SaveChanges
    /// commits both at once. Used by <see cref="Append"/> and by the schedule store, which records the runs missed
    /// while the Gateway was down in the same write that moves those schedules on (so the evidence cannot be lost
    /// between the two). Stamps the minted id onto <paramref name="record"/>.
    /// </summary>
    internal static CronRunEntity AddInContext(GatewayDbContext ctx, string tenantId, string jobId, CronRunRecord record)
    {
        // Rows already added to this context but not yet saved count too, so several adds in one write stay ordered.
        var pending = ctx.ChangeTracker.Entries<CronRunEntity>()
            .Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToList();
        var nextSeq = Math.Max(ctx.CronRuns.Max(e => (long?)e.Sequence) ?? 0,
            pending.Count == 0 ? 0 : pending.Max(p => p.Sequence)) + 1;
        var entity = ToEntity(jobId, record, nextSeq, tenantId);
        ctx.CronRuns.Add(entity);
        record.RunId = entity.Id.ToString("D");

        var rows = ctx.CronRuns.Where(e => e.JobId == jobId).ToList()
            .Concat(pending.Where(p => p.JobId == jobId))
            .Append(entity)
            .OrderBy(e => e.Sequence)
            .ToList();
        foreach (var gone in PrunableBeyondCap(rows))
        {
            if (ctx.Entry(gone).State == EntityState.Added)
                ctx.Entry(gone).State = EntityState.Detached;
            else
                ctx.CronRuns.Remove(gone);
        }
        return entity;
    }

    /// <summary>
    /// Which of a schedule's rows (oldest first) the cap removes. The history is bounded at
    /// <see cref="MaxRecordsPerJob"/>, but a row that still MEANS something is never removed for space: a problem
    /// that is still open (unresolved, and not cleared by a later run - see <see cref="CronRunResultFold"/>: a later ok
    /// clears any problem, a later nothing-to-do clears a check problem) stays until it is cleared or resolved, and a
    /// row still owing the factory activity record its row stays until the row is written. Only the oldest rows that
    /// are neither are removed, so the history can stand above the cap while problems are open. A checking or
    /// nothing-to-do row is removed like any other closed row.
    /// </summary>
    internal static IReadOnlyList<CronRunEntity> PrunableBeyondCap(IReadOnlyList<CronRunEntity> oldestFirst)
    {
        var overflow = oldestFirst.Count - MaxRecordsPerJob;
        if (overflow <= 0) return Array.Empty<CronRunEntity>();
        var newestOk = -1;
        var newestNothingToDo = -1;
        for (var i = 0; i < oldestFirst.Count; i++)
        {
            if (oldestFirst[i].Result == CronRunResults.Ok) newestOk = i;
            if (oldestFirst[i].Result == CronRunResults.NothingToDo) newestNothingToDo = i;
        }
        bool Open(CronRunEntity row, int i) =>
            row.Result == CronRunResults.Problem && row.ResolvedUtc is null && i > newestOk
            && !(CronRunProblems.IsCheckProblem(row.Problem) && i < newestNothingToDo);
        return oldestFirst
            .Select((row, i) => (row, keep: row.ActivityDue || Open(row, i)))
            .Where(x => !x.keep)
            .Take(overflow)
            .Select(x => x.row)
            .ToList();
    }

    /// <summary>One job's runs as defensive copies, newest first; empty when the job has no runs.</summary>
    public IReadOnlyList<CronRunRecord> List(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return Array.Empty<CronRunRecord>();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.CronRuns.AsNoTracking()
                .Where(e => e.JobId == jobId)
                .OrderByDescending(e => e.Sequence)
                .ToList()
                .Select(ToRecord)
                .ToList();
        }
    }

    /// <summary>
    /// WAS THIS SESSION STARTED BY ONE OF THIS ACCOUNT'S SCHEDULES? True when any recorded fire names it.
    ///
    /// The Wingman tab's Now view asks this so that a working session can say WHO asked it - "A schedule, at
    /// 6:00 AM" rather than a bare "at 6:00 AM" - and that one word tells the owner nobody is waiting on this
    /// session. It is answered from the stored run, which is the only place the fact is recorded: a fire writes
    /// the session it started (<see cref="Contracts.CronRunRecord.SessionId"/>), so the answer is a record and
    /// never a reading of the row.
    ///
    /// <c>SessionDto.AutoDismiss</c> is NOT this fact and must not be used for it. It is a per-job OPTION about
    /// whether a run closes itself, which a hand-started session can carry and a schedule can have switched off,
    /// so it would both name schedules that are not one and miss ones that are.
    ///
    /// THE TENANT IS EXPLICIT, never ambient: the caller has already resolved which account this read is for,
    /// and a query that fell back to whatever tenant happened to be in scope is how one account answers about
    /// another's runs.
    /// </summary>
    /// <param name="tenant">The account whose runs are searched.</param>
    /// <param name="sessionId">The session to look for.</param>
    public bool StartedSession(Core.Tenancy.TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(sessionId)) return false;

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.CronRuns.AsNoTracking().Any(e => e.SessionId == sessionId);
        }
    }

    /// <summary>
    /// Record how a scheduled run's session ended, at the moment a Gateway route carried the request that ends it
    /// (the owner, 2026-10-09): the session asked to close itself, a person stopped it, or another session did. Only
    /// the route knows who asked, so it is written then rather than worked out later.
    ///
    /// The LATEST request wins, because it is the one that ended the session: a session that flagged itself and was
    /// then stopped by hand before the reaper reached it was stopped by hand. A run whose session no schedule started
    /// has no row, and this does nothing for it - which is every session that is not a scheduled one.
    /// </summary>
    /// <returns>How many runs were stamped: 0 for a session no schedule started.</returns>
    public int StampEnding(Core.Tenancy.TenantId tenant, string sessionId, string ending)
    {
        if (!CronRunEndings.IsRecorded(ending))
            throw new ArgumentException($"only a recorded ending may be stamped, not '{ending}'", nameof(ending));
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(sessionId)) return 0;

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.CronRuns.Where(e => e.SessionId == sessionId).ToList();
            foreach (var row in rows)
                row.TaskStatus = ending;
            if (rows.Count > 0)
            {
                ctx.SaveChanges();
                FileLog.Write($"[CronRunHistoryStore] StampEnding: session={sessionId}, ending={ending}, runs={rows.Count}");
            }
            return rows.Count;
        }
    }

    /// <summary>
    /// Take back a recorded ending when the request that made it is undone - a pending deletion cancelled in its
    /// grace window - so a session that is carrying on is not reported as having closed itself.
    /// </summary>
    public int ClearEnding(Core.Tenancy.TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(sessionId)) return 0;

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.CronRuns.Where(e => e.SessionId == sessionId).ToList()
                .Where(e => CronRunEndings.IsRecorded(e.TaskStatus)).ToList();
            foreach (var row in rows)
                row.TaskStatus = TaskStatusUnknown;
            if (rows.Count > 0)
            {
                ctx.SaveChanges();
                FileLog.Write($"[CronRunHistoryStore] ClearEnding: session={sessionId}, runs={rows.Count}");
            }
            return rows.Count;
        }
    }

    /// <summary>
    /// True when this schedule already has a run recorded for the time it was due - how a missed run that a failed
    /// sweep already recorded is not recorded twice when the sweep retries.
    /// </summary>
    public bool HasRunScheduledAt(string jobId, DateTime scheduledUtc)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return false;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.CronRuns.AsNoTracking().Any(e => e.JobId == jobId && e.ScheduledUtc == scheduledUtc);
        }
    }

    /// <summary>A recorded run with the schedule it belongs to, and the factory activity rows written for it.</summary>
    public sealed record JobRun(string JobId, CronRunRecord Run, Guid? ActivityId, Guid? ResolveActivityId);

    private static JobRun ToJobRun(CronRunEntity e) => new(e.JobId, ToRecord(e), e.ActivityId, e.ResolveActivityId);

    /// <summary>
    /// The scheduled run this session is: the newest run that names it. Null when no schedule of this account started
    /// the session - which is every session that is not a scheduled run.
    /// </summary>
    public JobRun? FindBySession(Core.Tenancy.TenantId tenant, string sessionId)
    {
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.AsNoTracking()
                .Where(e => e.SessionId == sessionId)
                .OrderByDescending(e => e.Sequence)
                .FirstOrDefault();
            return row is null ? null : ToJobRun(row);
        }
    }

    /// <summary>One run by its id, or null when this account has no such run.</summary>
    public JobRun? Find(Core.Tenancy.TenantId tenant, Guid runId)
    {
        if (!tenant.IsValid) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.AsNoTracking().FirstOrDefault(e => e.Id == runId);
            return row is null ? null : ToJobRun(row);
        }
    }

    /// <summary>Every run of this account still waiting on its session's report, oldest first.</summary>
    public IReadOnlyList<JobRun> PendingRuns(Core.Tenancy.TenantId tenant) =>
        RunsWhere(tenant, e => e.Result == CronRunResults.Pending);

    /// <summary>Every run of this account whose start's outcome is still not known, oldest first (review of P10).</summary>
    public IReadOnlyList<JobRun> StartUnknownRuns(Core.Tenancy.TenantId tenant) =>
        RunsWhere(tenant, e => e.Result == CronRunResults.StartUnknown && e.SessionId == null);

    /// <summary>
    /// A start whose outcome was not known turned out to have started <paramref name="sessionId"/>: the run takes the
    /// session, its status becomes <c>started</c> and its result <c>pending</c> - the run now owes its report like every
    /// started run. Applied only while the run is still start-unknown with no session, and only when no run of the
    /// account already holds <paramref name="sessionId"/> - checked under the same lock as the write, so two callers
    /// settling at once (the minute sweep and a refused report) cannot both give one session to two runs. Returns the
    /// run as it now stands, or null when it is not applied.
    /// </summary>
    public CronRunRecord? AdoptStartUnknown(Core.Tenancy.TenantId tenant, Guid runId, string sessionId, string? directorId)
    {
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId);
            if (row is null || row.Result != CronRunResults.StartUnknown || row.SessionId is not null)
            {
                FileLog.Write($"[CronRunHistoryStore] AdoptStartUnknown NOT APPLIED: run={runId}, found={row?.Result ?? "no row"}");
                return null;
            }
            if (ctx.CronRuns.AsNoTracking().Any(e => e.SessionId == sessionId))
            {
                FileLog.Write($"[CronRunHistoryStore] AdoptStartUnknown NOT APPLIED: run={runId}: session {sessionId} is already another run's");
                return null;
            }
            row.SessionId = sessionId;
            if (!string.IsNullOrWhiteSpace(directorId)) row.TargetDirectorId = directorId;
            row.InfraStatus = "started";
            row.Result = CronRunResults.Pending;
            row.ResultReason = null;
            row.ResultUtc = null;
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] AdoptStartUnknown: run={runId}, job={row.JobId}, session={sessionId}");
            return ToRecord(row);
        }
    }

    /// <summary>
    /// A start whose outcome was not known, and no session of its name appeared within the run's window: the run did not
    /// run, with <paramref name="reason"/>, and owes its factory the failed row from now. Applied only while the run is
    /// still start-unknown with no session. Returns the run as it now stands, or null when it is not.
    /// </summary>
    public CronRunRecord? LapseStartUnknown(Core.Tenancy.TenantId tenant, Guid runId, string reason, DateTime nowUtc)
    {
        if (!tenant.IsValid) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId);
            if (row is null || row.Result != CronRunResults.StartUnknown || row.SessionId is not null)
            {
                FileLog.Write($"[CronRunHistoryStore] LapseStartUnknown NOT APPLIED: run={runId}, found={row?.Result ?? "no row"}");
                return null;
            }
            row.InfraStatus = "not-started";
            row.Result = CronRunResults.Problem;
            row.Problem = CronRunProblems.DidNotRun;
            row.ResultReason = TruncateReason(reason);
            row.ResultUtc = nowUtc;
            row.ActivityDue = true;
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] LapseStartUnknown: run={runId}, job={row.JobId}: {row.ResultReason}");
            return ToRecord(row);
        }
    }

    /// <summary>Every run of this account that still owes the factory activity record a row, oldest first.</summary>
    public IReadOnlyList<JobRun> ActivityDueRuns(Core.Tenancy.TenantId tenant) =>
        RunsWhere(tenant, e => e.ActivityDue);

    private IReadOnlyList<JobRun> RunsWhere(Core.Tenancy.TenantId tenant, System.Linq.Expressions.Expression<Func<CronRunEntity, bool>> where)
    {
        if (!tenant.IsValid) return Array.Empty<JobRun>();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.CronRuns.AsNoTracking().Where(where).OrderBy(e => e.Sequence).ToList().Select(ToJobRun).ToList();
        }
    }

    /// <summary>
    /// Every run of this account, grouped by schedule, newest first - what the problems fold reads. Bounded by the
    /// per-schedule cap <see cref="Append"/> keeps, plus the problems still open that the cap never removes.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> AllByJob(Core.Tenancy.TenantId tenant)
    {
        if (!tenant.IsValid) return new Dictionary<string, IReadOnlyList<CronRunRecord>>(StringComparer.Ordinal);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.CronRuns.AsNoTracking()
                .OrderByDescending(e => e.Sequence)
                .ToList()
                .GroupBy(e => e.JobId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<CronRunRecord>)g.Select(ToRecord).ToList(), StringComparer.Ordinal);
        }
    }

    /// <summary>What a conditional write found: whether it was applied, and the run as it now stands (null: no such run).</summary>
    public sealed record ResultWrite(bool Applied, CronRunRecord? Run);

    /// <summary>
    /// Write how a run went onto it - ONLY when the run is still in the state the caller read
    /// (<paramref name="expectedResult"/>, and <paramref name="expectedReason"/> when given). The check and the write
    /// happen under this store's lock, so a session's report and the sweep's "did not report" cannot both land: the
    /// second finds the state moved on and is not applied. <paramref name="problem"/> is null for an ok result.
    /// The same write marks the run as owing its factory activity row.
    /// </summary>
    public ResultWrite RecordResult(Core.Tenancy.TenantId tenant, Guid runId, string expectedResult, string? expectedReason,
        string result, string? problem, string? reason, DateTime nowUtc)
    {
        if (result is not (CronRunResults.Ok or CronRunResults.Problem))
            throw new ArgumentException($"only ok or problem may be recorded, not '{result}'", nameof(result));
        if (result == CronRunResults.Problem && string.IsNullOrWhiteSpace(problem))
            throw new ArgumentException("a problem needs its kind", nameof(problem));
        if (!tenant.IsValid) return new ResultWrite(false, null);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId);
            if (row is null) return new ResultWrite(false, null);
            if (row.Result != expectedResult || (expectedReason is not null && row.ResultReason != expectedReason))
            {
                FileLog.Write($"[CronRunHistoryStore] RecordResult NOT APPLIED: run={runId}, expected={expectedResult}, found={row.Result}");
                return new ResultWrite(false, ToRecord(row));
            }
            row.Result = result;
            row.Problem = result == CronRunResults.Problem ? problem : null;
            row.ResultReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            // When the result was first recorded is when a problem was raised. A later write only adds to it (a session's
            // report on a run already recorded as past its shift), so it keeps that time.
            if (expectedResult is CronRunResults.Pending or CronRunResults.Untracked || row.ResultUtc is null)
                row.ResultUtc = nowUtc;
            row.ActivityDue = row.ActivityDue || row.ActivityId is null;
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] RecordResult: run={runId}, job={row.JobId}, result={result}, problem={row.Problem ?? "-"}");
            return new ResultWrite(true, ToRecord(row));
        }
    }

    /// <summary>
    /// Mark a run's problem resolved by hand, keeping who did it and why - only when it is a problem not already
    /// resolved. The same write marks the run as owing the correcting factory activity row.
    /// </summary>
    public ResultWrite Resolve(Core.Tenancy.TenantId tenant, Guid runId, string resolvedBy, string reason, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(resolvedBy)) throw new ArgumentException("who resolved it is required", nameof(resolvedBy));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("a reason is required", nameof(reason));
        if (!tenant.IsValid) return new ResultWrite(false, null);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId);
            if (row is null) return new ResultWrite(false, null);
            if (row.Result != CronRunResults.Problem || row.ResolvedUtc is not null)
                return new ResultWrite(false, ToRecord(row));
            row.ResolvedUtc = nowUtc;
            row.ResolvedBy = resolvedBy.Trim();
            row.ResolvedReason = reason.Trim();
            row.ActivityDue = true;
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] Resolve: run={runId}, job={row.JobId}, by={row.ResolvedBy}");
            return new ResultWrite(true, ToRecord(row));
        }
    }

    /// <summary>
    /// Clear what a run in NO factory is marked as owing: it has no factory activity record to write to. A run in a
    /// factory is settled only by <see cref="WriteActivityRow"/>, in the write that records its row.
    /// </summary>
    public void SettleInNoFactory(Core.Tenancy.TenantId tenant, Guid runId)
    {
        if (!tenant.IsValid) throw new ArgumentException("a valid account is required", nameof(tenant));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId)
                ?? throw new InvalidOperationException($"there is no run {runId} to settle the activity of");
            if (row.Factory is not null)
                throw new InvalidOperationException($"run {runId} belongs to factory {row.Factory}; its rows are owed, not settled away");
            row.ActivityDue = false;
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] SettleInNoFactory: run={runId}");
        }
    }

    /// <summary>
    /// Write one factory activity row a run owes - its result row (<paramref name="correction"/> false) or the row
    /// that marks its resolved problem handled (true) - in the SAME database write that notes it on the run. The row
    /// and the note land together or not at all, so no failure can leave a written row still marked as owed, and a
    /// retry can never write it twice: a run whose row is already noted answers with that row and writes nothing.
    /// Throws when the row is refused or the write fails; the run then still owes it.
    /// </summary>
    public Guid WriteActivityRow(Core.Tenancy.TenantId tenant, Guid runId, AppendFactoryActivityRequest request, bool correction)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!tenant.IsValid) throw new ArgumentException("a valid account is required", nameof(tenant));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId)
                ?? throw new InvalidOperationException($"there is no run {runId} to write the factory activity row of");
            if ((correction ? row.ResolveActivityId : row.ActivityId) is { } already)
            {
                FileLog.Write($"[CronRunHistoryStore] WriteActivityRow: run={runId}, correction={correction} already written as {already}");
                return already;
            }
            var activity = Factory.FactoryActivityRecord.NewRow(ctx, request, callingActor: null, DateTime.UtcNow);
            ctx.FactoryActivity.Add(activity);
            if (correction) row.ResolveActivityId = activity.Id;
            else row.ActivityId = activity.Id;
            row.ActivityDue = OwesActivity(row);
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] WriteActivityRow: run={runId}, correction={correction}, row={activity.Id}, due={row.ActivityDue}");
            return activity.Id;
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Factory Definition, phase 5b: the due check of a schedule with a check. A fire of such a schedule records a run
    // whose result is "checking"; that row IS the due check. Every write below is conditional on the row still being
    // in the state the caller read, under this store's lock, so two reports of one check can never both land.

    /// <summary>
    /// A DUE CHECK NOBODY REPORTED BEFORE ITS NEXT TIME (phase 5b, design 3.3): the newest run of the job still waiting on
    /// its check and NOT yet reported (<c>CheckedUtc</c> null) becomes a <c>check-not-run</c> problem, in one write under
    /// the store's lock - so a report that decides it at the same moment either wins or is refused as already recorded,
    /// never both. A run whose check was reported and whose session is being started is left alone: its start is the
    /// one writer that finishes it (<see cref="FinishDueStart"/>). <paramref name="reasonFor"/> words the reason from
    /// the run's infrastructure status. Returns the superseded run, or null when none was waiting.
    /// </summary>
    public CronRunRecord? SupersedeUnreportedCheck(Core.Tenancy.TenantId tenant, string jobId, Func<string, string> reasonFor,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(reasonFor);
        FileLog.Write($"[CronRunHistoryStore] SupersedeUnreportedCheck: job={jobId}");
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(jobId)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns
                .Where(e => e.JobId == jobId && e.Result == CronRunResults.Checking && e.CheckedUtc == null)
                .OrderByDescending(e => e.Sequence)
                .FirstOrDefault();
            if (row is null) return null;
            row.Result = CronRunResults.Problem;
            row.Problem = CronRunProblems.CheckNotRun;
            row.ResultReason = TruncateReason(reasonFor(row.InfraStatus));
            row.ResultUtc = nowUtc;
            row.ActivityDue = row.ActivityDue || row.ActivityId is null;
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] SupersedeUnreportedCheck: run={row.Id}, job={jobId}: {row.ResultReason}");
            return ToRecord(row);
        }
    }

    /// <summary>A run still waiting on its check, with who holds its claim.</summary>
    public sealed record DueCheck(string JobId, CronRunRecord Run, string? ClaimedByDirectorId, DateTime? ClaimedUtc);

    /// <summary>
    /// Every run of this account on <paramref name="machine"/> still waiting on its check and not yet decided, that is
    /// unclaimed, claimed by <paramref name="directorId"/>, or whose claim is older than <paramref name="claimLapse"/> -
    /// and CLAIMS each for that Director, in one write. A run whose check is already being decided (its report was read
    /// and its session is being started) is never handed out again. Oldest first.
    /// </summary>
    public IReadOnlyList<DueCheck> ClaimDueChecks(Core.Tenancy.TenantId tenant, string directorId, string machine,
        DateTime nowUtc, TimeSpan claimLapse)
    {
        if (string.IsNullOrWhiteSpace(directorId)) throw new ArgumentException("a Director id is required", nameof(directorId));
        FileLog.Write($"[CronRunHistoryStore] ClaimDueChecks: director={directorId}, machine={machine}");
        if (!tenant.IsValid) return Array.Empty<DueCheck>();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var lapsedBefore = nowUtc - claimLapse;
            // The machine is compared ignoring case, in memory, as the trigger store and the Director lookup compare it:
            // a seat written "soren_north" runs on the Director registered as SOREN_NORTH.
            var rows = ctx.CronRuns
                .Where(e => e.Result == CronRunResults.Checking && e.CheckedUtc == null)
                .OrderBy(e => e.Sequence)
                .ToList()
                .Where(e => string.Equals(e.Machine, machine, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.ClaimedByDirectorId is null || e.ClaimedByDirectorId == directorId
                            || e.ClaimedUtc is null || e.ClaimedUtc.Value < lapsedBefore)
                .ToList();
            foreach (var row in rows)
            {
                if (row.ClaimedByDirectorId != directorId)
                    FileLog.Write($"[CronRunHistoryStore] ClaimDueChecks: run={row.Id}, job={row.JobId}, claimed by {directorId}" +
                                  (row.ClaimedByDirectorId is null ? "" : $" (the claim of {row.ClaimedByDirectorId} lapsed)"));
                row.ClaimedByDirectorId = directorId;
                row.ClaimedUtc = nowUtc;
            }
            if (rows.Count > 0) ctx.SaveChanges();
            return rows.Select(r => new DueCheck(r.JobId, ToRecord(r), r.ClaimedByDirectorId, SpecifyUtc(r.ClaimedUtc))).ToList();
        }
    }

    /// <summary>A run with its claim, by id, or null when this account has no such run.</summary>
    public DueCheck? FindDueCheck(Core.Tenancy.TenantId tenant, Guid runId)
    {
        FileLog.Write($"[CronRunHistoryStore] FindDueCheck: run={runId}");
        if (!tenant.IsValid) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.AsNoTracking().FirstOrDefault(e => e.Id == runId);
            return row is null ? null : new DueCheck(row.JobId, ToRecord(row), row.ClaimedByDirectorId, SpecifyUtc(row.ClaimedUtc));
        }
    }

    /// <summary>What a conditional write to a due check found.</summary>
    public enum DueCheckWrite
    {
        /// <summary>Written.</summary>
        Applied,
        /// <summary>No such run in this account.</summary>
        NoSuchRun,
        /// <summary>The run is no longer waiting on its check, or its check was already decided.</summary>
        AlreadyRecorded,
        /// <summary>Another Director holds the run's check.</summary>
        HeldByAnother,
    }

    /// <summary>
    /// Record what a due check's report came to when it starts no session: <c>nothing-to-do</c>, or a problem
    /// (<c>check-failed</c>). Applied only while the run is still waiting on its check, undecided, and held by
    /// <paramref name="directorId"/>. The same write marks the run as owing its factory activity row.
    /// </summary>
    public DueCheckWrite RecordDueCheck(Core.Tenancy.TenantId tenant, Guid runId, string directorId, string result,
        string? problem, string? reason, int? count, string infraStatus, DateTime nowUtc)
    {
        if (result is not (CronRunResults.NothingToDo or CronRunResults.Problem))
            throw new ArgumentException($"a due check's report records nothing-to-do or a problem, not '{result}'", nameof(result));
        if (result == CronRunResults.Problem && string.IsNullOrWhiteSpace(problem))
            throw new ArgumentException("a problem needs its kind", nameof(problem));
        return WriteUndecidedDueCheck(tenant, runId, directorId, row =>
        {
            row.Result = result;
            row.Problem = result == CronRunResults.Problem ? problem : null;
            row.ResultReason = string.IsNullOrWhiteSpace(reason) ? null : TruncateReason(reason.Trim());
            row.ResultUtc = nowUtc;
            row.CheckCount = count;
            row.CheckedUtc = nowUtc;
            row.InfraStatus = infraStatus;
            row.TargetDirectorId = directorId;
            row.ActivityDue = true;
        });
    }

    /// <summary>The infrastructure status of a due check whose check counted work while its session is being started.</summary>
    public const string CheckStartingStatus = "check-starting";

    /// <summary>The infrastructure status of a due check whose session start never returned (<see cref="FailStalledDueStarts"/>).</summary>
    public const string CheckStartLostStatus = "check-start-lost";

    /// <summary>
    /// How long a decided due check's session start may take before its run is recorded as not started: the same
    /// grace a trigger's start has (<see cref="Factory.Triggers.TriggerService.StartGrace"/>).
    /// </summary>
    public static readonly TimeSpan DueStartGrace = Factory.Triggers.TriggerService.StartGrace;

    /// <summary>
    /// A DECIDED DUE CHECK WHOSE START NEVER RETURNED (phase 5b, review 2, finding 3): its check counted work and its
    /// session start began, but the start never came back - the Gateway stopped in between, or the start hung - so
    /// nothing else would ever end it. Every such run of the account (still checking, its check reported, no session)
    /// decided before <paramref name="decidedBeforeUtc"/> becomes a did-not-run problem that says so, in one write.
    /// Returns those runs. A start that does come back later still writes its session onto the run
    /// (<see cref="FinishDueStart"/>), so the record never denies a session that exists.
    /// </summary>
    public IReadOnlyList<JobRun> FailStalledDueStarts(Core.Tenancy.TenantId tenant, DateTime decidedBeforeUtc, DateTime nowUtc)
    {
        FileLog.Write($"[CronRunHistoryStore] FailStalledDueStarts: decided before {decidedBeforeUtc:o}");
        if (!tenant.IsValid) return Array.Empty<JobRun>();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.CronRuns
                .Where(e => e.Result == CronRunResults.Checking && e.CheckedUtc != null && e.SessionId == null)
                .ToList()
                .Where(e => SpecifyUtc(e.CheckedUtc)!.Value < decidedBeforeUtc)
                .ToList();
            foreach (var row in rows)
            {
                var counted = row.CheckCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "work";
                row.Result = CronRunResults.Problem;
                row.Problem = CronRunProblems.DidNotRun;
                row.ResultReason = TruncateReason(
                    $"its check counted {counted} at {SpecifyUtc(row.CheckedUtc):yyyy-MM-dd HH:mm} UTC, and the session it asked for had not started by {nowUtc:yyyy-MM-dd HH:mm} UTC");
                row.ResultUtc = nowUtc;
                row.InfraStatus = CheckStartLostStatus;
                row.ActivityDue = row.ActivityDue || row.ActivityId is null;
                FileLog.Write($"[CronRunHistoryStore] FailStalledDueStarts: run={row.Id}, job={row.JobId}: {row.ResultReason}");
            }
            if (rows.Count > 0) ctx.SaveChanges();
            return rows.Select(ToJobRun).ToList();
        }
    }

    /// <summary>
    /// Mark a due check DECIDED: its check counted work and a session start is beginning. The run stays
    /// <c>checking</c> until the start returns (<see cref="FinishDueStart"/>), but it is no longer handed out and a
    /// second report of it is refused as already recorded.
    /// </summary>
    public DueCheckWrite BeginDueStart(Core.Tenancy.TenantId tenant, Guid runId, string directorId, int count, DateTime nowUtc)
        => WriteUndecidedDueCheck(tenant, runId, directorId, row =>
        {
            row.CheckCount = count;
            row.CheckedUtc = nowUtc;
            row.TargetDirectorId = directorId;
            row.InfraStatus = CheckStartingStatus;
        });

    /// <summary>
    /// Record how a decided due check's session start came out: <c>pending</c> with its session (the run now owes its
    /// report, as every started run does), or a <c>did-not-run</c> problem with <paramref name="failure"/>. Returns the
    /// run as it now stands, or null when the row is gone or is no longer a decided due check.
    /// </summary>
    public CronRunRecord? FinishDueStart(Core.Tenancy.TenantId tenant, Guid runId, string directorId, Running.CronRunStart start,
        string? failure, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (!tenant.IsValid) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId);
            if (row is not null && row.InfraStatus == CheckStartLostStatus && row.SessionId is null
                && !string.IsNullOrEmpty(start.SessionId))
            {
                // The start came back after its run was recorded as not started: the problem stands, and the run names the
                // session that did start, so the record never denies it.
                row.SessionId = start.SessionId;
                row.TargetDirectorId = start.DirectorId ?? directorId;
                row.ResultReason = TruncateReason($"{row.ResultReason} - session {start.SessionId} started later, at {nowUtc:yyyy-MM-dd HH:mm} UTC");
                ctx.SaveChanges();
                FileLog.Write($"[CronRunHistoryStore] FinishDueStart: run={runId} was recorded as not started; its late session {start.SessionId} is written onto it");
                return ToRecord(row);
            }
            if (row is null || row.Result != CronRunResults.Checking || row.CheckedUtc is null)
            {
                FileLog.Write($"[CronRunHistoryStore] FinishDueStart NOT APPLIED: run={runId}, found={row?.Result ?? "no row"}");
                return null;
            }
            // The fire time is kept: the run fired when its schedule said; the start's time is its CheckedUtc (when the
            // report decided it) and, for a start that failed, its ResultUtc.
            row.TargetDirectorId = start.DirectorId ?? directorId;
            if (!string.IsNullOrEmpty(start.SessionId))
            {
                row.Result = CronRunResults.Pending;
                row.SessionId = start.SessionId;
                row.InfraStatus = "started";
                row.BriefVersion = start.BriefVersion;
                row.BriefVersionKind = start.BriefVersionKind;
            }
            else
            {
                row.Result = CronRunResults.Problem;
                row.Problem = CronRunProblems.DidNotRun;
                row.ResultReason = TruncateReason(failure ?? "it did not start a session");
                row.ResultUtc = nowUtc;
                row.InfraStatus = "not-started";
                row.ActivityDue = true;
            }
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] FinishDueStart: run={runId}, job={row.JobId}, result={row.Result}, session={row.SessionId ?? "none"}");
            return ToRecord(row);
        }
    }

    private DueCheckWrite WriteUndecidedDueCheck(Core.Tenancy.TenantId tenant, Guid runId, string directorId,
        Action<CronRunEntity> write)
    {
        if (!tenant.IsValid) return DueCheckWrite.NoSuchRun;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId);
            if (row is null) return DueCheckWrite.NoSuchRun;
            if (row.Result != CronRunResults.Checking || row.CheckedUtc is not null)
                return DueCheckWrite.AlreadyRecorded;
            // Only the Director the check was handed to reports it: a report from any other is refused, as a trigger's is.
            if (row.ClaimedByDirectorId != directorId)
                return DueCheckWrite.HeldByAnother;
            write(row);
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] WriteUndecidedDueCheck: run={runId}, job={row.JobId}, director={directorId}, result={row.Result}, count={row.CheckCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
            return DueCheckWrite.Applied;
        }
    }

    private static string TruncateReason(string text) =>
        text.Length <= Running.CronRunResultFold.MaxReasonChars ? text : text[..(Running.CronRunResultFold.MaxReasonChars - 3)] + "...";

    // A run still owes a row while its result row, or - once resolved - its correcting row, is unwritten.
    private static bool OwesActivity(CronRunEntity row) =>
        ((row.Result is CronRunResults.Ok or CronRunResults.Problem or CronRunResults.NothingToDo) && row.ActivityId is null)
        || (row.ResolvedUtc is not null && row.ResolveActivityId is null);

    /// <summary>The task status a fire writes, before anything is known about how the work ended.</summary>
    public const string TaskStatusUnknown = "unknown";

    /// <summary>
    /// The newest <paramref name="perJob"/> runs of each of <paramref name="jobIds"/>, newest first, in ONE query - what
    /// the schedule list folds each job's run record from on every poll, so it must not cost a query per job. Only the
    /// listed jobs are read: run history outlives a deleted schedule, and its rows must not ride along on every poll.
    /// What is read is bounded by the per-job cap <see cref="Append"/> keeps (<see cref="MaxRecordsPerJob"/>).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> RecentByJob(IReadOnlyCollection<string> jobIds, int perJob)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        if (perJob < 1) throw new ArgumentOutOfRangeException(nameof(perJob), perJob, "perJob must be at least 1");
        if (jobIds.Count == 0) return new Dictionary<string, IReadOnlyList<CronRunRecord>>(StringComparer.Ordinal);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return RecentIn(ctx, jobIds, perJob);
        }
    }

    /// <summary>
    /// <see cref="RecentByJob(IReadOnlyCollection{string}, int)"/> in the account a route resolved EXPLICITLY - the
    /// Factories screen's seat markers - never the ambient one.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CronRunRecord>> RecentByJob(Core.Tenancy.TenantId tenant, IReadOnlyCollection<string> jobIds, int perJob)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        if (perJob < 1) throw new ArgumentOutOfRangeException(nameof(perJob), perJob, "perJob must be at least 1");
        if (jobIds.Count == 0) return new Dictionary<string, IReadOnlyList<CronRunRecord>>(StringComparer.Ordinal);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return RecentIn(ctx, jobIds, perJob);
        }
    }

    private static Dictionary<string, IReadOnlyList<CronRunRecord>> RecentIn(GatewayDbContext ctx, IReadOnlyCollection<string> jobIds, int perJob) =>
        ctx.CronRuns.AsNoTracking()
            .Where(e => jobIds.Contains(e.JobId))
            .OrderByDescending(e => e.Sequence)
            .ToList()
            .GroupBy(e => e.JobId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key,
                g => (IReadOnlyList<CronRunRecord>)g.Take(perJob).Select(ToRecord).ToList(),
                StringComparer.Ordinal);

    private static CronRunEntity ToEntity(string jobId, CronRunRecord r, long sequence, string tenantId) => new()
    {
        JobId = jobId,
        Sequence = sequence,
        TenantId = tenantId,
        ScheduledUtc = r.ScheduledUtc,
        FiredUtc = r.FiredUtc,
        Machine = r.Machine,
        TargetDirectorId = r.TargetDirectorId,
        SessionId = r.SessionId,
        InfraStatus = r.InfraStatus,
        TaskStatus = r.TaskStatus,
        Result = string.IsNullOrWhiteSpace(r.Result) ? CronRunResults.Untracked : r.Result,
        Problem = r.Problem,
        ResultReason = r.ResultReason,
        ResultUtc = r.ResultUtc,
        // A run recorded WITH its result (a run that did not run) owes its factory activity row from the start.
        ActivityDue = r.Result is CronRunResults.Ok or CronRunResults.Problem or CronRunResults.NothingToDo,
        Factory = r.Factory,
        Seat = r.Seat,
        JobName = r.JobName,
        MissedNotListed = r.MissedNotListed,
        BriefVersion = r.BriefVersion,
        BriefVersionKind = r.BriefVersionKind,
        SessionName = r.SessionName,
    };

    /// <summary>True when the schedule belongs to a factory and runs as one of its seats.</summary>
    public static bool IsFactoryLinked(CronJobDto? job) =>
        job is not null && !string.IsNullOrWhiteSpace(job.Factory) && !string.IsNullOrWhiteSpace(job.Seat);

    private static CronRunRecord ToRecord(CronRunEntity e) => new()
    {
        RunId = e.Id.ToString("D"),
        ScheduledUtc = e.ScheduledUtc,
        FiredUtc = e.FiredUtc,
        Machine = e.Machine,
        TargetDirectorId = e.TargetDirectorId,
        SessionId = e.SessionId,
        InfraStatus = e.InfraStatus,
        TaskStatus = e.TaskStatus,
        Result = e.Result,
        Problem = e.Problem,
        ResultReason = e.ResultReason,
        ResultUtc = SpecifyUtc(e.ResultUtc),
        ResolvedUtc = SpecifyUtc(e.ResolvedUtc),
        ResolvedBy = e.ResolvedBy,
        ResolvedReason = e.ResolvedReason,
        Factory = e.Factory,
        Seat = e.Seat,
        JobName = e.JobName,
        MissedNotListed = e.MissedNotListed,
        BriefVersion = e.BriefVersion,
        BriefVersionKind = e.BriefVersionKind,
        SessionName = e.SessionName,
        CheckCount = e.CheckCount,
        CheckedUtc = SpecifyUtc(e.CheckedUtc),
    };

    private static DateTime? SpecifyUtc(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    // ---- one-time legacy JSON import --------------------------------------------------------------

    private sealed class StoreFile
    {
        public Dictionary<string, List<CronRunRecord>> Runs { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Import a legacy <c>cronruns.json</c> exactly once: only when it exists AND the table is empty. Each
    /// job's list is newest-first; it is capped at <see cref="MaxRecordsPerJob"/> and inserted so that the
    /// newest record gets the highest <see cref="CronRunEntity.Sequence"/>, preserving the exact newest-first
    /// order. All inside one transaction, then the JSON is renamed aside. Fail-loud and all-or-nothing.
    /// </summary>
    private void ImportLegacyJsonIfNeeded()
        => LegacyJsonImport.Recoverable(
            _legacyJsonPath,
            "[CronRunHistoryStore]",
            isPopulated: () => { using var ctx = _db.CreateContext(); return ctx.CronRuns.Any(); },
            importCommitted: ImportRowsFromLegacyJson);

    /// <summary>
    /// Parse the legacy file and insert every run inside one transaction. Fail-loud and all-or-nothing.
    /// Called by the recoverable-import plumbing only when the file exists and the table is empty; the
    /// plumbing renames the file aside after this returns.
    /// </summary>
    private void ImportRowsFromLegacyJson()
    {
        using var ctx = _db.CreateContext();

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_legacyJsonPath), FileJsonOptions);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CronRunHistoryStore] Import FAILED: legacy file {_legacyJsonPath} could not be read: {ex.Message}");
            throw new InvalidOperationException(
                $"The legacy cron run-history file '{_legacyJsonPath}' could not be parsed for the one-time " +
                $"import: {ex.Message}. The Gateway will not start with a partial import. Fix or move the file " +
                "aside and restart.", ex);
        }

        var runsByJob = parsed?.Runs ?? new Dictionary<string, List<CronRunRecord>>(StringComparer.Ordinal);

        long seq = 0;
        var imported = 0;
        using var tx = ctx.Database.BeginTransaction();
        foreach (var (jobId, list) in runsByJob)
        {
            if (string.IsNullOrWhiteSpace(jobId) || list is null)
                continue;

            // The list is newest-first and capped at the per-job max; keep the newest ones. Insert
            // oldest-first so the newest record ends up with the highest Sequence (newest-first on read).
            var capped = list.Take(MaxRecordsPerJob).ToList();
            for (var i = capped.Count - 1; i >= 0; i--)
            {
                ctx.CronRuns.Add(ToEntity(jobId, capped[i], ++seq, ctx.ActiveTenant!));
                imported++;
            }
        }
        ctx.SaveChanges();
        tx.Commit();

        FileLog.Write($"[CronRunHistoryStore] Import: {imported} run(s) across {runsByJob.Count} job(s) imported from {_legacyJsonPath}");
    }
}
