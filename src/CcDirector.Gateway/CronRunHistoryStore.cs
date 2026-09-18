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
            ctx.CronRuns.Add(ToEntity(jobId, record, nextSeq, ctx.ActiveTenant!));

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
    };

    private static CronRunRecord ToRecord(CronRunEntity e) => new()
    {
        ScheduledUtc = e.ScheduledUtc,
        FiredUtc = e.FiredUtc,
        Machine = e.Machine,
        TargetDirectorId = e.TargetDirectorId,
        SessionId = e.SessionId,
        InfraStatus = e.InfraStatus,
        TaskStatus = e.TaskStatus,
    };

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
