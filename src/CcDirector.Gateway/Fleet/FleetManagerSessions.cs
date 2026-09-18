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

    /// <summary>A session that has ended: exited, or crashed.</summary>
    public static bool IsGone(SessionDto session)
        => session.Crashed || string.Equals(session.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

    public static bool SameId(string? a, string? b)
        => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
