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

    /// <summary>How many runs the listed problems stand for: <see cref="Count"/>, plus every due time of a long outage
    /// that was counted on its last row instead of getting a row of its own.</summary>
    public int Occurrences { get; set; }

    /// <summary>The instant the list describes (UTC): a problem raised after it is not listed, and a clear or resolve
    /// after it has not happened yet. Now, unless the reader asked for an earlier instant.</summary>
    public DateTime AsOfUtc { get; set; }

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

    /// <summary>What went wrong as one finished sentence, for example "Did not run: its machine was offline when it
    /// was due." Every surface shows this text as it is.</summary>
    public string Sentence { get; set; } = "";

    /// <summary>When the run started, or was due if it never started, in the account's time zone, for example
    /// "Started Fri 10 Oct 09:00" or "Due Sat 11 Oct 02:00".</summary>
    public string WhenText { get; set; } = "";

    /// <summary>When the problem was raised (UTC) - the instant it was recorded on the run.</summary>
    public DateTime RaisedAtUtc { get; set; }

    /// <summary>How many runs this problem stands for: 1, or more on the last row of an outage longer than the rows
    /// recorded one by one - that row also counts the due times past the limit.</summary>
    public int Occurrences { get; set; } = 1;

    /// <summary>How long it has been open at the list's as-of instant, for example "open 3 hours"; null once it is
    /// cleared or resolved.</summary>
    public string? OpenForText { get; set; }

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

    /// <summary>True when the session should now close itself (the same as <c>session done</c>): after an ok result,
    /// when the run's schedule is set to close itself. With that setting off the session stays open after ok. After a
    /// problem the session stays open for someone to look at. The Gateway decides; the command follows.</summary>
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

/// <summary>
/// One factory's last day of scheduled runs, for the 08:00 email's one line per factory (Factory Control, step 4).
/// Folded by <c>CronRunResultFold.FactoryDays</c>; the email renders the numbers verbatim.
/// </summary>
public sealed class CronFactoryDayDto
{
    /// <summary>The factory's id, as its schedules name it.</summary>
    public string Factory { get; set; } = "";

    /// <summary>Runs that started in the window and reported ok.</summary>
    public int Done { get; set; }

    /// <summary>Runs that were due in the window and never started (problem kind <c>did-not-run</c>).</summary>
    public int DidNotRun { get; set; }

    /// <summary>Runs whose check counted nothing in the window, so no session was needed (Factory Definition, phase
    /// 5b). Never counted as did not run.</summary>
    public int NothingToDo { get; set; }

    /// <summary>The factory's seats with a run whose session is still open now, whatever the run's age - the same fact
    /// the Factories list's "stayed open" marker shows.</summary>
    public int LeftOpen { get; set; }

    /// <summary>How many of the factory's problems are still open.</summary>
    public int OpenProblems { get; set; }
}
