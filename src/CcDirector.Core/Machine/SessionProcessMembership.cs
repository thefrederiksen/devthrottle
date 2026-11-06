namespace CcDirector.Core.Machine;

/// <summary>
/// A process, as one specific process: its id AND its start time. Ids are reused, so the id alone would
/// let a new process inherit a dead one's session.
/// </summary>
public readonly record struct ProcessIdentity(int Pid, DateTime? StartTimeUtc)
{
    /// <summary>The identity of a listed process.</summary>
    public static ProcessIdentity Of(ProcessReading process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return new ProcessIdentity(process.Pid, process.StartTimeUtc);
    }
}

/// <summary>
/// WHICH SESSION EACH PROCESS BELONGS TO, REMEMBERED FROM ONE SAMPLE TO THE NEXT.
///
/// Walking parent links (<see cref="ProcessTree"/>) loses a child the moment its parent exits: Linux and
/// macOS hand the orphan to process 1, and the next walk no longer reaches the session. A test run whose
/// shell wrapper finished would then drop out of its session's cost while it is still burning the
/// processor. So a process is remembered as a member once it has been seen in a session's tree, for as
/// long as that same process (same id AND start time) stays alive and the session still exists.
///
/// Immutable: <see cref="Update"/> returns the next membership and leaves the previous one unchanged, so
/// the sampler holds one value and replaces it each sample. Pure: no clock, no platform call.
/// </summary>
public sealed class SessionProcessMembership
{
    private readonly IReadOnlyDictionary<ProcessIdentity, string> _sessionByProcess;

    private SessionProcessMembership(IReadOnlyDictionary<ProcessIdentity, string> sessionByProcess)
    {
        _sessionByProcess = sessionByProcess;
    }

    /// <summary>No process belongs to any session.</summary>
    public static SessionProcessMembership Empty { get; } = new(new Dictionary<ProcessIdentity, string>());

    /// <summary>How many processes are remembered as members.</summary>
    public int Count => _sessionByProcess.Count;

    /// <summary>The session <paramref name="process"/> belongs to, or null when it belongs to none.</summary>
    public string? SessionOf(ProcessIdentity process) =>
        _sessionByProcess.TryGetValue(process, out var sessionId) ? sessionId : null;

    /// <summary>
    /// The membership after <paramref name="processes"/>: every member still alive and still in a live
    /// session is kept, and every process now in a session's tree is added. When the process list could
    /// not be read, nothing is learned and nothing is forgotten except members of sessions that have
    /// ended - "could not read" is never taken to mean "every process exited".
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

        foreach (var (sessionId, pids) in ProcessTree.Members(processes, rootPidBySessionId))
        {
            foreach (var pid in pids) next.TryAdd(ProcessIdentity.Of(byPid[pid]), sessionId);
        }

        return new SessionProcessMembership(next);
    }

    /// <summary>
    /// Each session's members that are in <paramref name="processes"/>, as process ids - the input
    /// <see cref="SessionUsageRollup.Sum"/> takes. A remembered member is matched by id AND start time, so
    /// a new process that reused a member's id is not counted.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<int>> Members(ProcessSetReading processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var members = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        if (!processes.CouldRead) return new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal);

        foreach (var process in processes.Processes)
        {
            if (SessionOf(ProcessIdentity.Of(process)) is not { } sessionId) continue;
            if (!members.TryGetValue(sessionId, out var pids)) members[sessionId] = pids = [];
            pids.Add(process.Pid);
        }

        return members.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<int>)pair.Value, StringComparer.Ordinal);
    }
}
