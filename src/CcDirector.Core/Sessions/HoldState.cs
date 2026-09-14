namespace CcDirector.Core.Sessions;

/// <summary>
/// Where a session sits in the hold ("Snooze") state machine - the user's "I do not want to deal with
/// this one right now". Design and diagram: docs/new_architecture/sessions.html.
///
/// The whole machine, driven by two things: what the user pressed, and whether the agent is working
/// (<see cref="ActivityState"/>, the authoritative live fact - never a latch):
///
/// <code>
///   [None] --- Snooze pressed while settled -------------------------> [Held]
///     ^                                                                    ^
///     |--- Unsnooze pressed ---------------------------------------------- |
///     |--- the OWNER types or speaks into it (supersedes) ---------------- |
///     |--- the snooze timer expires (Gateway sweep) ---------------------- |
///     |--- it exits ------------------------------------------------------ |
///     |                                                                    |
///     +--- Snooze pressed while working -> [DeferredHold] --- it stops ----+
/// </code>
///
/// The load-bearing rule is: ONLY THE OWNER LIFTS A HOLD. A hold is the owner saying "do not bother me
/// with this session", so only the owner withdraws it - by un-snoozing, by typing or speaking into it
/// (Session.IsOwnerDriven), or by letting the snooze timer run out. An exited session drops its hold too,
/// because a dead session must never hide behind a "Snoozed" label.
///
/// ACTIVITY IS NOT CONSENT, and this is the correction that defines this machine. Until 15 July 2026 the
/// load-bearing edge was the opposite - "Held + it starts working -> None", justified by the claim that a
/// session can never be simultaneously held and working. That invariant WAS the bug. Work is not the
/// owner: another agent's fleet message is real work driven by an agent, and the terminal detector's rule
/// ("a byte out of the ConPTY means working") fires on a bare repaint. So every hold was destroyed by
/// something that was not the owner - all 16 set on 15 July 2026 died within 1-21 minutes, and a 12-hour
/// hold was unreachable.
///
/// So <see cref="Held"/> + working IS now reachable, deliberately: a parked session can do background work
/// for another agent and stay parked. That is not a lie - it is exactly what the owner asked for. The
/// roster keeps rendering it as Snoozed; hold wins over the activity colour, because suppressing attention
/// is the entire purpose of a hold.
/// </summary>
public enum HoldState
{
    /// <summary>Not held. The session's own activity state is the whole story.</summary>
    None = 0,

    /// <summary>
    /// Parked by the user: shown as "Snoozed", sunk to the bottom of the roster, skipped by the
    /// conductor, and never raised as "needs you". Left the instant the agent starts working again.
    /// </summary>
    Held = 1,

    /// <summary>
    /// The user pressed Snooze while the agent was working: "hold this one when it finishes". NOT parked
    /// yet - the session is still working and still reads as working on every screen - but it parks
    /// (-&gt; <see cref="Held"/>) the moment the work stops. Superseded by a fresh prompt, and dropped if
    /// the session exits (there is no turn left to come back to).
    /// </summary>
    DeferredHold = 2,
}
