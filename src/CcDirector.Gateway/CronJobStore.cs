using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway;

/// <summary>
/// The Gateway's cron-job definition store (epic #479, part 1 = issue #482). Holds cron-job definitions
/// keyed by id and serves the REST CRUD surface; it does NOT fire jobs (the background engine is part 2,
/// issue #483).
///
/// PERSISTENCE (Hosted Gateway mission, Step 1b): definitions live in the EF data layer's <c>cron_jobs</c>
/// table (SQLite locally), NOT the old hand-rolled <c>cronjobs.json</c>. The public API and observable
/// behavior are unchanged - every mutation writes through immediately, and a fresh store instance over the
/// same database reads the same jobs, exactly as a new Gateway process does. On construction each loaded
/// job's <see cref="CronJobDto.NextRunUtc"/> is RECOMPUTED from its schedule (the wall clock moved on while
/// the Gateway was down) and persisted, matching the previous store.
///
/// ONE-TIME IMPORT: on first run after the upgrade, if a legacy <c>cronjobs.json</c> exists and the table
/// is empty, every job is imported inside one transaction and the JSON file is renamed aside
/// (<c>.migrated-&lt;UTCstamp&gt;</c>) so it is never re-imported and stays on disk as a backup. The import
/// is fail-loud and all-or-nothing: on any error it throws and imports nothing, so no local user loses data
/// or gets a partial import. A corrupt legacy file therefore fails the Gateway loudly rather than being
/// silently quarantined - the fail-loud contract of the EF data layer, matching GatewayStatsDatabase.
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over a
/// fresh pooled context, preserving the single-writer invariant.
/// </summary>
public sealed class CronJobStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly string _legacyJsonPath;

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <param name="legacyJsonPath">
    /// The legacy <c>cronjobs.json</c> path to import ONCE if it exists and the table is empty. REQUIRED so
    /// no caller silently lands on the real user's file: production (<see cref="GatewayHost"/>) passes the
    /// Gateway data dir path; tests pass an isolated temp path (usually nonexistent, so no import).
    /// </param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    /// <exception cref="ArgumentException">The legacy path is null/empty/whitespace.</exception>
    /// <param name="deferInitialize">
    /// When true the constructor validates arguments and stops; the caller must call
    /// <see cref="Initialize"/> once the database is open. The Gateway passes true so its listener can bind
    /// BEFORE any database work - the load below used to sit in front of the bind, and a slow database
    /// therefore delayed it past the platform's container-start deadline (#2383, #2585).
    ///
    /// The caller MUST run Initialize inside the same ambient tenant scope the constructor would have had.
    /// Nothing is served in the meantime: the readiness gate refuses every request but /healthz.
    /// </param>
    public CronJobStore(GatewayDatabase db, string legacyJsonPath, bool deferInitialize = false)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrWhiteSpace(legacyJsonPath))
            throw new ArgumentException("legacy json path is required", nameof(legacyJsonPath));
        _legacyJsonPath = legacyJsonPath;

        if (!deferInitialize)
            InitializeCore();
    }

    /// <summary>
    /// Run the deferred load. Idempotent, and a no-op for an instance whose constructor already did it.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        InitializeCore();
    }

    /// <summary>True once the load has run.</summary>
    public bool IsInitialized => _initialized;

    private bool _initialized;

    private void InitializeCore()
    {
        lock (_gate)
        {
            ImportLegacyJsonIfNeeded();
            RecomputeNextRunOnLoad();
        }
        _initialized = true;
    }


    /// <summary>
    /// Create a job from a validated definition. Mints an id, stamps <see cref="CronJobDto.CreatedUtc"/>,
    /// computes <see cref="CronJobDto.NextRunUtc"/>, persists, and returns a copy of the stored job.
    /// </summary>
    public CronJobDto Create(CronJobDto job)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));
        var (ok, error) = CronSchedule.Validate(job);
        if (!ok)
            throw new ArgumentException($"invalid cron job: {error}", nameof(job));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var now = DateTime.UtcNow;
            var id = NewId(ctx);

            var entity = new CronJobEntity { Id = id, TenantId = ctx.ActiveTenant! };
            ApplyDefinition(entity, job);
            entity.CreatedUtc = now;
            entity.LastFiredUtc = null;
            entity.LastStatus = null;
            entity.NotifyOn = CronNotify.Normalize(job.NotifyOn);
            entity.NextRunUtc = CronSchedule.ComputeNextRunUtc(ToDto(entity), now);

            ctx.CronJobs.Add(entity);
            ctx.SaveChanges();

            var stored = ToDto(entity);
            FileLog.Write($"[CronJobStore] Create: id={stored.Id}, name={stored.Name}, kind={stored.ScheduleKind}, nextRunUtc={stored.NextRunUtc:o}");
            return stored;
        }
    }

    /// <summary>All jobs, id-sorted (ordinal). Each is a defensive copy.</summary>
    public IReadOnlyList<CronJobDto> ListAll()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.CronJobs.AsNoTracking().ToList()
                .Select(ToDto)
                .OrderBy(j => j.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>All jobs of an account the CALLER names rather than the ambient one, id-sorted - for a view that
    /// resolved its account itself (the Factories screen's Seats tab).</summary>
    public IReadOnlyList<CronJobDto> ListAll(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.CronJobs.AsNoTracking().ToList()
                .Select(ToDto)
                .OrderBy(j => j.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>One job of an account the CALLER names rather than the ambient one, or null if absent - for a route that
    /// resolved its account itself (the Factories screen's Talk button reads the seat's schedule seed).</summary>
    public CronJobDto? Get(TenantId tenant, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var entity = ctx.CronJobs.AsNoTracking().FirstOrDefault(e => e.Id == id);
            return entity is null ? null : ToDto(entity);
        }
    }

    /// <summary>One job by id as a defensive copy, or null if absent.</summary>
    public CronJobDto? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.CronJobs.AsNoTracking().FirstOrDefault(e => e.Id == id);
            return entity is null ? null : ToDto(entity);
        }
    }

    /// <summary>
    /// Replace an existing job's editable fields from a validated definition, preserving its id, creation
    /// time, and last-run metadata, and recomputing <see cref="CronJobDto.NextRunUtc"/>. Returns the updated
    /// copy, or null if no job with that id exists.
    /// </summary>
    public CronJobDto? Update(string id, CronJobDto incoming)
    {
        if (incoming is null)
            throw new ArgumentNullException(nameof(incoming));
        var (ok, error) = CronSchedule.Validate(incoming);
        if (!ok)
            throw new ArgumentException($"invalid cron job: {error}", nameof(incoming));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.CronJobs.FirstOrDefault(e => e.Id == id);
            if (entity is null)
            {
                FileLog.Write($"[CronJobStore] Update: no such job id={id}");
                return null;
            }

            // Preserve identity + the firing engine's metadata; overwrite the editable definition.
            ApplyDefinition(entity, incoming);
            entity.NotifyOn = CronNotify.Normalize(incoming.NotifyOn);
            entity.NextRunUtc = CronSchedule.ComputeNextRunUtc(ToDto(entity), DateTime.UtcNow);

            ctx.SaveChanges();
            var stored = ToDto(entity);
            FileLog.Write($"[CronJobStore] Update: id={id}, name={stored.Name}, nextRunUtc={stored.NextRunUtc:o}");
            return stored;
        }
    }

    /// <summary>
    /// Switch one job of an account the CALLER names on or off, and nothing else about it - for the owner's Archive
    /// and Restore of a factory (Factories screen mission, round 2), which switch exactly the schedules its seats name.
    /// Switching on recomputes the next run from now, so a schedule that was off does not fire the runs it missed.
    /// Returns the updated copy, or null if the account has no job with that id.
    /// </summary>
    /// <param name="cronExpression">A window schedule's settings with its newly chosen minute, written with the switch;
    /// null leaves the stored expression as it is.</param>
    public CronJobDto? SetEnabled(TenantId tenant, string id, bool enabled, string? cronExpression = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var entity = ctx.CronJobs.FirstOrDefault(e => e.Id == id);
            if (entity is null)
            {
                FileLog.Write($"[CronJobStore] SetEnabled: no such job id={id}");
                return null;
            }
            entity.Enabled = enabled;
            if (cronExpression is not null)
                entity.CronExpression = cronExpression;
            entity.NextRunUtc = CronSchedule.ComputeNextRunUtc(ToDto(entity), DateTime.UtcNow);
            ctx.SaveChanges();
            var stored = ToDto(entity);
            FileLog.Write($"[CronJobStore] SetEnabled: id={id}, enabled={enabled}, nextRunUtc={stored.NextRunUtc:o}");
            return stored;
        }
    }

    /// <summary>Delete the job with the given id. Returns true if a job was removed, false if none existed.</summary>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.CronJobs.FirstOrDefault(e => e.Id == id);
            if (entity is null)
            {
                FileLog.Write($"[CronJobStore] Delete: no such job id={id}");
                return false;
            }

            ctx.CronJobs.Remove(entity);
            ctx.SaveChanges();
            FileLog.Write($"[CronJobStore] Delete: id={id}");
            return true;
        }
    }

    /// <summary>
    /// Record a fire's outcome on a job (epic #479, #483). Sets <see cref="CronJobDto.LastFiredUtc"/>,
    /// <see cref="CronJobDto.LastStatus"/>, <see cref="CronJobDto.NextRunUtc"/>, and
    /// <see cref="CronJobDto.Enabled"/>, then persists. Returns the updated copy, or null if no job with that
    /// id exists.
    /// </summary>
    public CronJobDto? MarkFired(string id, DateTime lastFiredUtc, string lastStatus, DateTime? nextRunUtc, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.CronJobs.FirstOrDefault(e => e.Id == id);
            if (entity is null)
            {
                FileLog.Write($"[CronJobStore] MarkFired: no such job id={id}");
                return null;
            }

            entity.LastFiredUtc = lastFiredUtc;
            entity.LastStatus = lastStatus;
            entity.NextRunUtc = nextRunUtc;
            entity.Enabled = enabled;

            ctx.SaveChanges();
            var stored = ToDto(entity);
            FileLog.Write($"[CronJobStore] MarkFired: id={id}, status={lastStatus}, enabled={enabled}, nextRunUtc={nextRunUtc:o}");
            return stored;
        }
    }

    /// <summary>
    /// Move a job on to its next run WITHOUT a fire: its last status says why, and its last fire time is left alone
    /// because nothing ran. The engine uses it for a window schedule's catch-up found after its deadline.
    /// </summary>
    public CronJobDto? SkipRun(string id, string lastStatus, DateTime? nextRunUtc)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.CronJobs.FirstOrDefault(e => e.Id == id);
            if (entity is null)
            {
                FileLog.Write($"[CronJobStore] SkipRun: no such job id={id}");
                return null;
            }

            entity.LastStatus = lastStatus;
            entity.NextRunUtc = nextRunUtc;
            ctx.SaveChanges();
            var stored = ToDto(entity);
            FileLog.Write($"[CronJobStore] SkipRun: id={id}, status={lastStatus}, nextRunUtc={nextRunUtc:o}");
            return stored;
        }
    }

    /// <summary>Recompute every job's next-run time on load and persist (the wall clock advanced while down).</summary>
    private void RecomputeNextRunOnLoad()
    {
        using var ctx = _db.CreateContext();
        var entities = ctx.CronJobs.ToList();
        if (entities.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var entity in entities)
        {
            // A switched-on schedule whose next run passed while the Gateway was down missed it. Remember the first one
            // it missed, so the engine records it as a run that did not run (Factory Control, step 1) instead of the
            // recompute below moving on in silence.
            if (entity.Enabled && entity.NextRunUtc is { } due && due <= now)
                _missedOnLoad.Add(new MissedRun(entity.TenantId, entity.Id, DateTime.SpecifyKind(due, DateTimeKind.Utc), now));
            entity.NextRunUtc = CronSchedule.ComputeNextRunUtc(ToDto(entity), now);
        }

        ctx.SaveChanges();
        FileLog.Write($"[CronJobStore] Load: {entities.Count} job(s) present; next-run recomputed; missed while down={_missedOnLoad.Count}");
    }

    /// <summary>A run that fell due while the Gateway was down.</summary>
    /// <param name="TenantId">The account the schedule belongs to.</param>
    /// <param name="JobId">The schedule.</param>
    /// <param name="DueUtc">The first run it missed.</param>
    /// <param name="LoadedUtc">When the Gateway loaded again and moved the schedule on.</param>
    public sealed record MissedRun(string TenantId, string JobId, DateTime DueUtc, DateTime LoadedUtc);

    private readonly List<MissedRun> _missedOnLoad = new();

    /// <summary>
    /// Hand over, ONCE, the runs of <paramref name="tenant"/> that fell due while the Gateway was down. A second call
    /// returns nothing, so each missed run is recorded exactly once.
    /// </summary>
    public IReadOnlyList<MissedRun> TakeMissedOnLoad(TenantId tenant)
    {
        lock (_gate)
        {
            var mine = _missedOnLoad.Where(m => string.Equals(m.TenantId, tenant.Value, StringComparison.Ordinal)).ToList();
            if (mine.Count > 0)
            {
                _missedOnLoad.RemoveAll(m => string.Equals(m.TenantId, tenant.Value, StringComparison.Ordinal));
                FileLog.Write($"[CronJobStore] TakeMissedOnLoad: tenant={tenant.ToLogString()}, runs={mine.Count}");
            }
            return mine;
        }
    }

    /// <summary>Mint an id not already in use. Short and human-quotable, like <c>cj_7fa3b1</c>.</summary>
    private static string NewId(GatewayDbContext ctx)
    {
        string id;
        do
        {
            id = "cj_" + Guid.NewGuid().ToString("N")[..6];
        }
        while (ctx.CronJobs.Any(e => e.Id == id));
        return id;
    }

    /// <summary>Copy the editable definition fields from a DTO onto an entity (identity/run metadata untouched).</summary>
    private static void ApplyDefinition(CronJobEntity entity, CronJobDto job)
    {
        entity.Name = job.Name;
        entity.Enabled = job.Enabled;
        entity.ScheduleKind = job.ScheduleKind;
        entity.CronExpression = job.CronExpression;
        entity.RunAt = job.RunAt;
        entity.TimeZoneId = job.TimeZoneId;
        entity.Factory = job.Factory;
        entity.Seat = job.Seat;
        entity.Target = new CronJobTarget { Machine = job.Target.Machine };
        entity.Action = new CronJobAction
        {
            RepoPath = job.Action.RepoPath,
            Seed = job.Action.Seed,
            WorkListName = job.Action.WorkListName,
            AutoDismiss = job.Action.AutoDismiss,
        };
        entity.PreventOverlap = job.PreventOverlap;
        entity.NotifyOn = job.NotifyOn;
        entity.NotifyWebhookUrl = job.NotifyWebhookUrl;
    }

    private static CronJobDto ToDto(CronJobEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Enabled = e.Enabled,
        ScheduleKind = e.ScheduleKind,
        CronExpression = e.CronExpression,
        RunAt = e.RunAt,
        TimeZoneId = e.TimeZoneId,
        Factory = e.Factory,
        Seat = e.Seat,
        Target = new CronJobTarget { Machine = e.Target.Machine },
        Action = new CronJobAction
        {
            RepoPath = e.Action.RepoPath,
            Seed = e.Action.Seed,
            WorkListName = e.Action.WorkListName,
            AutoDismiss = e.Action.AutoDismiss,
        },
        PreventOverlap = e.PreventOverlap,
        NotifyOn = e.NotifyOn,
        NotifyWebhookUrl = e.NotifyWebhookUrl,
        CreatedUtc = e.CreatedUtc,
        LastFiredUtc = e.LastFiredUtc,
        NextRunUtc = e.NextRunUtc,
        LastStatus = e.LastStatus,
    };

    // ---- one-time legacy JSON import --------------------------------------------------------------

    /// <summary>The on-disk shape of the legacy store file: one document holding every job.</summary>
    private sealed class StoreFile
    {
        public List<CronJobDto> Jobs { get; set; } = new();
    }

    /// <summary>
    /// Import a legacy <c>cronjobs.json</c> exactly once, through the shared recoverable-import plumbing
    /// (<see cref="LegacyJsonImport.Recoverable"/>): import only when the file exists AND the table is empty;
    /// if the file lingers while the table is already populated, rename it aside idempotently (recovery from a
    /// rename that failed after a prior commit); and rename aside best-effort after a successful import so a
    /// briefly-locked file cannot brick startup. The parse/insert below is unchanged and stays fail-loud.
    /// </summary>
    private void ImportLegacyJsonIfNeeded()
        => LegacyJsonImport.Recoverable(
            _legacyJsonPath,
            "[CronJobStore]",
            isPopulated: () => { using var ctx = _db.CreateContext(); return ctx.CronJobs.Any(); },
            importCommitted: ImportRowsFromLegacyJson);

    /// <summary>
    /// Parse the legacy file and insert every job inside one transaction. Fail-loud and all-or-nothing - a
    /// parse or write error throws and imports nothing (the transaction rolls back and the JSON is left in
    /// place), so no data is lost or partially imported. Called by the recoverable-import plumbing only when
    /// the file exists and the table is empty; the plumbing renames the file aside after this returns.
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
            FileLog.Write($"[CronJobStore] Import FAILED: legacy file {_legacyJsonPath} could not be read: {ex.Message}");
            throw new InvalidOperationException(
                $"The legacy cron jobs file '{_legacyJsonPath}' could not be parsed for the one-time import: " +
                $"{ex.Message}. The Gateway will not start with a partial import. Fix or move the file aside " +
                "and restart.", ex);
        }

        var jobs = parsed?.Jobs ?? new List<CronJobDto>();

        using var tx = ctx.Database.BeginTransaction();
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Id))
                throw new InvalidOperationException(
                    $"The legacy cron jobs file '{_legacyJsonPath}' has a job with an empty id; refusing a " +
                    "partial import.");

            var entity = new CronJobEntity { Id = job.Id, TenantId = ctx.ActiveTenant! };
            ApplyDefinition(entity, job);
            entity.NotifyOn = CronNotify.Normalize(job.NotifyOn);
            entity.CreatedUtc = job.CreatedUtc;
            entity.LastFiredUtc = job.LastFiredUtc;
            entity.LastStatus = job.LastStatus;
            entity.NextRunUtc = job.NextRunUtc;
            ctx.CronJobs.Add(entity);
        }
        ctx.SaveChanges();
        tx.Commit();

        FileLog.Write($"[CronJobStore] Import: {jobs.Count} job(s) imported from {_legacyJsonPath}");
    }
}
