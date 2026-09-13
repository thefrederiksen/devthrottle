using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// RAISES THE THREAD POOL FLOOR SO A TIMER CALLBACK IS NOT QUEUED BEHIND SESSION WORK (issue #2818).
///
/// THE MEASUREMENT THIS IS SIZED FROM. Every session occupies TWO thread-pool workers for its whole
/// life, on both platforms: <c>ProcessHost.StartDrainLoop</c> does synchronous terminal reads inside a
/// <c>Task.Run</c>, and <c>ProcessHost.StartExitMonitor</c> waits for process exit inside another
/// (<c>UnixProcessHost</c> does the same two). They are blocked, not merely busy, so they never return
/// a worker to the pool. Eighteen sessions - the load on the machine in issue #2818 - therefore pin 36
/// workers before any other work is considered.
///
/// The default floor is the processor count. Above the floor the pool injects new threads at roughly
/// one to two per second, so on an eight-processor machine with eighteen sessions EVERYTHING else -
/// including the keep-alive ping that holds the Gateway tunnel open, which is a timer callback on this
/// same pool - waits in a queue behind an injection rate measured in seconds. The tunnel's tolerance
/// for silence is thirty seconds. That is how a Director with sessions on it gets declared dead by a
/// Gateway that is alive and answering.
///
/// WHY THIS IS NOT EXPENSIVE. <c>SetMinThreads</c> does not create threads. It raises the number the
/// pool may create ON DEMAND without waiting out the injection delay, so an idle Director pays nothing
/// and a busy one pays only for the threads it actually needs. Microsoft's own guidance warns that
/// raising it can cost memory and scheduling contention; that warning is about pools sized far beyond
/// the work, which is why the number here is derived from the measured per-session cost rather than
/// picked large.
///
/// WHAT THIS DOES NOT FIX, AND THE HONESTY MATTERS. This addresses WORKER STARVATION only. If the
/// machine is paging so hard that the Director's own pages are evicted, no pool setting will get a
/// callback onto a processor in time, and the tunnel will still drop. This narrows the window; it does
/// not close it. The silence tolerance itself is deliberately left alone - shortening it hangs up on a
/// merely busy peer and lengthening it delays noticing a dead one, as
/// <c>DirectorStreamLimits.SilenceTolerance</c> already records.
/// </summary>
public static class ThreadPoolFloor
{
    /// <summary>
    /// Thread-pool workers each session blocks for its whole life - the drain loop and the exit
    /// monitor. Measured from <c>ProcessHost</c> and <c>UnixProcessHost</c>, not estimated.
    /// </summary>
    public const int BlockingWorkersPerSession = 2;

    /// <summary>
    /// Sessions one Director is sized to carry. The machine in issue #2818 was running eighteen; this
    /// is set well above that so the floor does not need revisiting the first time somebody opens a few
    /// more, and the cost of the headroom is nothing until the sessions exist.
    /// </summary>
    public const int SessionCeiling = 32;

    /// <summary>
    /// Workers left over for everything that is not a session: the Gateway tunnel and its keep-alive,
    /// the roster push, dictation, the update checker, and the user interface's own background work.
    /// </summary>
    public const int Headroom = 16;

    /// <summary>
    /// The worker floor this machine should have. A pure function of the processor count, so the sizing
    /// rule is testable without touching the real pool.
    /// </summary>
    public static int RequiredWorkerThreads(int processorCount) =>
        processorCount + (BlockingWorkersPerSession * SessionCeiling) + Headroom;

    /// <summary>
    /// The completion-port floor. Raised SEPARATELY and by much less, because the justification is
    /// different: no session blocks a completion-port thread - the drain loops and exit monitors are
    /// workers - so this is not sized per session. It is raised only so the tunnel's own socket
    /// completions, which do land here, are not themselves waiting out an injection delay on a busy
    /// Director.
    /// </summary>
    public static int RequiredCompletionPortThreads(int processorCount) => processorCount + Headroom;

    /// <summary>
    /// Raise the floor, never lower it, and report what happened.
    ///
    /// NEVER LOWERS. Another component may have raised the floor already for its own reasons; taking
    /// the maximum means this can only ever add room.
    /// </summary>
    /// <returns>True when the pool accepted the values or already met them.</returns>
    public static bool Apply() => Apply(Environment.ProcessorCount, ThreadPool.GetMinThreads, ThreadPool.SetMinThreads);

    /// <summary>The testable body of <see cref="Apply()"/>, with the pool passed in as two delegates.</summary>
    internal static bool Apply(int processorCount, GetMinThreads get, SetMinThreads set)
    {
        get(out var currentWorkers, out var currentCompletionPorts);

        var wantWorkers = Math.Max(currentWorkers, RequiredWorkerThreads(processorCount));
        var wantCompletionPorts = Math.Max(currentCompletionPorts, RequiredCompletionPortThreads(processorCount));

        if (wantWorkers == currentWorkers && wantCompletionPorts == currentCompletionPorts)
        {
            FileLog.Write($"[ThreadPoolFloor] Apply: already at or above the floor " +
                          $"(workers={currentWorkers}, completionPorts={currentCompletionPorts}) - nothing changed.");
            return true;
        }

        var accepted = set(wantWorkers, wantCompletionPorts);

        if (accepted)
            FileLog.Write($"[ThreadPoolFloor] Apply: raised the thread pool floor from " +
                          $"workers={currentWorkers}/completionPorts={currentCompletionPorts} to " +
                          $"workers={wantWorkers}/completionPorts={wantCompletionPorts} " +
                          $"({processorCount} processors, {BlockingWorkersPerSession} blocked workers per session, " +
                          $"sized for {SessionCeiling} sessions plus {Headroom} spare). Threads are created on demand, " +
                          "so an idle Director pays nothing for this.");
        else
            // A REFUSAL IS REPORTED, NOT SWALLOWED. If the pool declines, the Director keeps running with
            // the floor it had - and the tunnel keeps the fault this was meant to narrow, so the line has
            // to say so rather than leaving a silent no-op behind.
            FileLog.Write($"[ThreadPoolFloor] Apply REFUSED by the runtime: asked for " +
                          $"workers={wantWorkers}/completionPorts={wantCompletionPorts} and it declined. " +
                          $"The floor is still workers={currentWorkers}/completionPorts={currentCompletionPorts}, so a " +
                          "timer callback can still queue behind session work on a busy Director.");

        return accepted;
    }

    /// <summary>Matches <see cref="ThreadPool.GetMinThreads"/> so the real pool can be passed as a delegate.</summary>
    internal delegate void GetMinThreads(out int workerThreads, out int completionPortThreads);

    /// <summary>Matches <see cref="ThreadPool.SetMinThreads"/> so a test can observe and refuse.</summary>
    internal delegate bool SetMinThreads(int workerThreads, int completionPortThreads);
}
