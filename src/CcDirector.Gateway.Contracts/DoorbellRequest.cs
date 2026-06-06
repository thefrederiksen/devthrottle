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
