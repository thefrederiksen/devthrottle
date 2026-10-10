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

            // Read the job's current rows (oldest-first) so the insert and the prune are decided together
            // and committed in ONE SaveChanges - the whole capped list moves as a single durable state,
            // exactly like the old JSON store's single whole-list rewrite. There is no intermediate save, so
            // a crash or failure can never leave the job holding more than the cap. Two separate SaveChanges
            // (the earlier shape) could persist the insert and then die before the prune - that is the
            // atomicity regression this closes.
            var existing = ctx.CronRuns
                .Where(e => e.JobId == jobId)
                .OrderBy(e => e.Sequence)
                .ToList();

            var nextSeq = (ctx.CronRuns.Max(e => (long?)e.Sequence) ?? 0) + 1;
            var entity = ToEntity(jobId, record, nextSeq, ctx.ActiveTenant!);
            ctx.CronRuns.Add(entity);
            record.RunId = entity.Id.ToString("D");

            // Prune the oldest beyond the cap (lowest Sequence = oldest) in the SAME change set as the insert.
            var overflow = existing.Count + 1 - MaxRecordsPerJob;
            if (overflow > 0)
                ctx.CronRuns.RemoveRange(existing.Take(overflow));

            ctx.SaveChanges();

            var kept = Math.Min(existing.Count + 1, MaxRecordsPerJob);
            FileLog.Write($"[CronRunHistoryStore] Append: job={jobId}, firedUtc={record.FiredUtc:o}, infra={record.InfraStatus}, task={record.TaskStatus}, count={kept}");
        }
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

    /// <summary>A recorded run with the schedule it belongs to.</summary>
    public sealed record JobRun(string JobId, CronRunRecord Run, Guid? ProblemActivityId);

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
            return row is null ? null : new JobRun(row.JobId, ToRecord(row), row.ProblemActivityId);
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
            return row is null ? null : new JobRun(row.JobId, ToRecord(row), row.ProblemActivityId);
        }
    }

    /// <summary>Every run of this account still waiting on its session's report, oldest first.</summary>
    public IReadOnlyList<JobRun> PendingRuns(Core.Tenancy.TenantId tenant)
    {
        if (!tenant.IsValid) return Array.Empty<JobRun>();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.CronRuns.AsNoTracking()
                .Where(e => e.Result == CronRunResults.Pending)
                .OrderBy(e => e.Sequence)
                .ToList()
                .Select(e => new JobRun(e.JobId, ToRecord(e), e.ProblemActivityId))
                .ToList();
        }
    }

    /// <summary>
    /// Every run of this account, grouped by schedule, newest first - what the problems fold reads. Bounded by the
    /// per-schedule cap <see cref="Append"/> keeps.
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

    /// <summary>
    /// Write how a run went onto it. <paramref name="problem"/> is null for an ok result. Returns the run as stored, or
    /// null when this account has no such run.
    /// </summary>
    public CronRunRecord? RecordResult(Core.Tenancy.TenantId tenant, Guid runId, string result, string? problem, string? reason, DateTime nowUtc)
    {
        if (result is not (CronRunResults.Ok or CronRunResults.Problem))
            throw new ArgumentException($"only ok or problem may be recorded, not '{result}'", nameof(result));
        if (result == CronRunResults.Problem && string.IsNullOrWhiteSpace(problem))
            throw new ArgumentException("a problem needs its kind", nameof(problem));
        if (!tenant.IsValid) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId);
            if (row is null) return null;
            row.Result = result;
            row.Problem = result == CronRunResults.Problem ? problem : null;
            row.ResultReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            row.ResultUtc = nowUtc;
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] RecordResult: run={runId}, job={row.JobId}, result={result}, problem={row.Problem ?? "-"}");
            return ToRecord(row);
        }
    }

    /// <summary>Remember the factory activity row that recorded a run's problem, so resolving it can mark that row handled.</summary>
    public void SetProblemActivity(Core.Tenancy.TenantId tenant, Guid runId, Guid activityRowId)
    {
        if (!tenant.IsValid) return;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId)
                ?? throw new InvalidOperationException($"there is no run {runId} to link to activity row {activityRowId}");
            row.ProblemActivityId = activityRowId;
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] SetProblemActivity: run={runId}, activity={activityRowId}");
        }
    }

    /// <summary>Mark a run's problem resolved by hand, keeping who did it and why. Returns the run as stored.</summary>
    public CronRunRecord? Resolve(Core.Tenancy.TenantId tenant, Guid runId, string resolvedBy, string reason, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(resolvedBy)) throw new ArgumentException("who resolved it is required", nameof(resolvedBy));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("a reason is required", nameof(reason));
        if (!tenant.IsValid) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.CronRuns.FirstOrDefault(e => e.Id == runId);
            if (row is null) return null;
            row.ResolvedUtc = nowUtc;
            row.ResolvedBy = resolvedBy.Trim();
            row.ResolvedReason = reason.Trim();
            ctx.SaveChanges();
            FileLog.Write($"[CronRunHistoryStore] Resolve: run={runId}, job={row.JobId}, by={row.ResolvedBy}");
            return ToRecord(row);
        }
    }

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
            return ctx.CronRuns.AsNoTracking()
                .Where(e => jobIds.Contains(e.JobId))
                .OrderByDescending(e => e.Sequence)
                .ToList()
                .GroupBy(e => e.JobId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key,
                    g => (IReadOnlyList<CronRunRecord>)g.Take(perJob).Select(ToRecord).ToList(),
                    StringComparer.Ordinal);
        }
    }

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
    };

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
