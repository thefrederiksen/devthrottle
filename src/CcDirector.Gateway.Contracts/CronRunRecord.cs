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

    /// <summary>The machine the job targets (#503).</summary>
    public string Machine { get; set; } = "";

    /// <summary>The Director the run actually resolved to / used (may be launched on demand), or empty if none.</summary>
    public string TargetDirectorId { get; set; } = "";

    /// <summary>The session the fire started, or null if no session started.</summary>
    public string? SessionId { get; set; }

    /// <summary>Did the session START? (e.g. <c>started</c> / <c>not-started</c> / <c>catch-up</c>.)</summary>
    public string InfraStatus { get; set; } = "";

    /// <summary>Did the WORK finish? (e.g. <c>completed</c> / <c>needs-human</c> / <c>unknown</c>.)</summary>
    public string TaskStatus { get; set; } = "";

    /// <summary>
    /// How the run's session ended - one of the <see cref="CronRunEndings"/> - folded by the Gateway on every read.
    /// Read-only: ignored on a write.
    /// </summary>
    public string? Ending { get; set; }

    /// <summary>The ending in words for the run table, for example "closed itself after 6 min". Read-only.</summary>
    public string? EndingText { get; set; }

    /// <summary>
    /// The run's own identifier, minted by the Gateway when the run is recorded - what <c>run resolve</c> names.
    /// Read-only: ignored on a write.
    /// </summary>
    public string RunId { get; set; } = "";

    /// <summary>
    /// How the run went - one of the <see cref="CronRunResults"/> (Factory Control, step 1). Kept apart from
    /// <see cref="TaskStatus"/>, which carries how the session ENDED: a run that reports ok then closes itself, and
    /// the close must not overwrite the report.
    /// </summary>
    public string Result { get; set; } = CronRunResults.Untracked;

    /// <summary>When <see cref="Result"/> is <c>problem</c>: which kind - one of the <see cref="CronRunProblems"/>.</summary>
    public string? Problem { get; set; }

    /// <summary>The one line why, for a problem; for an ok result, an optional note.</summary>
    public string? ResultReason { get; set; }

    /// <summary>When the result was recorded (UTC).</summary>
    public DateTime? ResultUtc { get; set; }

    /// <summary>When someone resolved this run's problem by hand (UTC), or null.</summary>
    public DateTime? ResolvedUtc { get; set; }

    /// <summary>Who resolved it: "you" for a person, otherwise the session that did.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>The one-line reason given when it was resolved.</summary>
    public string? ResolvedReason { get; set; }
}

/// <summary>
/// How a scheduled run went (Factory Control, step 1): the answer the run's own session gives at its end with
/// <c>cc-devthrottle run result</c>, or a problem the Gateway records for it.
/// </summary>
public static class CronRunResults
{
    /// <summary>The run was recorded before results existed, or it is a work-list drain, which starts many sessions
    /// and so has no one session to report. Nothing is expected of it.</summary>
    public const string Untracked = "untracked";

    /// <summary>The run's session started and has not reported yet.</summary>
    public const string Pending = "pending";

    /// <summary>The run's session reported that the work went well.</summary>
    public const string Ok = "ok";

    /// <summary>Something went wrong; <see cref="CronRunRecord.Problem"/> says what kind.</summary>
    public const string Problem = "problem";
}

/// <summary>The kinds of problem a scheduled run can have (Factory Control, step 1).</summary>
public static class CronRunProblems
{
    /// <summary>The run's own session reported a problem, with its one line why.</summary>
    public const string Reported = "reported";

    /// <summary>The run's session ended without reporting how the run went.</summary>
    public const string DidNotReport = "did-not-report";

    /// <summary>The run had not reported and was still open when its shift ended.</summary>
    public const string RanPastShift = "ran-past-its-shift";

    /// <summary>The run was due and never started a session.</summary>
    public const string DidNotRun = "did-not-run";

    /// <summary>The problem in words, for a list or an email.</summary>
    public static string Words(string? problem) => problem switch
    {
        Reported => "reported a problem",
        DidNotReport => "did not report",
        RanPastShift => "ran past its shift",
        DidNotRun => "did not run",
        _ => "a problem",
    };
}
