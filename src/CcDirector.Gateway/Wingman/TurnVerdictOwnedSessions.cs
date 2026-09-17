using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// What the Wingman needs to know about the sessions a session owns (owner ruling, 2026-09-15): how many are
/// working, stopped and needing a person - the row's own crew line counts, from the same fold, which is what the
/// judge is told - and, for the carrying-on clock, how many are still inside a turn and the latest moment one of
/// them stopped.
/// </summary>
/// <param name="InTurn">How many owned sessions are inside a turn (issue #2992). This, not <paramref name="Working"/>,
/// is what stops the carrying-on clock: <paramref name="Working"/> is the terminal's ten-second silence rule, which
/// reads a session inside a long silent command as stopped.</param>
/// <param name="LastStoppedAtUtc">The latest moment an owned session stopped, in UTC, or null when none carries one.
/// See <see cref="TurnVerdictOwnedSessions.For"/> for how each session's moment is read.</param>
public sealed record OwnedSessionsFacts(int Working, int Stopped, int NeedYou, int InTurn, DateTime? LastStoppedAtUtc);

/// <summary>
/// The owned sessions of one session, read as a pure function over a roster - the shape and the reason of
/// <see cref="TurnVerdictHeldCheck"/>: the roster arrives with every role and liveness answer nulled by the push
/// store, so roles are resolved across the WHOLE roster first, then the ownership tree is built and the crew
/// under the session is summarised by <see cref="SessionTree.SummarizeCrew"/>, the fold the row's crew line
/// ("5 under it: 3 working") prints from.
///
/// "Owned" is every level: a Manager's Workers are an Architect's too.
/// </summary>
public static class TurnVerdictOwnedSessions
{
    /// <summary>
    /// The facts, or null when the session is not in the roster or owns no session.
    ///
    /// IS AN OWNED SESSION STILL INSIDE A TURN (issue #2992; Architect's ruling). Where the session has a transcript
    /// turn signal (<see cref="SessionTurnState"/>), it is inside a turn when that signal says the turn is open, or
    /// when its terminal is writing right now - a terminal that is printing is never evidence of a stop, and it
    /// covers the moment a new turn starts before the next push carries it. Only the transcript can say a turn has
    /// ended, and the moment it stopped is when the Gateway recorded that. Terminal silence is never read as a stop.
    ///
    /// AN AGENT WITH NO TURN SIGNAL - the Director derives one for Claude Code only, and a Director too old to push
    /// its conversation sends none - has nothing but its terminal. For such a session the clock reads the terminal,
    /// deliberately: inside a turn while the terminal says Working, stopped at its last terminal write. A long silent
    /// command under such an agent still reads as stopped; that is a known limit of what the agent tells us, not a
    /// default standing in for a signal that exists.
    /// </summary>
    /// <param name="turnState">The session's transcript turn signal, or null when it has none.</param>
    public static OwnedSessionsFacts? For(
        IReadOnlyList<(string DirectorId, SessionDto Session)> roster, string sessionId, Func<string, SessionTurnState?> turnState)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(turnState);
        var sessions = roster.Select(r => r.Session).Where(s => s is not null).ToList();
        FleetRoleResolver.Stamp(sessions);

        var root = sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        if (root is null) return null;

        var tree = SessionTree.Build(sessions);
        var owned = SessionTree.DescendantsOf(tree, root).Select(d => d.Session).ToList();
        if (owned.Count == 0) return null;

        var crew = SessionTree.SummarizeCrew(root, owned);
        var inTurn = 0;
        DateTime? last = null;
        foreach (var s in owned)
        {
            var terminalWorking = string.Equals(s.ActivityState, "Working", StringComparison.Ordinal);
            DateTime? stopped;
            if (turnState(s.SessionId) is { } turn)
            {
                if (turn.InTurn || terminalWorking) inTurn++;
                stopped = turn.RecordedAtUtc;
            }
            else
            {
                if (terminalWorking) inTurn++;
                stopped = s.LastActivityAt;
            }

            if (stopped is not { } at) continue;
            var utc = at.Kind == DateTimeKind.Utc ? at : at.ToUniversalTime();
            if (last is null || utc > last.Value) last = utc;
        }

        return new OwnedSessionsFacts(crew.Working, crew.Stopped, crew.NeedsYou, inTurn, last);
    }
}
