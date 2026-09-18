using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// THE ONE ANSWER to "is this session the Fleet Manager?". Every caller that treats the Fleet Manager
/// differently asks here, so the rule lives in one place.
///
/// THE MARK IS THE ACCOUNT'S, NOT THE SESSION'S. There is exactly one Fleet Manager per account, and the
/// account says which session it is: one session id per tenant, stored as the
/// <c>fleet_manager_session_id</c> tenant setting and set or cleared through <c>PUT /gateway/fleet-manager</c>
/// (<c>cc-devthrottle fleet-manager set|clear</c>). The roster fold pins the live Fleet Manager first
/// (<see cref="FleetManagerRosterFold"/>, step 8).
///
/// THE WORKFLOW SEAT DOES NOT DECIDE IDENTITY. A seat on the <c>fleet-manager</c> workflow is inherited - a
/// session spawned under a controller inherits its mission, and a mission spawn with no explicit run is seated
/// on the newest run - and nothing stops an unowned Architect being seated on that workflow. Reading the seat
/// would let any of those be treated as the Fleet Manager.
///
/// AND THE MARKED SESSION IS NOT ITSELF OWNED BY A SESSION. The Fleet Manager answers to the owner and nobody
/// else, so a marked session that has been given an owning session is not treated as the Fleet Manager while
/// that ownership stands.
/// </summary>
internal static class FleetManagerSessions
{
    /// <summary>
    /// True when <paramref name="session"/> is the session the account has marked as its Fleet Manager
    /// (<paramref name="markedSessionId"/>) and no session owns it. False when the account has no mark.
    /// </summary>
    public static bool IsFleetManager(SessionDto? session, string? markedSessionId)
        => session is not null
           && !string.IsNullOrEmpty(markedSessionId)
           && string.Equals(session.SessionId, markedSessionId, StringComparison.OrdinalIgnoreCase)
           && !session.IsControlled
           && string.IsNullOrEmpty(session.ControllerSessionId);

    /// <summary>The account's LIVE Fleet Manager in this roster - the marked session, owned by no session, not ended -
    /// or null.</summary>
    public static SessionDto? LiveFleetManager(IEnumerable<SessionDto> roster, string? markedSessionId)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (string.IsNullOrEmpty(markedSessionId)) return null;
        return roster.FirstOrDefault(s => !IsGone(s) && IsFleetManager(s, markedSessionId));
    }

    /// <summary>True when <paramref name="session"/> is owned DIRECTLY by <paramref name="ownerSessionId"/> (and is not
    /// that session itself). Only the direct owner counts: a Worker under an Architect the Fleet Manager started is the
    /// Architect's.</summary>
    public static bool IsOwnedBy(SessionDto session, string ownerSessionId)
        => SameId(session.ControllerSessionId, ownerSessionId) && !SameId(session.SessionId, ownerSessionId);

    /// <summary>
    /// THE ONE ANSWER to "does this session still ask the owner directly?" (step 8): it is running, it is not the Fleet
    /// Manager, the Fleet Manager does not own it, and no other live session owns it - so its stops go red for the
    /// owner. The page's "not the Fleet Manager's" count and list, the session list's "Hand to the Fleet Manager"
    /// offer, and the hand-over route all ask here. <paramref name="fleetManagerId"/> is the account's live Fleet
    /// Manager, or null; <see cref="SessionDto.HasLiveSupervisor"/> must already be resolved across the account.
    /// </summary>
    public static bool AsksOwnerDirectly(SessionDto session, string? fleetManagerId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (IsGone(session)) return false;
        if (!string.IsNullOrEmpty(fleetManagerId)
            && (SameId(session.SessionId, fleetManagerId) || IsOwnedBy(session, fleetManagerId)))
            return false;
        return !session.HasLiveSupervisor;
    }

    /// <summary>
    /// WOULD PUTTING <paramref name="targetSessionId"/> UNDER <paramref name="newOwnerSessionId"/> CLOSE A LOOP? -
    /// true when the target already owns the session it would be handed to, directly or anywhere further up that
    /// session's chain of owners.
    ///
    /// WHY THIS IS A REFUSAL AND NOT A CURIOSITY. A session is quietened because something alive is holding it, and
    /// that is the whole of the rule (<see cref="SessionOrdering.IsSupervised"/> reads
    /// <see cref="SessionDto.HasLiveSupervisor"/>, which is no more than "my owner is running"). Nothing in it asks
    /// where the chain ENDS. So a ring of live sessions holding each other holds every one of its members: each has a
    /// live owner, so none of them ever goes red, and the owner simply stops hearing from all of them. Two sessions
    /// are enough - session A owns W, the owner tells W to take A - and the owner loses both.
    ///
    /// A DEAD LINK BREAKS THE CHAIN, so the walk stops at one. A session whose owner has ended already asks the owner
    /// directly, which is exactly where the red escapes, so a chain that passes through an ended session cannot
    /// silence anybody and must not be refused.
    ///
    /// The walk carries a seen-set, because the roster it reads may already contain a ring this rule was written to
    /// prevent, and a ring must not become a hang.
    /// </summary>
    public static bool WouldCloseALoop(IEnumerable<SessionDto> roster, string? targetSessionId, string? newOwnerSessionId)
    {
        ArgumentNullException.ThrowIfNull(roster);
        if (string.IsNullOrEmpty(targetSessionId) || string.IsNullOrEmpty(newOwnerSessionId)) return false;

        var byId = new Dictionary<string, SessionDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in roster)
            if (!string.IsNullOrEmpty(s.SessionId)) byId[s.SessionId] = s;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var walk = newOwnerSessionId;
        while (!string.IsNullOrEmpty(walk) && seen.Add(walk))
        {
            if (SameId(walk, targetSessionId)) return true;
            if (!byId.TryGetValue(walk, out var row) || IsGone(row) || !row.IsControlled) return false;
            walk = row.ControllerSessionId;
        }
        return false;
    }

    /// <summary>A session that has ended: exited, or crashed.</summary>
    public static bool IsGone(SessionDto session)
        => session.Crashed || string.Equals(session.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

    public static bool SameId(string? a, string? b)
        => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
