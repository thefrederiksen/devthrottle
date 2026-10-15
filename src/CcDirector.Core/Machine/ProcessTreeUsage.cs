namespace CcDirector.Core.Machine;

/// <summary>
/// WHAT EACH SESSION COSTS THE MACHINE: its root process and every process descended from it, summed.
///
/// A pure function of one process list, so it is tested from hand-built lists with no machine at all.
///
/// The tree is found by PARENT PROCESS ID. That is the whole method here, and its known limit is a child
/// that detaches from its parent: once the parent link is cut, the child is no longer under the session.
/// Whether that happens on Windows is being investigated separately; a second method that also uses
/// another signal can sit beside this one with the same result type.
///
/// TWO GUARDS KEEP IT HONEST:
///
/// - A REUSED PROCESS ID IS NOT A CHILD. Process ids are reused, so a process whose parent id names a
///   process that started AFTER it cannot be that process's child - it is the child of an older,
///   dead process that happened to have the same id. When both start times are known and the child
///   started before the parent, the link is refused. When either start time is not known the link is
///   taken, because refusing every link on a platform without start times would count nothing.
///
/// - NO PROCESS IS COUNTED TWICE. If two sessions' trees overlap - one session's root is a descendant of
///   another's - each process is given to the session whose root is its NEAREST ancestor, so the inner
///   session gets its own processes and the outer session does not count them again.
///
/// An unreadable process list gives an empty list: nothing is made up. A session whose root process is
/// not in the list is left out rather than shown as costing zero.
/// </summary>
public static class ProcessTreeUsage
{
    /// <summary>
    /// The usage of each session whose root process is running, following parent process ids down from
    /// each root. Sessions are returned in the order of their session ids, so the result is stable.
    /// </summary>
    public static IReadOnlyList<SessionUsage> BySession(
        ProcessSetReading processes,
        IReadOnlyDictionary<string, int> rootPidBySessionId)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(rootPidBySessionId);

        if (!processes.CouldRead || rootPidBySessionId.Count == 0) return Array.Empty<SessionUsage>();

        // One process per id. A list that names the same id twice is a torn read; the first is kept so the
        // result does not depend on dictionary order.
        var byPid = new Dictionary<int, ProcessReading>();
        foreach (var process in processes.Processes)
        {
            byPid.TryAdd(process.Pid, process);
        }

        var sessionByRootPid = new Dictionary<int, string>();
        foreach (var (sessionId, rootPid) in rootPidBySessionId.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // Two sessions claiming one root process: the first by id keeps it, so it is not counted twice.
            if (byPid.ContainsKey(rootPid)) sessionByRootPid.TryAdd(rootPid, sessionId);
        }

        var pidsBySession = new Dictionary<string, List<int>>();
        foreach (var process in byPid.Values.OrderBy(process => process.Pid))
        {
            var sessionId = OwningSession(process, byPid, sessionByRootPid);
            if (sessionId is null) continue;

            if (!pidsBySession.TryGetValue(sessionId, out var pids))
            {
                pids = new List<int>();
                pidsBySession[sessionId] = pids;
            }
            pids.Add(process.Pid);
        }

        var result = new List<SessionUsage>();
        foreach (var (rootPid, sessionId) in sessionByRootPid.OrderBy(pair => pair.Value, StringComparer.Ordinal))
        {
            var pids = pidsBySession[sessionId];
            double cpu = 0;
            ulong memory = 0;
            foreach (var pid in pids)
            {
                cpu += byPid[pid].CpuPercent;
                memory += byPid[pid].MemoryBytes;
            }
            result.Add(new SessionUsage(sessionId, rootPid, cpu, memory, pids));
        }
        return result;
    }

    /// <summary>
    /// The session whose root is this process's nearest ancestor, the process itself included, or null.
    /// Walks up the parent chain, refusing a link a reused process id would fake, and stops on a loop.
    /// </summary>
    private static string? OwningSession(
        ProcessReading process,
        Dictionary<int, ProcessReading> byPid,
        Dictionary<int, string> sessionByRootPid)
    {
        var visited = new HashSet<int>();
        var current = process;
        while (true)
        {
            if (sessionByRootPid.TryGetValue(current.Pid, out var sessionId)) return sessionId;
            if (!visited.Add(current.Pid)) return null;

            if (current.ParentPid is not { } parentPid || parentPid == current.Pid) return null;
            if (!byPid.TryGetValue(parentPid, out var parent)) return null;
            if (!CanBeParent(parent, current)) return null;

            current = parent;
        }
    }

    /// <summary>
    /// False when both start times are known and the child started before the would-be parent - the
    /// parent id then belongs to a newer process that reused the id, not the one that started the child.
    /// </summary>
    internal static bool CanBeParent(ProcessReading parent, ProcessReading child) =>
        parent.StartTimeUtc is not { } parentStart
        || child.StartTimeUtc is not { } childStart
        || childStart >= parentStart;
}
