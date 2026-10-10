namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The answer of <c>GET /cron/problems</c> (Factory Control, step 1): the problems of an account's scheduled runs,
/// folded once on the Gateway. The factory page, the 08:00 email and the Factory Manager all read THIS answer, so
/// each shows the same problems in the same words.
/// </summary>
public sealed class CronRunProblemsDto
{
    /// <summary>How many problems are listed.</summary>
    public int Count { get; set; }

    /// <summary><c>open</c> when only problems still open are listed, <c>all</c> when cleared and resolved ones are
    /// listed too.</summary>
    public string State { get; set; } = CronRunProblemStates.Open;

    /// <summary>The factory the list was narrowed to, or null for the whole account.</summary>
    public string? Factory { get; set; }

    /// <summary>The account's time zone the shifts are counted in (an IANA or Windows id).</summary>
    public string TimeZone { get; set; } = "";

    /// <summary>The problems, newest first.</summary>
    public List<CronRunProblemDto> Problems { get; set; } = new();
}

/// <summary>One problem of one scheduled run.</summary>
public sealed class CronRunProblemDto
{
    /// <summary>The run, as <c>run resolve</c> names it.</summary>
    public string RunId { get; set; } = "";

    /// <summary>The schedule's id.</summary>
    public string JobId { get; set; } = "";

    /// <summary>The schedule's name.</summary>
    public string JobName { get; set; } = "";

    /// <summary>The factory the schedule belongs to, or null for a schedule in no factory.</summary>
    public string? Factory { get; set; }

    /// <summary>The factory seat the schedule runs as, or null.</summary>
    public string? Seat { get; set; }

    /// <summary>When the run was due (UTC).</summary>
    public DateTime ScheduledUtc { get; set; }

    /// <summary>When the run started its session (UTC), or null when it never started one.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>The shift the run belongs to: <c>night</c>, <c>morning</c> or <c>evening</c> - from its start, or
    /// from when it was due if it never started.</summary>
    public string Shift { get; set; } = "";

    /// <summary>The shift in words with its date and hours in the account's time zone, for example
    /// "night shift, Sat 11 Oct, 00:00-08:00".</summary>
    public string ShiftText { get; set; } = "";

    /// <summary>The kind of problem - one of the <see cref="CronRunProblems"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The kind in words, for example "did not report".</summary>
    public string KindText { get; set; } = "";

    /// <summary>The one line why.</summary>
    public string Reason { get; set; } = "";

    /// <summary>When the problem was recorded (UTC).</summary>
    public DateTime RecordedUtc { get; set; }

    /// <summary>The session to read to see what happened, or null when the run started none.</summary>
    public string? SessionId { get; set; }

    /// <summary><c>open</c>, <c>cleared</c> (a later run of the same schedule reported ok) or <c>resolved</c>
    /// (someone resolved it with a reason).</summary>
    public string State { get; set; } = CronRunProblemStates.Open;

    /// <summary>How it was cleared or resolved, in words; null while it is open.</summary>
    public string? StateText { get; set; }

    /// <summary>When it was cleared or resolved (UTC); null while it is open.</summary>
    public DateTime? ClosedUtc { get; set; }

    /// <summary>Who resolved it, when it was resolved by hand.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>The reason given when it was resolved by hand.</summary>
    public string? ResolvedReason { get; set; }

    /// <summary>The run that cleared it, when a later run reported ok.</summary>
    public string? ClearedByRunId { get; set; }
}

/// <summary>The states a scheduled run's problem can be in.</summary>
public static class CronRunProblemStates
{
    /// <summary>Not fixed yet.</summary>
    public const string Open = "open";

    /// <summary>The next run of the same schedule reported ok.</summary>
    public const string Cleared = "cleared";

    /// <summary>Someone resolved it with a one-line reason.</summary>
    public const string Resolved = "resolved";

    /// <summary>Every state - a list's filter, never a problem's state.</summary>
    public const string All = "all";
}

/// <summary>Body of <c>POST /cron/runs/result</c>: how the calling session's scheduled run went.</summary>
public sealed class CronRunResultRequest
{
    /// <summary><c>ok</c> or <c>problem</c>.</summary>
    public string? Result { get; set; }

    /// <summary>The one line why. Required for a problem.</summary>
    public string? Reason { get; set; }
}

/// <summary>The answer of <c>POST /cron/runs/result</c>.</summary>
public sealed class CronRunResultResponse
{
    /// <summary>The run, with the result on it.</summary>
    public CronRunRecord Run { get; set; } = new();

    /// <summary>The schedule's id.</summary>
    public string JobId { get; set; } = "";

    /// <summary>The schedule's name.</summary>
    public string JobName { get; set; } = "";

    /// <summary>True when the session should now close itself (the same as <c>session done</c>): after an ok result.
    /// After a problem the session stays open for someone to look at. The Gateway decides; the command follows.</summary>
    public bool CloseSession { get; set; }

    /// <summary>What happened, in words.</summary>
    public string Text { get; set; } = "";
}

/// <summary>Body of <c>POST /cron/runs/{runId}/resolve</c>.</summary>
public sealed class CronRunResolveRequest
{
    /// <summary>The one-line reason the problem is resolved. Required.</summary>
    public string? Reason { get; set; }
}
