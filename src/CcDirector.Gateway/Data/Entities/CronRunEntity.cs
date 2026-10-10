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
    /// The factory activity row that recorded this run's problem, so resolving the problem can mark that row handled
    /// (a correcting row) and the factory stops reading as failing. Null for a run in no factory.
    /// </summary>
    public Guid? ProblemActivityId { get; set; }
}
