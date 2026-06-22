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

    /// <summary>
    /// Run-complete notification policy (epic #479 deferred piece, issue #622): one of the
    /// <see cref="CronNotify"/> values - <c>none</c> (default, opt-in: silent jobs stay silent),
    /// <c>always</c> (notify on every finish, success or failure), or <c>failure</c> (notify only
    /// when the fire failed to start / errored). The firing engine reads this on fire completion
    /// and, when it opts in, delivers a notification over the existing fleet notification channel
    /// (the per-Director doorbell event ring observed at <c>GET /directors/{id}/events</c>) and,
    /// when <see cref="NotifyWebhookUrl"/> is set, also POSTs the same payload to that URL.
    /// </summary>
    public string NotifyOn { get; set; } = CronNotify.None;

    /// <summary>
    /// Optional per-job outbound webhook (issue #622): when set, a run-complete notification (gated
    /// by <see cref="NotifyOn"/>) also POSTs a <see cref="CronRunCompletedPayload"/> to this URL, so
    /// an external consumer learns how the scheduled run went. Null/empty disables the webhook; the
    /// in-fleet notification still fires per <see cref="NotifyOn"/>.
    /// </summary>
    public string? NotifyWebhookUrl { get; set; }

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

/// <summary>
/// The accepted values of <see cref="CronJobDto.NotifyOn"/> (issue #622). Run-complete
/// notifications are opt-in per job: a job left at <see cref="None"/> never notifies, so existing
/// and deliberately-silent jobs are unaffected by the feature.
/// </summary>
public static class CronNotify
{
    /// <summary>No run-complete notification (the default - opt-in means silent unless asked).</summary>
    public const string None = "none";

    /// <summary>Notify on every fire finish, whether it started cleanly or failed.</summary>
    public const string Always = "always";

    /// <summary>Notify only when a fire failed to start / errored (a silent failed run is the worst case).</summary>
    public const string Failure = "failure";

    /// <summary>The three accepted policy values.</summary>
    public static readonly IReadOnlyList<string> All = new[] { None, Always, Failure };

    /// <summary>
    /// Normalize a policy string to one of <see cref="All"/>. Null/empty/whitespace maps to
    /// <see cref="None"/> (the opt-in default); the compare is case-insensitive.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return None;
        var trimmed = value.Trim();
        foreach (var policy in All)
        {
            if (string.Equals(policy, trimmed, StringComparison.OrdinalIgnoreCase))
                return policy;
        }
        return None;
    }

    /// <summary>True when a run with the given outcome should notify under the given policy.</summary>
    public static bool ShouldNotify(string? policy, bool succeeded)
    {
        var normalized = Normalize(policy);
        return normalized switch
        {
            Always => true,
            Failure => !succeeded,
            _ => false,           // None: never
        };
    }
}

/// <summary>
/// The run-complete notification payload (issue #622): the body delivered both as the in-fleet
/// notification (over the existing doorbell event ring) and to a per-job outbound webhook when one
/// is set. Carries everything a supervisor needs to know how a scheduled run went without polling:
/// the job name, the outcome (with infra-status vs task-status per #483), the target machine, the
/// resulting session id, and a deep link to that session.
/// </summary>
public sealed class CronRunCompletedPayload
{
    /// <summary>The cron job's id.</summary>
    public string JobId { get; set; } = "";

    /// <summary>The cron job's human-readable name.</summary>
    public string JobName { get; set; } = "";

    /// <summary>True when the fire started cleanly (session started / catch-up / work-list started); false when it failed to start or errored.</summary>
    public bool Succeeded { get; set; }

    /// <summary>The run's infra-status (did it START?) - e.g. <c>started</c> / <c>not-started</c> / <c>catch-up</c> / <c>worklist-*</c> (#483).</summary>
    public string InfraStatus { get; set; } = "";

    /// <summary>The run's task-status (did the WORK finish?) - <c>unknown</c> at fire time (#483).</summary>
    public string TaskStatus { get; set; } = "";

    /// <summary>The machine the job targeted (#503).</summary>
    public string Machine { get; set; } = "";

    /// <summary>The session the fire started, or null when no session started (a failure, or a work-list drain).</summary>
    public string? SessionId { get; set; }

    /// <summary>A deep link to the resulting session's view, or empty when there is no session to link.</summary>
    public string SessionLink { get; set; } = "";

    /// <summary>The failure / not-started reason when <see cref="Succeeded"/> is false, or null on success.</summary>
    public string? Reason { get; set; }

    /// <summary>When the run fired (UTC).</summary>
    public DateTime FiredUtc { get; set; }
}
