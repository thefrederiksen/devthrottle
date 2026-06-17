using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway;

/// <summary>
/// The Gateway's cron-job definition store (epic #479, part 1 = issue #482). Holds cron-job
/// definitions keyed by id and serves the REST CRUD surface; it does NOT fire jobs (the background
/// engine is part 2, issue #483).
///
/// PERSISTENCE (the <see cref="WorkListStore"/> precedent): the whole store lives in ONE plain
/// JSON file at the path the constructor receives (production: <c>cronjobs.json</c> in the Gateway
/// data dir). Every mutation writes through immediately with an atomic temp-file + rename, so a
/// crash mid-write can never half-truncate the store. On construction the file is loaded back:
///   - missing file  = empty store + a log line (the normal first boot), never an error;
///   - corrupt file  = the bytes are QUARANTINED to "&lt;path&gt;.corrupt-&lt;stamp&gt;" (preserved
///     for the operator, never silently overwritten), an explicit error is logged, and the store
///     starts empty so the Gateway still boots;
///   - every loaded job has its <see cref="CronJobDto.NextRunUtc"/> RECOMPUTED from the schedule
///     (the wall clock moved on while the Gateway was down), and the refreshed state is persisted.
/// </summary>
public sealed class CronJobStore
{
    private readonly object _gate = new();
    private readonly string _path;

