namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One recorded fire of a cron job (<see cref="Contracts.CronRunRecord"/>) in the EF data layer: a row in
/// the <c>cron_runs</c> child table, one per run. Runs are grouped by <see cref="JobId"/> and returned
/// newest-first, capped at 50 per job (<see cref="CronRunHistoryStore.MaxRecordsPerJob"/>).
///
/// <see cref="JobId"/> is a plain indexed column, NOT a foreign key: run history has an independent
/// lifecycle from the job definition (deleting a job does not delete its run history today), so no cascade
/// relationship is modeled.
///
/// Ordering is by <see cref="Sequence"/>, a code-assigned monotonically increasing value (highest = newest).
/// The JSON store preserved insertion order by prepending; <see cref="Sequence"/> reproduces that exactly
/// and survives the one-time import (records are assigned sequences in their stored newest-first order), so
/// a tie in <see cref="Contracts.CronRunRecord.FiredUtc"/> can never reorder the list.
/// </summary>
public sealed class CronRunEntity : GatewayMintedKeyEntity
{
    /// <summary>The owning cron job's id. Indexed; not a foreign key (independent lifecycle).</summary>
    public string JobId { get; set; } = "";

    /// <summary>Insertion order within a job, assigned in code. Highest is newest; ordering is by this DESC.</summary>
    public long Sequence { get; set; }

    public DateTime ScheduledUtc { get; set; }
    public DateTime FiredUtc { get; set; }
    public string Machine { get; set; } = "";
    public string TargetDirectorId { get; set; } = "";
    public string? SessionId { get; set; }
    public string InfraStatus { get; set; } = "";
    public string TaskStatus { get; set; } = "";

    /// <summary>
    /// How the run went (<see cref="Contracts.CronRunResults"/>; Factory Control, step 1). Rows recorded before results
    /// existed read <c>untracked</c> - the migration's default - so no old run is condemned for not reporting.
    /// </summary>
    public string Result { get; set; } = Contracts.CronRunResults.Untracked;

    /// <summary>The kind of problem (<see cref="Contracts.CronRunProblems"/>), when <see cref="Result"/> is a problem.</summary>
    public string? Problem { get; set; }

    /// <summary>The one line why.</summary>
    public string? ResultReason { get; set; }

    /// <summary>When the result was recorded.</summary>
    public DateTime? ResultUtc { get; set; }

    /// <summary>When someone resolved the problem by hand.</summary>
    public DateTime? ResolvedUtc { get; set; }

    /// <summary>Who resolved it.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>The reason they gave.</summary>
    public string? ResolvedReason { get; set; }

    /// <summary>
    /// The factory activity row written for this run's result (Factory Control, step 6). For a problem it is also the
    /// row a resolve marks handled. Null until written, and always null for a run in no factory.
    /// </summary>
    public Guid? ActivityId { get; set; }

    /// <summary>The correcting row written when this run's problem was resolved, so the factory stops reading as failing.</summary>
    public Guid? ResolveActivityId { get; set; }

    /// <summary>
    /// True while this run owes the factory activity record a row - set in the SAME write that records a result or a
    /// resolve, and cleared only once the row is written (or the schedule turns out to be in no factory). A write that
    /// fails leaves it set, and the cron sweep writes the row on its next pass: the result and its row cannot part.
    /// </summary>
    public bool ActivityDue { get; set; }

    /// <summary>The factory the run's activity rows go to, fixed when its result is recorded; null in no factory.</summary>
    public string? Factory { get; set; }

    /// <summary>The factory seat the run's activity rows name.</summary>
    public string? Seat { get; set; }

    /// <summary>The schedule's name when the result was recorded - the subject of the run's activity rows.</summary>
    public string? JobName { get; set; }

    /// <summary>On the last row of a long outage: how many more due times got no row of their own.</summary>
    public int MissedNotListed { get; set; }
}
