using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// THE ONE ANSWER to "is this session the Fleet Manager?". Every caller that treats the Fleet Manager
/// differently asks here, so the mark can change in one place.
///
/// THE MARK TODAY IS THE WORKFLOW SEAT: a session seated on the built-in <c>fleet-manager</c> workflow. The
/// seat is stamped by the Director at spawn and pushed up through <c>ControlEndpoints.Map</c>, and the push
/// store keeps it (only the role and the supervisor-liveness answer are discarded at ingest), so the Gateway
/// can read it off any roster row.
///
/// AND THE SESSION IS NOT ITSELF OWNED BY A SESSION. The seat alone is not enough, because it is inherited: a
/// session spawned with a controlling session inherits that controller's mission, and a mission spawn with no
/// explicit run is seated on the mission's newest run. So an Architect a Fleet Manager starts can carry the
/// <c>fleet-manager</c> seat too, and reading the seat alone would let that Architect's Workers be judged. The
/// Fleet Manager answers to the owner and nobody else, so a controlled session is never the Fleet Manager.
///
/// GAP, STATED: a session spawned with no controlling session and explicitly seated on a Fleet Manager's run
/// (or on its mission) reads as a Fleet Manager. Step 8 of the Fleet Manager mission replaces this with a
/// pinned mark; when it does, it changes this method and nothing else.
/// </summary>
internal static class FleetManagerSessions
{
    /// <summary>The id of the built-in workflow a Fleet Manager session is seated on.</summary>
    public const string WorkflowId = "fleet-manager";

    /// <summary>True when <paramref name="session"/> is a Fleet Manager session.</summary>
    public static bool IsFleetManager(SessionDto? session)
        => session is not null
           && string.Equals(session.WorkflowId, WorkflowId, StringComparison.Ordinal)
           && !session.IsControlled
           && string.IsNullOrEmpty(session.ControllerSessionId);
}
