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
    /// How long a claimed crash journal is kept before it is swept as stale (issue #961). Only the
    /// last week of crashes is useful for recovery; older <c>.dirty.json</c> files just accumulate
    /// and clutter the Interrupted list. The recovery read surface also hides anything older.
    /// </summary>
    public static readonly TimeSpan DirtyJournalRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// Delete claimed <c>.dirty.json</c> journals whose last activity (<c>LastUpdatedUtc</c>) is
    /// older than <paramref name="maxAge"/> (default <see cref="DirtyJournalRetention"/>). Returns
    /// the number deleted. Robust per-file: one unreadable or locked journal never aborts the sweep.
    /// </summary>
    public static int SweepExpired(TimeSpan? maxAge = null, string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        if (!Directory.Exists(dir)) return 0;
        var cutoff = DateTimeOffset.UtcNow - (maxAge ?? DirtyJournalRetention);
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(dir, "*.dirty.json"))
        {
            try
            {
                if (IsExpired(path, cutoff))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorCrashJournal] SweepExpired: could not remove {path}: {ex.Message}");
            }
        }
        if (deleted > 0)
            FileLog.Write($"[DirectorCrashJournal] SweepExpired: removed {deleted} crash journal(s) older than {(maxAge ?? DirtyJournalRetention).TotalDays:0.#} day(s).");
        return deleted;
    }

    // A dirty journal is expired when its recorded last activity is older than the cutoff. Falls
    // back to the file's last-write time when the content cannot be read, so a corrupt or ancient
    // file is still swept rather than lingering forever.
    private static bool IsExpired(string path, DateTimeOffset cutoff)
    {
        try
        {
            var data = JsonSerializer.Deserialize<DirectorCrashJournalData>(File.ReadAllText(path), JsonOptions);
            if (data is not null) return data.LastUpdatedUtc < cutoff;
        }
        catch { /* fall through to the file's timestamp */ }
        return File.GetLastWriteTimeUtc(path) < cutoff;
    }

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
    /// The LIVE roster of a running Director, read from outside that Director - the session count the
    /// launcher needs before it decides whether installing an update would interrupt somebody's work.
    ///
    /// This is the same file <see cref="Update"/> writes, read rather than asked for. It is a fair
    /// substitute for asking the Director over a socket precisely because of how it is maintained: it
    /// is rewritten atomically on EVERY change to the session set, so it is never more than one event
    /// stale, and <see cref="MarkClean"/> deletes it on an orderly stop, so a file that exists beside a
    /// live process describes that process. It carries every session's identity as well as the count,
    /// which the health route never did.
    ///
    /// Returns null when there is no readable journal. Callers must treat that as "unknown", never as
    /// "no sessions": the file is absent both when a Director holds nothing and when it has not opened
    /// its journal yet, and only one of those is safe to restart.
    /// </summary>
    /// <param name="directorId">The Director whose roster is wanted.</param>
    /// <param name="directory">The crash-journal directory of THAT Director's instance home. A caller
    /// outside the Director must pass this: journals live under the instance's own storage, and the
    /// default resolves against the CALLER's home, which is a different place.</param>
    public static DirectorCrashJournalData? ReadLiveRoster(string directorId, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(directorId)) return null;
        var path = Path.Combine(directory ?? DefaultDirectory, $"{directorId}.json");
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);

            // THE SESSION LIST MUST BE POSITIVELY PRESENT IN THE DOCUMENT, and this check is the point of
            // the method rather than defensive tidying.
            //
            // DirectorCrashJournalData.Sessions has an empty-list initializer, so deserializing a document
            // that never mentioned sessions - "{}", a half-written file, or one written by a newer build
            // that renamed the property - produces a NON-NULL roster whose Sessions.Count is ZERO. The
            // caller then reads a confident "this Director holds no sessions" out of a document that said
            // nothing at all, and a guard standing on that number permits a restart over a busy Director.
            //
            // That is the same fold this method's own summary warns about - "callers must treat null as
            // unknown, never as no sessions" - defeated one layer BELOW it, at deserialization, where
            // absent and explicitly-empty become the same in-memory value. The sentence was true and the
            // layer beneath it made it false, which is why the check belongs here and not in the caller:
            // no caller can tell the two apart once they have been folded.
            //
            // An EXPLICIT empty list is a real answer and still passes: "sessions": [] means the Director
            // says it holds none.
            using (var document = JsonDocument.Parse(text))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !CarriesASessionList(document.RootElement))
                {
                    FileLog.Write($"[DirectorCrashJournal] ReadLiveRoster: {path} carries no session list, so how "
                                  + "busy this Director is is UNKNOWN. It is NOT being reported as zero sessions - "
                                  + "a document that did not answer must never read as an answer of none.");
                    return null;
                }
            }

            return JsonSerializer.Deserialize<DirectorCrashJournalData>(text, JsonOptions);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorCrashJournal] ReadLiveRoster: cannot read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Is a session list actually present in this document? Matched case-insensitively, because the
    /// serializer that reads the document is case-insensitive too - a check stricter than the parser it
    /// guards would refuse documents the product itself writes.
    /// </summary>
    private static bool CarriesASessionList(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, "sessions", StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.Array;
        return false;
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
    /// read API for the recovery surface (Cockpit Interrupted sessions list / restore skill).
    /// </summary>
    public static IReadOnlyList<DirectorCrashJournalData> ListPendingRecoveries(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        var result = new List<DirectorCrashJournalData>();
        if (!Directory.Exists(dir)) return result;

        // Hide journals older than the retention window (issue #961) so the Interrupted list only
        // ever shows genuinely recent crashes, even if the startup sweep has not run yet.
        var cutoff = DateTimeOffset.UtcNow - DirtyJournalRetention;
        foreach (var path in Directory.EnumerateFiles(dir, "*.dirty.json"))
        {
            try
            {
                var data = JsonSerializer.Deserialize<DirectorCrashJournalData>(File.ReadAllText(path), JsonOptions);
                if (data is not null && data.LastUpdatedUtc >= cutoff) result.Add(data);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorCrashJournal] ListPendingRecoveries: failed to read {path}: {ex.Message}");
            }
        }
        return result.OrderByDescending(d => d.LastUpdatedUtc).ToList();
    }

    /// <summary>
    /// Dismiss a claimed dirty journal (delete its <c>.dirty.json</c>) once its sessions have
    /// been recovered or the user no longer cares. Returns true if a file was removed. The
    /// recovery surface calls this via the Director that surfaced the journal.
    /// </summary>
    public static bool Dismiss(string directorId, int pid, string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        var path = Path.Combine(dir, $"{directorId}.{pid}.dirty.json");
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            FileLog.Write($"[DirectorCrashJournal] dismissed dirty journal {path}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorCrashJournal] Dismiss failed for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Remove ONE session from a claimed dirty journal after it has been restored
    /// (issue #212 W4), so the remaining sessions stay in the Interrupted sessions list. When the
    /// last session is removed the journal file is deleted - same end state as
    /// <see cref="Dismiss"/>. Returns false when the journal or the session is not there
    /// (already restored/dismissed); restoring twice must not fail the second caller.
    /// </summary>
    public static bool RemoveSession(string directorId, int pid, string sessionId, string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        var path = Path.Combine(dir, $"{directorId}.{pid}.dirty.json");
        try
        {
            if (!File.Exists(path)) return false;
            var data = JsonSerializer.Deserialize<DirectorCrashJournalData>(File.ReadAllText(path), JsonOptions);
            if (data is null) return false;

            var removed = data.Sessions.RemoveAll(s =>
                string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) return false;

            if (data.Sessions.Count == 0)
            {
                File.Delete(path);
                FileLog.Write($"[DirectorCrashJournal] removed last session {sessionId} from {path} - journal deleted");
            }
            else
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
                File.Move(tmp, path, overwrite: true);
                FileLog.Write($"[DirectorCrashJournal] removed session {sessionId} from {path} ({data.Sessions.Count} remaining)");
            }
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorCrashJournal] RemoveSession failed for {path}: {ex.Message}");
            return false;
        }
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
