using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Snooze;

/// <summary>
/// THE PUSH SEAM, and where the Gateway drives the hold machine. Every session state a Director pushes up
/// its tunnel passes through here, carrying the only two facts a Director contributes: what it is doing
/// (<c>ActivityState</c>) and whether the owner just drove a turn (<c>LastOwnerTurnAtUtc</c>). This class
/// turns those observations into the Gateway's rulings.
///
/// It used to read the Director's own <c>HoldState</c> and believe it - which meant the ruling was made on
/// the Director and merely reported here. That is the architecture this replaces. A Director does not
/// decide hold: a hold is the owner's intent, and a Director's world is bytes and processes.
///
/// THE RULING (owner, 14 July 2026) that the landing edge implements: the snooze clock starts when the
/// work ENDS. "Snooze me for 12 hours when this finishes" means twelve hours of quiet AFTER it finishes,
/// so the clock cannot be armed when the snooze is asked for - the agent is still working then.
/// <see cref="SnoozeRegistry.Land"/> is idempotent, so a settled session re-pushing its state repeatedly
/// never restarts a running clock.
///
/// Cost: one lookup per pushed session, and the overwhelming majority - anything with no hold - is
/// rejected by it immediately.
/// </summary>
public sealed class SnoozeLandingObserver
{
    private readonly SnoozeRegistry _registry;
    private readonly Func<DateTime> _utcNow;

