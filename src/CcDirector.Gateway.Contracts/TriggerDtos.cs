namespace CcDirector.Gateway.Contracts;

/// <summary>
/// What one trigger check came to (the Website Business Factory mission, product track). Every check a
/// Director reports writes exactly one run row carrying one of these, whatever happened, so an empty check is
/// still visible as "ran, nothing to do" and silence is never mistaken for a quiet night.
/// </summary>
public static class TriggerRunOutcome
{
    /// <summary>The check ran and counted nothing. No session was started.</summary>
    public const string NothingToDo = "nothing-to-do";

    /// <summary>The check counted work and the Gateway started a session for it.</summary>
    public const string Started = "started";

    /// <summary>The check counted work, but the trigger is paused, so nothing was started.</summary>
    public const string Paused = "paused";

    /// <summary>The check counted work, but the session this trigger last started is still alive, so a
    /// second one was not started.</summary>
    public const string SkippedRunning = "skipped-running";

    /// <summary>The check did not keep its contract (it exited non-zero, timed out, printed something that is
    /// not JSON, or printed no integer count), or the session it asked for could not be started.</summary>
    public const string Failed = "failed";

    public static readonly string[] All = { NothingToDo, Started, Paused, SkippedRunning, Failed };
}

/// <summary>The two status words a trigger can have. The Gateway decides which; a client only renders it.</summary>
public static class TriggerStatusKind
{
    public const string Ok = "ok";
    public const string Red = "red";
}

/// <summary>
/// The body of <c>POST /triggers</c> and <c>PUT /triggers/{id}</c>. Every field is required on a create; on
/// an update a field left null keeps its current value.
/// </summary>
public sealed class TriggerDefinitionRequest
{
    /// <summary>The trigger's name, unique in the account, e.g. <c>website-new-mail</c>.</summary>
    public string? Name { get; set; }

    /// <summary>The factory this trigger belongs to, e.g. <c>website-factory</c>.</summary>
    public string? Factory { get; set; }

    /// <summary>The factory agent a started session is, e.g. <c>Front Desk</c>. It leads the session's name.</summary>
    public string? FactoryAgent { get; set; }

    /// <summary>The machine whose Director runs the check and where the session starts.</summary>
    public string? Machine { get; set; }

    /// <summary>The repository the session starts in. The check also runs in this folder.</summary>
    public string? RepoPath { get; set; }

    /// <summary>The check: any command that exits 0 and prints JSON with an integer <c>count</c>.</summary>
    public string? CheckCommand { get; set; }

    /// <summary>How often the check runs, in seconds. At least 60.</summary>
    public int? IntervalSeconds { get; set; }

    /// <summary>The first prompt of a started session. <c>{count}</c> is replaced with the check's count.</summary>
    public string? Prompt { get; set; }

    /// <summary>Whether the trigger starts paused. Defaults to false on a create.</summary>
    public bool? Paused { get; set; }
}

/// <summary>One trigger as the Gateway serves it, with its status already decided.</summary>
public sealed class TriggerDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Factory { get; set; } = "";
    public string FactoryAgent { get; set; } = "";
    public string Machine { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string CheckCommand { get; set; } = "";
    public int IntervalSeconds { get; set; }
    public string Prompt { get; set; } = "";
    public bool Paused { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }

    /// <summary>The session this trigger last started, or null when it has never started one.</summary>
    public string? LastSessionId { get; set; }

    /// <summary>When the last check was reported, or null when none has been.</summary>
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>The outcome of the last check, one of <see cref="TriggerRunOutcome"/>, or null.</summary>
    public string? LastOutcome { get; set; }

    /// <summary><see cref="TriggerStatusKind.Ok"/> or <see cref="TriggerStatusKind.Red"/>.</summary>
    public string Status { get; set; } = TriggerStatusKind.Ok;

    /// <summary>The status in words, ready to print: "OK", "check failed: exit code 1", "no checks ran".</summary>
    public string StatusText { get; set; } = "";
}

/// <summary>The answer to <c>GET /triggers</c>.</summary>
public sealed class TriggerListResponse
{
    public List<TriggerDto> Triggers { get; set; } = new();
}

/// <summary>One check, as the run history serves it.</summary>
public sealed class TriggerRunDto
{
    public string Id { get; set; } = "";
    public string TriggerId { get; set; } = "";
    public DateTime CheckedUtc { get; set; }
    public DateTime RecordedUtc { get; set; }
    public string Outcome { get; set; } = "";
    public int? Count { get; set; }
    public string? SessionId { get; set; }
    public string? Reason { get; set; }
    public string DirectorId { get; set; } = "";
}

/// <summary>
/// The 202 answer to a check report that counted work and began a session start. The start runs on the Gateway's
/// own lifetime, not the report's request, so this comes back at once; the check's run row - <c>started</c> with
/// the session id, or <c>failed</c> - is written when the start returns, and <c>GET /triggers/{id}/runs</c> shows it.
/// </summary>
public sealed class TriggerStartAccepted
{
    /// <summary>The trigger as the report named it (its id or its name).</summary>
    public string TriggerId { get; set; } = "";

    /// <summary>What the check counted.</summary>
    public int Count { get; set; }

    /// <summary>Always true: a session start was begun and holds the trigger's one-at-a-time lock.</summary>
    public bool Starting { get; set; } = true;
}

/// <summary>The answer to <c>GET /triggers/{id}/runs</c>, newest first.</summary>
public sealed class TriggerRunListResponse
{
    public string TriggerId { get; set; } = "";
    public List<TriggerRunDto> Runs { get; set; } = new();
}

/// <summary>
/// One trigger a Director must check, as <c>GET /directors/{directorId}/triggers</c> hands it out. Only what
/// running the check needs: the Director never decides anything from the result.
/// </summary>
public sealed class TriggerAssignmentDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CheckCommand { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public int IntervalSeconds { get; set; }

    /// <summary>How long the check may run before the Director kills it and reports a timeout.</summary>
    public int TimeoutSeconds { get; set; }
}

/// <summary>The answer to <c>GET /directors/{directorId}/triggers</c>.</summary>
public sealed class TriggerAssignmentResponse
{
    public List<TriggerAssignmentDto> Triggers { get; set; } = new();
}

/// <summary>
/// The body of <c>POST /directors/{directorId}/triggers/{id}/checks</c>: what running the check produced, raw.
/// The Director does not interpret it - the Gateway applies the check contract, so there is one reading of it.
/// </summary>
public sealed class TriggerCheckReport
{
    /// <summary>When the check finished, on the Director's clock (UTC).</summary>
    public DateTime CheckedAtUtc { get; set; }

    /// <summary>The process exit code, or null when it never exited (a timeout, or it could not start).</summary>
    public int? ExitCode { get; set; }

    /// <summary>True when the check ran past its timeout and was killed.</summary>
    public bool TimedOut { get; set; }

    /// <summary>Why the check could not even start (a missing folder, a missing shell), or null.</summary>
    public string? StartError { get; set; }

    /// <summary>What the check printed on standard output, capped by the Director.</summary>
    public string? Output { get; set; }

    /// <summary>What the check printed on standard error, capped by the Director.</summary>
    public string? ErrorOutput { get; set; }
}
