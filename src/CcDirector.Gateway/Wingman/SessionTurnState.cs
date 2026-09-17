using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Whether a session is INSIDE A TURN, as its own transcript says (issue #2992), and when the Gateway recorded that
/// answer. This is the turn signal the carrying-on clock reads for an owner's sessions, in place of terminal silence.
///
/// WHY NOT THE TERMINAL. The terminal-state detector calls a session waiting after ten seconds without a byte,
/// whatever the reason. A Worker inside a long command that prints nothing is still inside its turn - its transcript
/// holds the tool call with no result yet - and its owner, which said it would wait for that Worker, was marked
/// "Said it would continue and did not" while the Worker was still running.
///
/// WHERE IT COMES FROM. The Director derives the transcript's state when it pushes the conversation
/// (<c>HistoryStateDeriver</c>: a tool call awaiting its result or a last message from the person is "Working",
/// background work still in flight is "BackgroundRunning", a finished turn is "NeedsYou", no conversation or an
/// exited process is "Idle") and the Gateway stores it on the session's turn head. It pushes at its own turn-end
/// edge - the same ten-second quiet moment that made the Worker look stopped - so the head says "Working" exactly
/// when the terminal alone would have said otherwise.
/// </summary>
/// <param name="InTurn">True when the transcript says the session's turn is still open.</param>
/// <param name="RecordedAtUtc">When the Gateway stored this state - for a closed turn, when it learned the turn had
/// ended.</param>
public sealed record SessionTurnState(bool InTurn, DateTime RecordedAtUtc)
{
    /// <summary>
    /// The turn state a stored head carries, or null when it carries none: nothing pushed for the session, an agent
    /// whose conversation cannot be read, or an agent the Director derives no state for (today every agent other
    /// than Claude Code). An unrecognised state is also none, and is logged: a newer Director may name a state this
    /// Gateway does not know, and guessing what it means is exactly how a stopped session gets read as working.
    /// </summary>
    public static SessionTurnState? From(SessionTurnHeadEntity? head)
    {
        if (head is null || !head.IsSupported || string.IsNullOrEmpty(head.HistoryState)) return null;
        var recorded = DateTime.SpecifyKind(head.UpdatedAtUtc, DateTimeKind.Utc);
        switch (head.HistoryState)
        {
            case "Working":
            case "BackgroundRunning":
                return new SessionTurnState(InTurn: true, recorded);
            case "NeedsYou":
            case "Idle":
                return new SessionTurnState(InTurn: false, recorded);
            default:
                FileLog.Write($"[SessionTurnState] From: session={head.SessionId} carries unrecognised history state '{head.HistoryState}'; read as no turn signal");
                return null;
        }
    }
}
