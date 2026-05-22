namespace CcDirector.Core.Scheduler;

/// <summary>
/// Declares how a single comm-queue runner is invoked by the scheduler.
///
/// Each registration tells the scheduler:
///   * which queue items to scan for (QueueFilter, a SQL WHERE clause against
///     the communications.db `communications` table),
///   * what process to spawn when items exist (Command + Args),
///   * when to fire (Schedule),
///   * whether to add 0-60min human-cadence jitter (RespectHumanCadence),
///   * the minimum gap between successive fires (MinIntervalBetweenFires).
///
/// The runner process is responsible for processing items and marking each
/// posted via `cc-comm-queue mark-posted`. The scheduler does not touch the
/// DB state itself; it only decides when to invoke the runner.
/// </summary>
public sealed class RunnerRegistration
{
    public required string Name { get; init; }

    /// <summary>SQL WHERE clause for `SELECT COUNT(*) FROM communications WHERE ...`.</summary>
    public required string QueueFilter { get; init; }

    public required string Command { get; init; }
    public required string[] Args { get; init; }
    public required Schedule Schedule { get; init; }

    /// <summary>When true, the scheduler delays the actual fire by a uniform 0-60 minutes
    /// so that automated sends don't pattern-match as bot traffic.</summary>
    public bool RespectHumanCadence { get; init; }

    /// <summary>Minimum elapsed time between two consecutive fires of this runner.
    /// Acts as a tick-storm guard during failover or scheduler restarts.</summary>
    public TimeSpan MinIntervalBetweenFires { get; init; } = TimeSpan.FromMinutes(30);
}
