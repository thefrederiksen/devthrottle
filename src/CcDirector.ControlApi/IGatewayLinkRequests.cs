using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The seam through which this desktop reads and answers the requests sessions make to talk to another session
/// (<c>GET /fleet/link-requests</c> and <c>POST /fleet/link-requests/{id}/answer</c>, issue #3851). A seam of its own
/// beside <see cref="IGatewayColourLegend"/>, for the same reason: Director-to-Gateway traffic is moving onto the tunnel
/// and a named seam makes that one swap.
///
/// The Gateway admits the Director's own key to exactly these two routes. Setting up a link unasked stays with the
/// owner's phone or browser.
/// </summary>
public interface IGatewayLinkRequests
{
    /// <summary>The requests the owner can see - waiting and lately answered - or null when no Gateway is configured.
    /// Throws when one is configured and the read fails.</summary>
    Task<List<FleetMessageLinkRequestDto>?> ListLinkRequestsAsync(CancellationToken ct = default);

    /// <summary>Approve exactly what the session asked for, or say no. Throws, with the Gateway's own sentence where it
    /// gave one, when the answer was not accepted.</summary>
    Task<FleetMessageLinkRequestAnswerResponse> AnswerLinkRequestAsync(string requestId, bool approve, CancellationToken ct = default);
}
