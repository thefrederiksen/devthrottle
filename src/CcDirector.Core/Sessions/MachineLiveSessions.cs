using System.Text.Json;
using CcDirector.Core.Git;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The live sessions of the OTHER Directors running on this machine, read from the rosters they keep
/// on disk rather than asked of the Gateway.
///
/// WHY THIS EXISTS. The Repository screen labels a worktree "in use" when a live session on this
/// machine runs in it, and it asks that question on every repository recompute - which is driven by
/// file changes, so it runs every few seconds on a busy machine. It used to answer by downloading the
/// Gateway's WHOLE fleet list (every session on every machine) and keeping this machine's rows: one
/// uncompressed download every thirteen seconds, measured over a morning, to learn something that is
/// already written on the local disk.
///
/// WHERE THE ANSWER IS. Every Director writes its live roster to
/// <c>&lt;its home&gt;/config/director/crash-journal/&lt;directorId&gt;.json</c> on EVERY change to its session
/// set, atomically, and deletes it on a clean shutdown (<see cref="DirectorCrashJournal"/>). Every Director
/// home on the machine sits under one machine root - <c>&lt;machine root&gt;/instances/&lt;slug&gt;</c>, plus the
/// pre-1.8 flat layout at the root itself - so the other slots' rosters are one directory listing away.
/// A journal whose process is no longer alive is a dead Director's leftover (a crash the next start will
/// claim), not a live session, and is skipped.
///
/// WHAT THIS IS NOT. It is the DISPLAY source only. The destructive worktree reaper keeps asking the
/// Gateway for the authoritative roster at the moment it deletes, and fails closed when that roster is
/// incomplete; nothing here is allowed to stand in for that check.
/// </summary>
public static class MachineLiveSessions
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The result of one read: the sessions found, and every roster file that could not be read.</summary>
    public sealed record Snapshot(IReadOnlyList<LiveSessionRef> Sessions, IReadOnlyList<string> Unreadable);

    /// <summary>
    /// Every crash-journal directory a Director on this machine can write to: the legacy flat layout at the
    /// machine root, then each instance home under <c>instances</c>. Only directories that exist are returned.
    /// </summary>
    public static IReadOnlyList<string> JournalDirectories(string machineRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineRoot);
        var dirs = new List<string>();
        var flat = Path.Combine(machineRoot, "config", "director", "crash-journal");
        if (Directory.Exists(flat)) dirs.Add(flat);

        var instances = Path.Combine(machineRoot, "instances");
        if (Directory.Exists(instances))
        {
            foreach (var home in Directory.EnumerateDirectories(instances))
            {
                var dir = Path.Combine(home, "config", "director", "crash-journal");
                if (Directory.Exists(dir)) dirs.Add(dir);
            }
        }
        return dirs;
    }

    /// <summary>
    /// The live sessions of every OTHER live Director on this machine, read from their on-disk rosters.
    /// </summary>
    /// <param name="machineRoot">The machine root (<see cref="CcStorage.MachineRoot"/> in production).</param>
    /// <param name="ownPid">This Director's process id. Its own journal is skipped: its sessions are held in
    /// memory, which is fresher than the file it writes.</param>
    /// <param name="isProcessAlive">Whether a process id is a running process. A seam so tests do not depend on
    /// which process ids happen to exist on the machine running them.</param>
    public static Snapshot ReadOtherDirectors(string machineRoot, int ownPid, Func<int, bool> isProcessAlive)
    {
        ArgumentNullException.ThrowIfNull(isProcessAlive);
        var sessions = new List<LiveSessionRef>();
        var unreadable = new List<string>();

        foreach (var dir in JournalDirectories(machineRoot))
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
            {
                // A claimed crash (.dirty.json) is a dead Director's roster, not a live one.
                if (path.EndsWith(".dirty.json", StringComparison.OrdinalIgnoreCase)) continue;

                DirectorCrashJournalData? data;
                try
                {
                    data = JsonSerializer.Deserialize<DirectorCrashJournalData>(File.ReadAllText(path), JsonOptions);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    unreadable.Add($"{path}: {ex.Message}");
                    continue;
                }
                if (data is null)
                {
                    unreadable.Add($"{path}: empty document");
                    continue;
                }

                if (data.Pid == ownPid) continue;
                if (!isProcessAlive(data.Pid)) continue;

                foreach (var s in data.Sessions)
                {
                    if (string.IsNullOrWhiteSpace(s.RepoPath)) continue;
                    sessions.Add(new LiveSessionRef { RepoPath = s.RepoPath, Label = LabelFor(s) });
                }
            }
        }

        return new Snapshot(sessions, unreadable);
    }

    /// <summary>
    /// The live sessions on this whole machine: this Director's own (from memory) followed by every other
    /// live Director's (from disk). Each roster file that could not be read is logged by name - the rest of
    /// the answer is still right, and the destructive path never reads this list.
    /// </summary>
    public static IReadOnlyList<LiveSessionRef> OnThisMachine(
        IEnumerable<LiveSessionRef> ownSessions, string machineRoot, int ownPid, Func<int, bool> isProcessAlive)
    {
        ArgumentNullException.ThrowIfNull(ownSessions);
        var result = ownSessions.ToList();
        var others = ReadOtherDirectors(machineRoot, ownPid, isProcessAlive);
        foreach (var failure in others.Unreadable)
            FileLog.Write($"[MachineLiveSessions] OnThisMachine: a sibling Director's roster could not be read, so its sessions are missing from the in-use labels: {failure}");
        result.AddRange(others.Sessions);
        return result;
    }

    /// <summary>Whether a process id names a running process. The production liveness check.</summary>
    public static bool IsProcessAlive(int pid)
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

    private static string LabelFor(DirectorCrashJournalSession s) =>
        string.IsNullOrWhiteSpace(s.Name) ? "session in another Director" : s.Name;
}
