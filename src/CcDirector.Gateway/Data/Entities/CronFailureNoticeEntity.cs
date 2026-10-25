namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// When a schedule's owner was last told that it failed for one cause (issue #4436): one row per (account, schedule,
/// cause) in <c>cron_failure_notices</c>. A broken schedule firing every five minutes would otherwise notify its owner
/// every five minutes; with this it notifies at most once an hour for each cause, and the failures held back in between
/// are counted onto the next notice. Kept in the database, not in memory, so a Gateway restart neither tells the same
/// failure twice nor forgets that an hour has not passed.
/// </summary>
public sealed class CronFailureNoticeEntity : TenantScopedEntity
{
    /// <summary>The schedule's id.</summary>
    public string JobId { get; set; } = "";

    /// <summary>The cause of the failure, one of <see cref="Running.ScheduleFailureCauses"/>.</summary>
    public string Cause { get; set; } = "";

    /// <summary>When the owner was last told of a failure of this cause (UTC).</summary>
    public DateTime LastSentUtc { get; set; }

    /// <summary>The failures of this cause since then that were not told, because the hour had not passed.</summary>
    public int HeldBack { get; set; }

    /// <summary>When the newest failure counted here was recorded (its result time, UTC). A failure recorded at or
    /// before it was already told or held back - a Gateway that restarts reads a little of the run history again - so
    /// it is not counted twice.</summary>
    public DateTime LastCountedUtc { get; set; }

    /// <summary>The run counted last, at <see cref="LastCountedUtc"/>. Several runs can carry one result time - a late sweep
    /// records every run it skipped in one write - so a run at that same time is new unless it is this one.</summary>
    public string LastCountedRunId { get; set; } = "";
}
