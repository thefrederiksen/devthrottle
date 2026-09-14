namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Defect 5: the payload of the <c>set-resolved-role</c> command - the Gateway telling a Director what one
/// of its sessions' resolved role IS, so the Director's desktop can fold the same answer the phone and the
/// Cockpit fold.
///
/// This is a FACT being delivered, not a request for the Director to decide anything. The Director stores
/// it verbatim on <c>Session.GatewayResolvedRole</c> and reports it back out through
/// <c>ControlEndpoints.Map</c>; it never computes, adjusts, or second-guesses the value. "Is this session's
/// controller still alive?" is unanswerable from one Director - which is why the answer has to arrive from
/// here. See docs/new_architecture/sessions.html, defect 5.
/// </summary>
public sealed class SetResolvedRoleRequest
{
    /// <summary>
    /// The resolved role: one of the <see cref="SessionRoles"/> values (Standalone / Manager / Worker /
    /// Architect). An empty or whitespace value CLEARS the stamp back to "no answer" (the Director then
    /// reports null, exactly as it does before any Gateway has ever spoken to it).
    /// </summary>
    public string Role { get; set; } = "";

    /// <summary>
    /// IS THERE A SUPERVISOR ALIVE RIGHT NOW? The answer to the one question the attention rule asks
    /// (<c>SessionOrdering.IsSupervised</c>), delivered on the same command and for the same reason as the
    /// role: a single Director cannot see the session that supervises one of its own, so the answer has to
    /// arrive from the Gateway. This class's summary already described that gap - "is this session's
    /// controller still alive? is unanswerable from one Director" - it was simply being answered through a
    /// proxy, the Worker seat, rather than stated.
    ///
    /// IT TRAVELS WITH THE ROLE AND NOT INSTEAD OF IT, because they are now two different facts. The seat
    /// says what a session is for; this says whether anybody is holding it. Conflating them is what let a
    /// seat stamped at spawn keep quietening a session whose supervisor had died hours earlier.
    ///
    /// A Director that has never heard from a Gateway reports false, and false SURFACES the session to the
    /// owner. That is the deliberate floor: with no fleet-wide answer available, a session asks for him.
    /// </summary>
    public bool HasLiveSupervisor { get; set; }
}
