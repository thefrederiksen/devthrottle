namespace CcDirector.Gateway.Contracts;

/// <summary>
/// A scheduled job as it travels over the Gateway REST surface (epic #479, part 1 = issue #482).
/// A cron job says WHEN to run (<see cref="ScheduleKind"/> + <see cref="CronExpression"/> or
/// <see cref="RunAt"/> in <see cref="TimeZoneId"/>), on WHICH machine (<see cref="Target"/>), and
/// WHAT to run (<see cref="Action"/>).
///
/// This part of the feature persists and manages definitions only - it does NOT fire jobs (the
/// background engine is part 2, issue #483). <see cref="NextRunUtc"/> is computed by the Gateway
/// from the schedule + time zone; <see cref="LastFiredUtc"/>/<see cref="LastStatus"/> are written
/// by the firing engine in part 2 and are simply round-tripped here.
/// </summary>
public sealed class CronJobDto
{
    /// <summary>The job's unique id (its address in the REST surface). Assigned by the store on create.</summary>
    public string Id { get; set; } = "";

    /// <summary>Human-readable label.</summary>
    public string Name { get; set; } = "";

    /// <summary>When false the job is never evaluated for firing. Defaults to enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The schedule kind: <c>recurring</c> (uses <see cref="CronExpression"/>) or <c>oneOff</c>
    /// (uses <see cref="RunAt"/>). See <c>CronSchedule</c> for the accepted values.
    /// </summary>
    public string ScheduleKind { get; set; } = "";

    /// <summary>Standard 5-field cron expression; required when <see cref="ScheduleKind"/> is recurring.</summary>
    public string? CronExpression { get; set; }

    /// <summary>
    /// A one-off run time as a local timestamp (e.g. <c>2026-06-17T00:00:00</c>) interpreted in
    /// <see cref="TimeZoneId"/>; required when <see cref="ScheduleKind"/> is one-off.
    /// </summary>
    public string? RunAt { get; set; }

    /// <summary>IANA/Windows time-zone id (e.g. <c>America/Chicago</c>). All computed times are UTC.</summary>
    public string TimeZoneId { get; set; } = "";

    /// <summary>Which machine the job runs on.</summary>
    public CronJobTarget Target { get; set; } = new();

    /// <summary>What the fired session runs.</summary>
    public CronJobAction Action { get; set; } = new();

    /// <summary>
    /// When true (default) a fire is skipped while a prior run of the same job is still in flight.
    /// Honored by the firing engine (part 2, #483); stored here.
    /// </summary>
    public bool PreventOverlap { get; set; } = true;

    /// <summary>UTC instant the job was created. Set by the store on create.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>UTC instant the job last fired, or null if it never has. Written by the firing engine (#483).</summary>
    public DateTime? LastFiredUtc { get; set; }

    /// <summary>The next UTC instant the job is due, computed by the Gateway from the schedule, or null if none.</summary>
    public DateTime? NextRunUtc { get; set; }

    /// <summary>Outcome of the most recent run, or null. Written by the firing engine (#483).</summary>
    public string? LastStatus { get; set; }
}

/// <summary>
/// The machine a cron job runs on (epic #479, #503). A job targets a MACHINE, not a specific
/// Director: a DirectorId is per-process and changes on restart, so the engine resolves the machine
/// to an available Director at fire time (launching one via the launcher if none is running).
/// </summary>
public sealed class CronJobTarget
{
    /// <summary>The target machine name (from <c>GET /directors</c>' machineName).</summary>
    public string Machine { get; set; } = "";
}

/// <summary>
/// What a cron job runs when it fires (epic #479). Two action shapes:
///   - SEED (parts 1-2): start one session on the target Director seeded with <see cref="Seed"/>.
///   - WORK LIST (part 3, #484): when <see cref="WorkListName"/> is set, the fire instead triggers
///     the named-work-list runner (#274) to drain that list on the target Director - the headline
///     "midnight, run the loop over a list of work items" use case.
/// A job sets one or the other; <see cref="WorkListName"/> takes precedence when both are present.
/// </summary>
public sealed class CronJobAction
{
    /// <summary>Working directory for the session(s) the fire starts.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>The skill or prompt text a seed-action session runs (e.g. <c>/help</c>). Optional when <see cref="WorkListName"/> is set.</summary>
    public string Seed { get; set; } = "";

    /// <summary>
    /// When set, the fire drains this named work list (#274) on the target Director instead of
    /// starting a single seeded session. Null/empty = a seed action.
    /// </summary>
    public string? WorkListName { get; set; }
}
