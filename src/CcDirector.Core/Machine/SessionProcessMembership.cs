namespace CcDirector.Core.Machine;

/// <summary>
/// One process, told apart from any later process that reuses its id by the time it started.
/// </summary>
/// <param name="Pid">The process id.</param>
/// <param name="StartTimeUtc">When it started. Null when the platform does not offer it.</param>
public readonly record struct ProcessIdentity(int Pid, DateTime? StartTimeUtc)
{
    /// <summary>The identity of a process in a reading.</summary>
    public static ProcessIdentity Of(ProcessReading process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new ProcessIdentity(process.Pid, process.StartTimeUtc);
    }
}

/// <summary>
/// WHICH SESSION EACH PROCESS BELONGS TO, REMEMBERED ACROSS SAMPLES - so a child process that detaches
/// from its parent is still counted against the session that started it.
///
/// <see cref="ProcessTreeUsage.BySession"/> finds a session's processes by walking parent process ids,
/// and that walk loses a child whose parent has died: on Windows the child keeps naming a parent id that
/// no longer exists, and on Linux it is handed to process 1 or a subreaper (phase 1 Windows finding,
/// <c>missions/resource-monitor/phase-1/windows-detached-children.md</c>). A test run that outlives the
/// shell that started it would then vanish from its session's figures while still loading the machine.
///
/// So once a process has been seen under a session, it is remembered by <see cref="ProcessIdentity"/> -
/// its id AND its start time - and stays that session's for as long as that same process is alive. It
/// is forgotten when its id is gone from the process list, when the id now belongs to a process with a
/// different start time (the id was reused), or when its session is no longer one of the roots handed in.
///
/// THE VALUE IS IMMUTABLE AND ITS UPDATE IS A PURE FUNCTION: previous membership, the new process list
/// and the session roots in; the new membership out. No clock and no platform call, so it is tested in
/// <c>CcDirector.Core.UnitTests</c>. The sampler that calls it once per sample holds the latest value;
/// that is the only state, and it lives with the caller.
/// </summary>
public sealed class SessionProcessMembership
{
    private readonly IReadOnlyDictionary<ProcessIdentity, string> _sessionByProcess;

    private SessionProcessMembership(IReadOnlyDictionary<ProcessIdentity, string> sessionByProcess)
    {
        _sessionByProcess = sessionByProcess;
    }

    /// <summary>No process remembered under any session - the membership before the first sample.</summary>
    public static SessionProcessMembership Empty { get; } = new(new Dictionary<ProcessIdentity, string>());

    /// <summary>How many processes are remembered, across every session.</summary>
    public int Count => _sessionByProcess.Count;

    /// <summary>The session the process with this identity belongs to, or null when it belongs to none.</summary>
    public string? SessionOf(ProcessIdentity process) =>
        _sessionByProcess.TryGetValue(process, out var sessionId) ? sessionId : null;

    /// <summary>
    /// The membership after one more sample.
    ///
    /// A process the membership already holds keeps its session while it lives, even if its parent has
    /// died or it now sits under another session's tree - it was that session's first, and moving it
    /// would count it against a session that did not start it. A process not yet held is given to the
    /// session whose tree the parent walk finds it in. A process with no start time is remembered by its
    /// id alone, because the platform offers nothing better; such an id cannot be told from a reused one.
    ///
    /// An UNREADABLE process list changes nothing: it says nothing about which processes are gone, so
    /// forgetting them would make sessions lose their children on one failed read. The previous
    /// membership is returned, minus sessions that have ended.
    /// </summary>
    public static SessionProcessMembership Update(
        SessionProcessMembership previous,
        ProcessSetReading processes,
        IReadOnlyDictionary<string, int> rootPidBySessionId)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(rootPidBySessionId);

        var liveSessions = new HashSet<string>(rootPidBySessionId.Keys, StringComparer.Ordinal);
        var next = new Dictionary<ProcessIdentity, string>();

        if (!processes.CouldRead)
        {
            foreach (var (identity, sessionId) in previous._sessionByProcess)
            {
                if (liveSessions.Contains(sessionId)) next[identity] = sessionId;
            }
            return new SessionProcessMembership(next);
        }

        var alive = new HashSet<ProcessIdentity>();
        var byPid = new Dictionary<int, ProcessReading>();
        foreach (var process in processes.Processes)
        {
            alive.Add(ProcessIdentity.Of(process));
            byPid.TryAdd(process.Pid, process);
        }

        foreach (var (identity, sessionId) in previous._sessionByProcess)
        {
            if (alive.Contains(identity) && liveSessions.Contains(sessionId)) next[identity] = sessionId;
        }

        foreach (var usage in ProcessTreeUsage.BySession(processes, rootPidBySessionId))
        {
            foreach (var pid in usage.Pids)
            {
                next.TryAdd(ProcessIdentity.Of(byPid[pid]), usage.SessionId);
            }
        }

        return new SessionProcessMembership(next);
    }

    /// <summary>
    /// What each session costs the machine in this process list, by remembered membership: every live
    /// process held under the session, summed. A session with no live process in the list is left out
    /// rather than shown as costing zero. Sessions come back in the order of their ids.
    ///
    /// The root process id reported is the one handed in for the session; it may have exited while its
    /// detached children still run. An unreadable process list gives an empty list.
    /// </summary>
    public IReadOnlyList<SessionUsage> Usage(ProcessSetReading processes, IReadOnlyDictionary<string, int> rootPidBySessionId)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(rootPidBySessionId);
        if (!processes.CouldRead) return Array.Empty<SessionUsage>();

        var bySession = new Dictionary<string, List<ProcessReading>>(StringComparer.Ordinal);
        var counted = new HashSet<int>();
        foreach (var process in processes.Processes.OrderBy(process => process.Pid))
        {
            if (!counted.Add(process.Pid)) continue;
            if (SessionOf(ProcessIdentity.Of(process)) is not { } sessionId) continue;
            if (!rootPidBySessionId.ContainsKey(sessionId)) continue;

            if (!bySession.TryGetValue(sessionId, out var members))
            {
                members = new List<ProcessReading>();
                bySession[sessionId] = members;
            }
            members.Add(process);
        }

        var result = new List<SessionUsage>();
        foreach (var (sessionId, members) in bySession.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            double cpu = 0;
            ulong memory = 0;
            foreach (var member in members)
            {
                cpu += member.CpuPercent;
                memory += member.MemoryBytes;
            }
            result.Add(new SessionUsage(sessionId, rootPidBySessionId[sessionId], cpu, memory, members.Select(member => member.Pid).ToList()));
        }
        return result;
    }
}
