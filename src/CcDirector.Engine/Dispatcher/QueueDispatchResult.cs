namespace CcDirector.Engine.Dispatcher;

/// <summary>
/// Outcome categories for an on-demand, by-id queue dispatch (issue #329, the
/// <c>POST /dispatch</c> verb). These are EXPECTED failures modeled as results
/// (CodingStyle.md section 2), not exceptions: refusing an unapproved item is the
/// approval gate working, not an error in the dispatcher.
/// </summary>
public enum QueueDispatchOutcome
{
    /// <summary>The approved item was sent through its channel and marked posted.</summary>
    Dispatched,

    /// <summary>No communications row exists with the given id.</summary>
    NotFound,

    /// <summary>
    /// The item exists but is not in the 'approved' state (pending_review, rejected,
    /// already posted, ...). NOTHING was sent - the verb only executes an
    /// already-made human approval decision.
    /// </summary>
    NotApproved,

    /// <summary>The item is approved but its platform has no machine-bound sender here (only email today).</summary>
    UnsupportedPlatform,

    /// <summary>The item is approved but malformed (missing/unparseable email_specific or no recipients).</summary>
    InvalidItem,

    /// <summary>The send was attempted and the channel/provider failed (no route, tool exit != 0, tool crash).</summary>
    SendFailed,
}

/// <summary>
/// Result of <see cref="CommunicationDispatcher.DispatchByIdAsync"/>: what happened to
/// the one queue item the caller named, with enough detail for the Control API to map
/// it onto an HTTP response and for the audit trail to be self-explanatory.
/// </summary>
public sealed record QueueDispatchResult(
    QueueDispatchOutcome Outcome,
    string QueueItemId,
    int TicketNumber = 0,
    string? ItemStatus = null,
    string? Channel = null,
    string? Error = null)
{
    /// <summary>True only when the item actually went out and advanced to posted.</summary>
    public bool Dispatched => Outcome == QueueDispatchOutcome.Dispatched;
}

/// <summary>Captured output of one channel-tool invocation (exit code + both streams).</summary>
public sealed record ToolProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// The channel boundary of the dispatcher: runs one send-tool invocation. Production uses
/// the real process runner (argument-list <c>Process.Start</c>, no shell); tests inject a
/// mock channel so NOTHING real can ever send from a test.
/// </summary>
public delegate Task<ToolProcessResult> ToolProcessRunner(string toolPath, IReadOnlyList<string> args);
