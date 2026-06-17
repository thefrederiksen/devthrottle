namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One execution of a cron job as it travels over the Gateway REST surface (epic #479). Defined in
/// part 1 (issue #482) so the contract is fixed; records are PRODUCED by the firing engine in
/// part 2 (issue #483).
///
/// The two status fields are deliberately separate (the lesson from Claude Code routines): a green
/// <see cref="InfraStatus"/> means the session started without an infrastructure error - it does
/// NOT mean the work succeeded. <see cref="TaskStatus"/> carries whether the work itself finished.
/// </summary>
public sealed class CronRunRecord
{
    /// <summary>The UTC instant the run was due.</summary>
    public DateTime ScheduledUtc { get; set; }

    /// <summary>The UTC instant the run actually fired (may be staggered/jittered).</summary>
    public DateTime FiredUtc { get; set; }

    /// <summary>The Director the run targeted.</summary>
    public string TargetDirectorId { get; set; } = "";

    /// <summary>The session the fire started, or null if no session started.</summary>
    public string? SessionId { get; set; }

    /// <summary>Did the session START? (e.g. <c>started</c> / <c>not-started</c> / <c>catch-up</c>.)</summary>
    public string InfraStatus { get; set; } = "";

    /// <summary>Did the WORK finish? (e.g. <c>completed</c> / <c>needs-human</c> / <c>unknown</c>.)</summary>
    public string TaskStatus { get; set; } = "";
}