    // Id -> job. Ids are case-sensitive opaque tokens minted by the store.
    private readonly Dictionary<string, CronJobDto> _jobs = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <param name="path">
    /// The JSON file the store persists to. REQUIRED so no caller can silently land on the real
    /// user's file: production (<see cref="GatewayHost"/>) passes <c>cronjobs.json</c> in the
    /// Gateway data dir; tests pass an isolated temp path.
    /// </param>
    /// <exception cref="ArgumentException">The path is null/empty/whitespace.</exception>
    public CronJobStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("store path is required", nameof(path));
        _path = path;
        Load();
    }

    /// <summary>
    /// Create a job from a validated definition. Mints an id, stamps <see cref="CronJobDto.CreatedUtc"/>,
    /// computes <see cref="CronJobDto.NextRunUtc"/>, persists, and returns a copy of the stored job.
    /// </summary>
    /// <exception cref="ArgumentNullException">The job is null.</exception>
    /// <exception cref="ArgumentException">The job fails <see cref="CronSchedule.Validate"/> (the
    /// REST surface validates first and returns 400; reaching here with an invalid job is a bug).</exception>
    public CronJobDto Create(CronJobDto job)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));
        var (ok, error) = CronSchedule.Validate(job);
        if (!ok)
            throw new ArgumentException($"invalid cron job: {error}", nameof(job));

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            job.Id = NewId();
            job.CreatedUtc = now;
            job.LastFiredUtc = null;
            job.LastStatus = null;
            job.NextRunUtc = CronSchedule.ComputeNextRunUtc(job, now);

            _jobs[job.Id] = Copy(job);
            Save();
            FileLog.Write($"[CronJobStore] Create: id={job.Id}, name={job.Name}, kind={job.ScheduleKind}, nextRunUtc={job.NextRunUtc:o}");
            return Copy(job);
        }
    }

    /// <summary>All jobs, id-sorted. Each is a defensive copy (callers never mutate the store).</summary>
    public IReadOnlyList<CronJobDto> ListAll()
    {
        lock (_gate)
            return _jobs.Values
                .OrderBy(j => j.Id, StringComparer.Ordinal)
                .Select(Copy)
                .ToList();
    }

    /// <summary>One job by id as a defensive copy, or null if absent.</summary>
    public CronJobDto? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        lock (_gate)
            return _jobs.TryGetValue(id, out var job) ? Copy(job) : null;
    }

    /// <summary>
    /// Replace an existing job's editable fields from a validated definition, preserving its id,
    /// creation time, and last-run metadata, and recomputing <see cref="CronJobDto.NextRunUtc"/>.
    /// Returns the updated copy, or null if no job with that id exists.
    /// </summary>
    /// <exception cref="ArgumentNullException">The incoming definition is null.</exception>
    /// <exception cref="ArgumentException">The definition fails <see cref="CronSchedule.Validate"/>.</exception>
    public CronJobDto? Update(string id, CronJobDto incoming)
    {
        if (incoming is null)
            throw new ArgumentNullException(nameof(incoming));
        var (ok, error) = CronSchedule.Validate(incoming);
        if (!ok)
            throw new ArgumentException($"invalid cron job: {error}", nameof(incoming));

        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var existing))
            {
                FileLog.Write($"[CronJobStore] Update: no such job id={id}");
                return null;
            }

            // Preserve identity + the firing engine's metadata; overwrite the editable definition.
            existing.Name = incoming.Name;
            existing.Enabled = incoming.Enabled;
            existing.ScheduleKind = incoming.ScheduleKind;
            existing.CronExpression = incoming.CronExpression;
            existing.RunAt = incoming.RunAt;
            existing.TimeZoneId = incoming.TimeZoneId;
            existing.Target = new CronJobTarget { DirectorId = incoming.Target.DirectorId };
            existing.Action = new CronJobAction { RepoPath = incoming.Action.RepoPath, Seed = incoming.Action.Seed, WorkListName = incoming.Action.WorkListName };
            existing.PreventOverlap = incoming.PreventOverlap;
            existing.NextRunUtc = CronSchedule.ComputeNextRunUtc(existing, DateTime.UtcNow);

            Save();
            FileLog.Write($"[CronJobStore] Update: id={id}, name={existing.Name}, nextRunUtc={existing.NextRunUtc:o}");
            return Copy(existing);
        }
    }

    /// <summary>
    /// Delete the job with the given id. Returns true if a job was removed, false if none existed.
    /// </summary>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        lock (_gate)
        {
            if (!_jobs.Remove(id))
            {
                FileLog.Write($"[CronJobStore] Delete: no such job id={id}");
                return false;
            }

            Save();
            FileLog.Write($"[CronJobStore] Delete: id={id}");
            return true;
        }
    }

    /// <summary>
    /// Record a fire's outcome on a job (epic #479, #483): the firing engine is the writer of the
    /// run metadata that <see cref="Update"/> deliberately preserves. Sets <see cref="CronJobDto.LastFiredUtc"/>,
    /// <see cref="CronJobDto.LastStatus"/>, <see cref="CronJobDto.NextRunUtc"/>, and
    /// <see cref="CronJobDto.Enabled"/> (a one-off fire disables itself with a null next-run), then
    /// persists. Returns the updated copy, or null if no job with that id exists.
    /// </summary>
    public CronJobDto? MarkFired(string id, DateTime lastFiredUtc, string lastStatus, DateTime? nextRunUtc, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var existing))
            {
                FileLog.Write($"[CronJobStore] MarkFired: no such job id={id}");
                return null;
            }

            existing.LastFiredUtc = lastFiredUtc;
            existing.LastStatus = lastStatus;
            existing.NextRunUtc = nextRunUtc;
            existing.Enabled = enabled;

            Save();
            FileLog.Write($"[CronJobStore] MarkFired: id={id}, status={lastStatus}, enabled={enabled}, nextRunUtc={nextRunUtc:o}");
            return Copy(existing);
        }
    }

    /// <summary>Mint an id not already in use. Short and human-quotable, like the design report's <c>cj_7fa3b1</c>.</summary>
    private string NewId()
    {
        string id;
        do
        {
            id = "cj_" + Guid.NewGuid().ToString("N")[..6];
        }
        while (_jobs.ContainsKey(id));
        return id;
    }

    private static CronJobDto Copy(CronJobDto job) => new()
    {
        Id = job.Id,
        Name = job.Name,
        Enabled = job.Enabled,
        ScheduleKind = job.ScheduleKind,
        CronExpression = job.CronExpression,
        RunAt = job.RunAt,
        TimeZoneId = job.TimeZoneId,
        Target = new CronJobTarget { DirectorId = job.Target.DirectorId },
        Action = new CronJobAction { RepoPath = job.Action.RepoPath, Seed = job.Action.Seed, WorkListName = job.Action.WorkListName },
        PreventOverlap = job.PreventOverlap,
        CreatedUtc = job.CreatedUtc,
        LastFiredUtc = job.LastFiredUtc,
        NextRunUtc = job.NextRunUtc,
        LastStatus = job.LastStatus,
    };

    // ---- persistence (WorkListStore precedent) -----------------------------------------------

    /// <summary>The on-disk shape: one document holding every job.</summary>
    private sealed class StoreFile
    {
        public List<CronJobDto> Jobs { get; set; } = new();
    }

    /// <summary>
    /// Load the store file written by a previous Gateway run. Missing file = the normal first boot
    /// (empty store, logged). A corrupt file is quarantined (renamed next to the original with a
    /// timestamp suffix) so its bytes are preserved and never silently overwritten; the store then
    /// starts empty so the Gateway still boots. Every loaded job's next-run time is recomputed (the
    /// clock advanced while down) and the refreshed state persisted.
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[CronJobStore] Load: no store file at {_path}; starting empty");
            return;
        }

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        if (parsed is null)
        {
            // "null" is valid JSON, so deserialization succeeds but yields no document.
            Quarantine("file deserialized to null (no store document)");
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var job in parsed.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Id))
            {
                Quarantine("a persisted job has an empty id");
                _jobs.Clear();
                return;
            }

            // The wall clock moved on while the Gateway was down: recompute the next-run time so a
            // GET after restart reports a current value (epic #479 AC3).
            job.NextRunUtc = CronSchedule.ComputeNextRunUtc(job, now);
            _jobs[job.Id] = job;
        }

        FileLog.Write($"[CronJobStore] Load: restored {_jobs.Count} job(s) from {_path}");

        // Persist the recomputed next-run times so disk matches memory after a restart.
        if (_jobs.Count > 0)
            Save();
    }

    /// <summary>
    /// Preserve an unreadable store file as "&lt;path&gt;.corrupt-&lt;stamp&gt;" and log loudly. The
    /// original path is then free for the next write-through. The move is not allowed to fail
    /// silently: if even the quarantine fails, the exception propagates and the Gateway does not
    /// start half-blind.
    /// </summary>
    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[CronJobStore] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty. Operator action: inspect the quarantined file to recover jobs.");
    }

    /// <summary>
    /// Write-through: serialize the whole store and atomically replace the file (temp + rename), so
    /// a concurrent reader or a crash mid-write never sees a half-written store. Called inside the
    /// lock by every mutation. A failed save is a LOGGED error that propagates - never a silent skip.
    /// </summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile
            {
                Jobs = _jobs.Values
                    .OrderBy(j => j.Id, StringComparer.Ordinal)
                    .ToList(),
            };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CronJobStore] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
