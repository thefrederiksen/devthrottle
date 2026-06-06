using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>One live session as recorded in a Director's crash journal.</summary>
public sealed class DirectorCrashJournalSession
{
    public string SessionId { get; set; } = "";
    public string? Name { get; set; }
    public string RepoPath { get; set; } = "";
    public string Agent { get; set; } = "ClaudeCode";
    public string? ClaudeSessionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>The on-disk shape of a Director crash journal.</summary>
public sealed class DirectorCrashJournalData
{
    public string DirectorId { get; set; } = "";
    public int Pid { get; set; }
    public string MachineName { get; set; } = "";
    public string User { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset LastUpdatedUtc { get; set; }
    public List<DirectorCrashJournalSession> Sessions { get; set; } = new();
}

/// <summary>A crash journal left behind by a Director that died without a clean shutdown.</summary>
public sealed record DirtyShutdown(DirectorCrashJournalData Data, string DirtyFilePath);

/// <summary>
/// Durable per-Director roster of live sessions (issue #212 W1/L5).
///
/// The 2026-06-06 incident lost ten sessions because the only roster lived in memory: when
/// the Director died abnormally, nothing on disk said what had been running or how to get it
/// back. This journal fixes that. Each Director continuously writes
/// <c>crash-journal/{directorId}.json</c> with its live sessions (name, repo, Claude id), and
/// DELETES it on clean shutdown - exactly the crash-sentinel pattern InstanceRegistration
/// already uses. So a surviving journal whose owning process is dead is, by construction, a
/// dirty shutdown with a recoverable roster.
///
/// On startup a Director claims any such leftover (renames it to <c>.dirty.json</c> so it is
/// reported exactly once) and leaves it for the recovery surface (Cockpit "Interrupted
/// sessions" / the restore skill - later workstreams) to consume. This is deliberately a
/// purpose-built, per-Director file: the legacy shared <c>sessions.json</c> is cleared on
/// every startup and stomped across concurrent Directors, so it can never be the restore
/// point.
/// </summary>
public sealed class DirectorCrashJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;
    private readonly DirectorCrashJournalData _data;
    private readonly object _gate = new();

    public string FilePath { get; }

    public DirectorCrashJournal(
        string directorId, int pid, string machineName, string user,
        DateTimeOffset startedAtUtc, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));

        _directory = directory ?? DefaultDirectory;
        FilePath = Path.Combine(_directory, $"{directorId}.json");
        _data = new DirectorCrashJournalData
        {
            DirectorId = directorId,
            Pid = pid,
            MachineName = machineName,
            User = user,
            StartedAtUtc = startedAtUtc,
        };
    }

    /// <summary>The directory all crash journals live in: config/director/crash-journal/.</summary>
    public static string DefaultDirectory => Path.Combine(CcStorage.ToolConfig("director"), "crash-journal");

    /// <summary>
    /// Replace the journal's session roster and flush to disk atomically. Called whenever
    /// the live session set changes (create/rename/relink/close) so the on-disk roster is
    /// never more than one event stale.
    /// </summary>
    public void Update(IEnumerable<DirectorCrashJournalSession> sessions)
    {
        lock (_gate)
        {
            _data.Sessions = sessions.ToList();
            _data.LastUpdatedUtc = DateTimeOffset.UtcNow;
            Directory.CreateDirectory(_directory);

            var json = JsonSerializer.Serialize(_data, JsonOptions);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    /// <summary>
    /// Mark a clean shutdown by deleting the journal. A missing journal means "this Director
    /// stopped gracefully; nothing to recover" - so only an abnormal death leaves a file.
    /// </summary>
    public void MarkClean()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorCrashJournal] MarkClean failed for {FilePath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Scan for journals left behind by Directors that died abnormally and claim each one
    /// (rename to <c>.dirty.json</c>) so it is reported exactly once. A journal is "dirty" when
    /// its owning PID is no longer alive and it still holds at least one session; an empty
    /// leftover is just deleted. Robust per-file: one unreadable journal never aborts the scan.
    /// </summary>
    /// <param name="currentPid">This Director's PID, never treated as a dead predecessor.</param>
    public static IReadOnlyList<DirtyShutdown> DetectAndClaim(int currentPid, string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        var result = new List<DirtyShutdown>();
        if (!Directory.Exists(dir)) return result;

        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (path.EndsWith(".dirty.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var data = JsonSerializer.Deserialize<DirectorCrashJournalData>(File.ReadAllText(path), JsonOptions);
                if (data is null) continue;
                if (data.Pid == currentPid) continue;       // our own (not yet written, but be safe)
                if (IsProcessAlive(data.Pid)) continue;     // another live Director

                if (data.Sessions.Count == 0)
                {
                    // Dead Director, empty roster: nothing to recover, just clean up.
                    TryDelete(path);
                    continue;
                }

                var dirtyPath = Path.Combine(dir, $"{data.DirectorId}.{data.Pid}.dirty.json");
                File.Move(path, dirtyPath, overwrite: true);
                FileLog.Write(
                    $"[DirectorCrashJournal] DIRTY SHUTDOWN detected: directorId={data.DirectorId} " +
                    $"pid={data.Pid} machine={data.MachineName} startedAt={data.StartedAtUtc:o} " +
                    $"lastUpdated={data.LastUpdatedUtc:o} liveSessions={data.Sessions.Count} -> {dirtyPath}");
                foreach (var s in data.Sessions)
                    FileLog.Write($"[DirectorCrashJournal]   recoverable: sid={s.SessionId} name=\"{s.Name}\" repo={s.RepoPath} claude={s.ClaudeSessionId}");

                result.Add(new DirtyShutdown(data, dirtyPath));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorCrashJournal] DetectAndClaim: failed to inspect {path}: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>
    /// All claimed-but-not-yet-recovered dirty journals, newest session-activity first. The
    /// read API for the recovery surface (Cockpit interrupted bucket / restore skill).
    /// </summary>
    public static IReadOnlyList<DirectorCrashJournalData> ListPendingRecoveries(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        var result = new List<DirectorCrashJournalData>();
        if (!Directory.Exists(dir)) return result;

        foreach (var path in Directory.EnumerateFiles(dir, "*.dirty.json"))
        {
            try
            {
                var data = JsonSerializer.Deserialize<DirectorCrashJournalData>(File.ReadAllText(path), JsonOptions);
                if (data is not null) result.Add(data);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorCrashJournal] ListPendingRecoveries: failed to read {path}: {ex.Message}");
            }
        }
        return result.OrderByDescending(d => d.LastUpdatedUtc).ToList();
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { FileLog.Write($"[DirectorCrashJournal] failed to delete {path}: {ex.Message}"); }
    }
}
