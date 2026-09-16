using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// THE ONE ANSWER to "is this session the Fleet Manager?". Every caller that treats the Fleet Manager
/// differently asks here, so the rule lives in one place.
///
/// THE MARK IS THE ACCOUNT'S, NOT THE SESSION'S. There is exactly one Fleet Manager per account, and the
/// account says which session it is: one session id per tenant, stored as the
/// <c>fleet_manager_session_id</c> tenant setting and set or cleared through <c>PUT /gateway/fleet-manager</c>
/// (<c>cc-devthrottle fleet-manager set|clear</c>). This is the same mark the session list pins first.
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
}
