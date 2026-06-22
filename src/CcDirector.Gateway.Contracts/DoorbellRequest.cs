namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /directors/{id}/doorbell (issue #186): the Director announcing THAT a
/// session's mechanical activity state changed - never WHAT happened. The Gateway pulls
/// the truth (JSONL widgets, screen tail) over existing Director REST after the ping.
///
/// Fire-and-forget by design: no retries, no outbox, no ordering guarantees. A lost ping
/// is harmless because it carries no data - the 15s heartbeat (which snapshots every
/// session's state) is the reconcile channel that catches it.
/// </summary>
public sealed class DoorbellRequest
{
    /// <summary>The session whose state changed.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The new MECHANICAL state (rawState): Starting / Idle / Working /
    /// WaitingForInput / WaitingForPerm / Exited. A hypothesis from the Director's dumb
    /// 10s-quiet detector, not a verdict - the Gateway's brain may refute it.</summary>
    public string NewState { get; set; } = "";

    /// <summary>
    /// Optional event-vocabulary tag (issue #330, plan 1B): one of the
    /// <see cref="DoorbellEvents"/> names when this ping announces a lifecycle moment
    /// (session-created / session-exited / prompt-detected). Null/absent = a plain
    /// activity-transition ping, exactly the pre-#330 wire shape - old Directors never
    /// send it and old Gateways ignore it, so the field is compatible in both directions.
    /// Still fire-and-forget: an event ping carries nothing the heartbeat snapshot
    /// cannot reconcile.
    /// </summary>
    public string? Event { get; set; }
}

/// <summary>
/// The doorbell event vocabulary (issue #330). These are RAW mechanical notifications -
/// the Director announces THAT something happened, never what it means (interpretation
/// is the Gateway's job; Phase 3 consumes these via the event hub).
/// </summary>
public static class DoorbellEvents
{
    /// <summary>A session was created on the Director.</summary>
    public const string SessionCreated = "session-created";

    /// <summary>A session exited or was removed from the Director (announced once per session).</summary>
    public const string SessionExited = "session-exited";

    /// <summary>The terminal-state detector saw the session transition into a detected
    /// input-prompt state (WaitingForInput / WaitingForPerm). No prompt understanding -
    /// WHAT is being asked is Gateway interpretation (flagged assumption on issue #330).</summary>
    public const string PromptDetected = "prompt-detected";

    /// <summary>A scheduled cron job finished a fire (issue #622). Unlike the other events this
    /// one ORIGINATES in the Gateway's firing engine, not a Director doorbell ping, but it rides
    /// the same per-Director event ring so the fleet's existing notification channel carries it.
    /// The accompanying state is the run's infra-status (started / not-started / catch-up / ...).</summary>
    public const string CronRunCompleted = "cron-run-completed";
}

/// <summary>
/// One Director event as recorded by the Gateway's per-director event ring (issue #330) -
/// the minimal Phase-1 observable sink for the doorbell event vocabulary. The real
/// consumer (the SSE/WS event hub) lands in Phase 3.
/// </summary>
public sealed class DirectorEventDto
{
    /// <summary>When the Gateway received the event (UTC).</summary>
    public DateTime ReceivedAt { get; set; }

    /// <summary>The session the event concerns.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The <see cref="DoorbellEvents"/> name.</summary>
    public string Event { get; set; } = "";

    /// <summary>The session's mechanical state carried on the same ping.</summary>
    public string State { get; set; } = "";
}

/// <summary>
/// Optional body of POST /directors/{id}/heartbeat (issue #186). Old Directors send no
/// body (liveness only); new Directors include a snapshot of every session's mechanical
/// state so the heartbeat doubles as the reconcile channel for lost doorbell pings.
/// </summary>
public sealed class DirectorHeartbeatRequest
{
    public List<SessionStateSnapshot> Sessions { get; set; } = new();
}

/// <summary>One session's mechanical state at heartbeat time.</summary>
public sealed class SessionStateSnapshot
{
    public string SessionId { get; set; } = "";

    public string ActivityState { get; set; } = "";
}

/// <summary>
/// Body of POST /sessions/{sid}/assessment on a DIRECTOR (issue #186): the Gateway pushing
/// its assessed state back down for the Director's local UI. Stored as a display
/// annotation only - never fed into the detector, never re-pushed, wiped by new PTY bytes.
/// </summary>
public sealed class AssessmentRequest
{
    /// <summary>The Gateway's assessed state, or null to clear the annotation.</summary>
    public string? AssessedState { get; set; }
}
