namespace CcDirector.Core.Machine;

/// <summary>
/// WHAT EACH SESSION COSTS THE MACHINE: its member processes' processor shares and memory, summed
/// (mission section 5a, "per-session memory and processor, child processes included").
///
/// MEMBERSHIP IS AN INPUT, NOT A FACT THIS DERIVES. Which processes belong to which session is handed in.
/// On Linux and macOS a session's tree can be found from parent links (<see cref="ProcessTree"/>), and
/// <see cref="SessionProcessMembership"/> remembers members whose parent has since exited. On Windows the
/// parent links are not reliable - a child that detaches is not tracked without a Job Object, issue
/// #4624, deliberately out of scope - so the caller decides what membership it can honestly claim, and
/// this sums exactly that and nothing more.
///
/// AN UNKNOWN IS NEVER SUMMED AS ZERO. A member whose processor share or memory is null is left out of
/// that sum and COUNTED, in <see cref="SessionUsage.ProcessesWithUnknownProcessor"/> and
/// <see cref="SessionUsage.ProcessesWithUnknownMemory"/>, so a screen says "at least 2.1 GB" instead of
/// reporting a session cheaper than it is - a plausible wrong number. A member listed in the membership
/// but absent from this sample's process list has exited and is not counted at all.
///
/// A pure function: no clock, no platform call, no state.
/// </summary>
public static class SessionUsageRollup
{
    /// <summary>
    /// One usage per session that has at least one member in <paramref name="processes"/>, ordered by
    /// session id. Empty when the process list could not be read - nothing can be summed, and no session
    /// is reported as costing nothing.
    /// </summary>
    /// <param name="processes">The sample's process list.</param>
    /// <param name="membersBySessionId">Each session's member process ids, the root included.</param>
    /// <param name="rootPidBySessionId">Each session's root process - the agent itself.</param>
    public static IReadOnlyList<SessionUsage> Sum(
        ProcessSetReading processes,
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> membersBySessionId,
        IReadOnlyDictionary<string, int> rootPidBySessionId)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(membersBySessionId);
        ArgumentNullException.ThrowIfNull(rootPidBySessionId);
        if (!processes.CouldRead) return [];

        // One process per id. A list naming the same id twice is a torn read; the first is kept so the
        // result does not depend on dictionary order.
        var byPid = new Dictionary<int, ProcessReading>();
        foreach (var process in processes.Processes) byPid.TryAdd(process.Pid, process);

        // A process claimed by two sessions is counted once, by the first session in id order, so no
        // machine total is ever more than the machine.
        var claimed = new HashSet<int>();
        var result = new List<SessionUsage>();
        foreach (var (sessionId, members) in membersBySessionId.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!rootPidBySessionId.TryGetValue(sessionId, out var rootPid)) continue;

            double cpu = 0;
            ulong memory = 0;
            var unknownCpu = 0;
            var unknownMemory = 0;
            var pids = new List<int>();
            foreach (var pid in members.Order())
            {
                if (!byPid.TryGetValue(pid, out var process) || !claimed.Add(pid)) continue;
                pids.Add(pid);
                if (process.CpuPercent is { } share) cpu += share; else unknownCpu++;
                if (process.MemoryBytes is { } bytes) memory += bytes; else unknownMemory++;
            }

            if (pids.Count == 0) continue;
            result.Add(new SessionUsage(sessionId, rootPid, cpu, memory, pids, unknownCpu, unknownMemory));
        }

        return result;
    }
}