    /// <param name="registry">The registry holding the deferred entry to land.</param>
    /// <param name="utcNow">The clock; injected so the landing instant is deterministic in tests.</param>
    ///
    /// <remarks>
    /// SINGLE WRITER OF HOLD (round 2 finding 1). This observer used to ALSO stamp the hold down to the
    /// Director's display mirror with its own fire-and-forget, unretried <c>Task.Run</c>. That raced the
    /// reliable display-state channel: a delayed <c>None</c> from here could land after a fresh <c>Held</c>
    /// from that channel, and the channel's change gate - which records the value it WANTED, not what the
    /// Director currently holds - then suppressed the repair, leaving the desktop permanently stale. The
    /// one-shot mirror is deleted. This observer now ONLY mutates the registry; the SINGLE writer of
    /// <c>HoldState</c> down to the Director is <c>FleetDisplayStateObserver</c>, which runs on the same
    /// push right after this one, is change-gated, is retried on a no-stream result, and is driven by the
    /// periodic display-state sweep. Every hold transition this observer makes (working-delete, exit,
    /// owner-turn clear, deferral landing) is folded from the registry by that channel, so the desktop rail
    /// reconciles at fold cadence (&lt;=5s) instead of instantly - the correct trade for one writer and no
    /// permanent staleness.
    /// </remarks>
    public SnoozeLandingObserver(
        SnoozeRegistry registry,
        Func<DateTime>? utcNow = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Observe one pushed session and drive the hold machine from it.
    /// </summary>
    ///
    /// <remarks>
    /// This reads the session's ACTIVITY, never its hold. That is the whole architecture in one method: a
    /// Director reports the one fact only it can see - am I working - and the GATEWAY decides what that
    /// fact means for the owner's hold. It used to read <c>session.HoldState</c>, which meant asking a
    /// Director to decide, and then believing it.
    ///
    /// Three edges, all of them the Gateway's ruling, not the Director's:
    ///  * WORKING (Working or Starting) - there is activity on this terminal again, so an ARMED snooze is
    ///    spent and is DELETED outright (not merely outranked): the owner's law (17 July 2026), "if you
    ///    snooze it, it's a human thing; as soon as there's any work on that terminal it comes out of
    ///    snooze, period, full stop." A DEFERRED hold is one exception - see below. Work the Director
    ///    reports as started by an agent-origin send (<see cref="SessionDto.WorkingOrigin"/>) is the other:
    ///    the Message Load mission's ruling 15, set by that mission's Architect on 17 September 2026, keeps
    ///    the snooze so the fleet doorbell cannot end it.
    ///  * SETTLED (anything that is not Working, Starting or Exited) - the work the deferral was waiting
    ///    for has ended, so the deferral lands and the clock starts. The owner's ruling (14 July 2026):
    ///    "snooze me for 12 hours when this finishes" means twelve hours of quiet AFTER it finishes.
    ///  * EXITED - drop the hold entirely. There is no turn to come back to, and a dead session must never
    ///    hide behind a "Snoozed" label.
    ///
    /// This REVERSES the old rule that Working was deliberately not an edge. That rule feared that another
    /// agent's fleet message, or a bare terminal repaint, would be misread as "the owner is back" and kill
    /// the hold - so it kept a snooze alive through work. The owner has ruled the other way: it does not
    /// matter WHO woke the terminal, only that it is awake. A snooze exists to quiet a session with nothing
    /// happening; the instant something happens, the snooze is over. (The genuine need it feared - agents
    /// churning quietly in the background - is a separate future "background/running" state, NOT a snooze
    /// surviving work.)
    ///
    /// The one thing Working does NOT delete is a DEFERRED hold. "Snooze me when this finishes" is asked
    /// for while the agent is still working; if the next working observation cleared it, an agent could
    /// never snooze its own session. So the working edge clears only ARMED entries; a deferral is converted
    /// solely by the SETTLED edge (Land). This armed/deferred distinction is load-bearing.
    /// </remarks>
    public void Observe(SessionDto? session)
    {
        if (session is null || string.IsNullOrEmpty(session.SessionId))
            return;
        // One lookup: nothing held -> nothing to decide, which is the overwhelming majority of pushes. A
        // Contains() followed by a separate scan of Entries() would read the map twice with a gap the entry
        // could vanish through. (The result is no longer used to address a mirror - the reliable
        // display-state channel owns the down-stamp now - but the cheap early-out still matters.)
        if (_registry.DirectorIdFor(session.SessionId) is null)
            return;

        // The owner came back and drove a turn: the hold is over, whatever the session is doing. Checked
        // FIRST, because it beats every other edge - a hold exists to stop bothering someone who is away,
        // and they are demonstrably not away.
        if (_registry.ClearIfSupersededByOwnerTurn(session.SessionId, session.LastOwnerTurnAtUtc))
            return;

        var activity = (session.ActivityState ?? "").Trim();

        if (string.Equals(activity, nameof(ActivityState.Exited), StringComparison.OrdinalIgnoreCase))
        {
            if (_registry.Clear(session.SessionId, ActivityCauses.SessionExit))
                FileLog.Write($"[SnoozeLandingObserver] sid={session.SessionId}: session exited -> hold dropped");
            return;
        }

        // The terminal is working again (Working or Starting). By the owner's law any activity ends a
        // snooze, so an ARMED entry is DELETED here - the clock dies with it, and when the work settles the
        // session reads red "needs you", never grey "Snoozed". A DEFERRED entry is spared: it was asked for
        // WHILE working, so deleting it on the next working push would make an agent unable to snooze its own
        // session - only the settled edge (Land) converts a deferral. That armed/deferred split lives in
        // ClearIfArmed so it cannot be got wrong here.
        if (IsWorking(activity))
        {
            // AGENT-ORIGIN WORK KEEPS AN ARMED SNOOZE (the Message Load mission, ruling 15; inspection 4,
            // ruling 8). The fleet doorbell makes a snoozed session work, and a doorbell must not end the
            // owner's snooze; the same holds for any agent or product send. The Director reports who started
            // the work. The owner's half of the rule is untouched: owner-started work ends the snooze here,
            // and an owner turn at any time ends it above. Work NO submission explains - the agent starting on
            // its own - still ends it, as the 17 July 2026 law says.
            if (string.Equals(session.WorkingOrigin, WorkingOrigins.Agent, StringComparison.Ordinal))
            {
                if (_registry.SnoozeUntilFor(session.SessionId) is not null)
                    FileLog.Write($"[SnoozeLandingObserver] sid={session.SessionId}: working on an agent-origin send -> armed snooze kept (ruling 15)");
                return;
            }
            if (_registry.ClearIfArmed(session.SessionId))
                FileLog.Write($"[SnoozeLandingObserver] sid={session.SessionId}: working again -> armed snooze deleted (work ends a snooze)");
            return;
        }

        if (IsSettled(activity) && _registry.Land(session.SessionId, _utcNow()))
            FileLog.Write($"[SnoozeLandingObserver] sid={session.SessionId}: work ended -> deferred hold landed, clock started");
    }

    /// <summary>
    /// Is the session actively running? Working, or Starting (still coming up but already alive on the
    /// terminal). Both are "there is activity here", which the owner's law says ends a snooze. Mirrors
    /// <c>Session.IsWorking</c> (Working or Starting).
    /// </summary>
    private static bool IsWorking(string activity) =>
        string.Equals(activity, nameof(ActivityState.Working), StringComparison.OrdinalIgnoreCase)
        || string.Equals(activity, nameof(ActivityState.Starting), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Has the work ENDED? Settled is everything that is not actively running and not dead: the agent is
    /// sitting at its prompt, or waiting on a permission answer, or idle. Starting is excluded - a session
    /// still coming up has not finished anything yet - and Working is obviously excluded. An unrecognised
    /// value is NOT settled: never land a deferral on a state we do not understand, because landing starts
    /// a clock and a clock started too early expires too early.
    /// </summary>
    private static bool IsSettled(string activity) =>
        string.Equals(activity, nameof(ActivityState.WaitingForInput), StringComparison.OrdinalIgnoreCase)
        || string.Equals(activity, nameof(ActivityState.WaitingForPerm), StringComparison.OrdinalIgnoreCase)
        || string.Equals(activity, nameof(ActivityState.Idle), StringComparison.OrdinalIgnoreCase);

    /// <summary>Observe a whole pushed snapshot - the reconnect path, where a landing can hide.</summary>
    public void ObserveSnapshot(IReadOnlyList<SessionDto>? sessions)
    {
        if (sessions is null) return;
        foreach (var s in sessions)
            Observe(s);
    }
}
