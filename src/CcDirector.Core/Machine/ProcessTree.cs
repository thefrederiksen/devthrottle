namespace CcDirector.Core.Machine;

/// <summary>
/// ONE WAY TO FIND A SESSION'S MEMBERS: walk each process's parent links up to a session's root.
///
/// It is correct where the operating system's parent links are: on Linux and macOS a child keeps its
/// parent's id until the parent exits. It is NOT a fact about Windows, where a detached child is not
/// tracked (issue #4624); a Windows caller must decide whether to use it. See
/// <see cref="SessionUsageRollup"/>, which takes membership as an input for exactly that reason, and
/// <see cref="SessionProcessMembership"/>, which remembers a member after its parent has exited.
///
/// A pure function of one process list.
/// </summary>
public static class ProcessTree
{
    /// <summary>
    /// Each session's member process ids, its root included, found by following parent links. A session
    /// whose root process is not in the list has no members. A process is a member of at most one session:
    /// two sessions claiming one root keep it with the first by session id.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<int>> Members(
        ProcessSetReading processes,
        IReadOnlyDictionary<string, int> rootPidBySessionId)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(rootPidBySessionId);
        var result = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal);
        if (!processes.CouldRead || rootPidBySessionId.Count == 0) return result;

        var byPid = new Dictionary<int, ProcessReading>();
        foreach (var process in processes.Processes) byPid.TryAdd(process.Pid, process);

        var sessionByRootPid = new Dictionary<int, string>();
        foreach (var (sessionId, rootPid) in rootPidBySessionId.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (byPid.ContainsKey(rootPid)) sessionByRootPid.TryAdd(rootPid, sessionId);
        }

        var members = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var process in byPid.Values.OrderBy(process => process.Pid))
        {
            if (OwningSession(process, byPid, sessionByRootPid) is not { } sessionId) continue;
            if (!members.TryGetValue(sessionId, out var pids)) members[sessionId] = pids = [];
            pids.Add(process.Pid);
        }

        foreach (var (sessionId, pids) in members) result[sessionId] = pids;
        return result;
    }

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
    /// Whether <paramref name="parent"/> can really be <paramref name="child"/>'s parent. Process ids are
    /// reused: a child whose parent exited can name an id that now belongs to a NEWER process, and
    /// following that link would hand the child to the wrong session. A parent that started after its
    /// child is not its parent. When either start time is unknown the link is taken as given.
    /// </summary>
    internal static bool CanBeParent(ProcessReading parent, ProcessReading child) =>
        parent.StartTimeUtc is not { } parentStart
        || child.StartTimeUtc is not { } childStart
        || childStart >= parentStart;
}
