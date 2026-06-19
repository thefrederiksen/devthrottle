using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway;

/// <summary>
/// The run-history store for cron jobs (epic #479, #483): one <see cref="CronRunRecord"/> per fire,
/// keyed by job id, newest-first, capped per job. Persisted to <c>cronruns.json</c> with the same
/// atomic write-through + corrupt-file quarantine contract as <see cref="CronJobStore"/> /
/// <see cref="WorkListStore"/>, so a crash mid-write never half-truncates the file and an unreadable
/// file is preserved rather than silently overwritten.
/// </summary>
public sealed class CronRunHistoryStore
{
    /// <summary>Max records retained per job; older runs are pruned (keeps the file bounded).</summary>
    public const int MaxRecordsPerJob = 50;

    private readonly object _gate = new();
    private readonly string _path;

    // Job id -> runs, newest first.
    private readonly Dictionary<string, List<CronRunRecord>> _runs = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <param name="path">The JSON file the store persists to. REQUIRED (no silent default).</param>
    /// <exception cref="ArgumentException">The path is null/empty/whitespace.</exception>
    public CronRunHistoryStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("store path is required", nameof(path));
        _path = path;
        Load();
    }

    /// <summary>
    /// Record one run for a job (newest first), pruning to <see cref="MaxRecordsPerJob"/>, and persist.
    /// </summary>
    /// <exception cref="ArgumentException">The job id is null/empty/whitespace.</exception>
    /// <exception cref="ArgumentNullException">The record is null.</exception>
    public void Append(string jobId, CronRunRecord record)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("job id is required", nameof(jobId));
        if (record is null)
            throw new ArgumentNullException(nameof(record));

        lock (_gate)
        {
            if (!_runs.TryGetValue(jobId, out var list))
            {
                list = new List<CronRunRecord>();
                _runs[jobId] = list;
            }

            list.Insert(0, Copy(record));
            if (list.Count > MaxRecordsPerJob)
                list.RemoveRange(MaxRecordsPerJob, list.Count - MaxRecordsPerJob);

            Save();
            FileLog.Write($"[CronRunHistoryStore] Append: job={jobId}, firedUtc={record.FiredUtc:o}, infra={record.InfraStatus}, task={record.TaskStatus}, count={list.Count}");
        }
    }

    /// <summary>One job's runs as defensive copies, newest first; empty when the job has no runs.</summary>
    public IReadOnlyList<CronRunRecord> List(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return Array.Empty<CronRunRecord>();

        lock (_gate)
            return _runs.TryGetValue(jobId, out var list)
                ? list.Select(Copy).ToList()
                : Array.Empty<CronRunRecord>();
    }

    private static CronRunRecord Copy(CronRunRecord r) => new()
    {
        ScheduledUtc = r.ScheduledUtc,
        FiredUtc = r.FiredUtc,
        Machine = r.Machine,
        TargetDirectorId = r.TargetDirectorId,
        SessionId = r.SessionId,
        InfraStatus = r.InfraStatus,
        TaskStatus = r.TaskStatus,
    };

    // ---- persistence (CronJobStore / WorkListStore precedent) --------------------------------

    private sealed class StoreFile
    {
        public Dictionary<string, List<CronRunRecord>> Runs { get; set; } = new(StringComparer.Ordinal);
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[CronRunHistoryStore] Load: no store file at {_path}; starting empty");
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
            Quarantine("file deserialized to null (no store document)");
            return;
        }

        foreach (var (jobId, list) in parsed.Runs)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                continue;
            _runs[jobId] = list ?? new List<CronRunRecord>();
        }

        FileLog.Write($"[CronRunHistoryStore] Load: restored runs for {_runs.Count} job(s) from {_path}");
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[CronRunHistoryStore] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile { Runs = _runs };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CronRunHistoryStore] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
