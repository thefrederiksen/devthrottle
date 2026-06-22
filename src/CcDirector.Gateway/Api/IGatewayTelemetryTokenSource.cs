namespace CcDirector.Gateway.Api;

/// <summary>
/// Supplies the GATEWAY's own account token to attach when it forwards telemetry to the cloud
/// (Gateway Centralization Phase 2, issue #639). Phase 1 forwarded whatever Bearer the Director sent
/// inbound; from this issue the Gateway is the single egress and attaches its OWN stored account token
/// instead, so the Director no longer needs a token at all.
///
/// The source is consulted at FORWARD time (not enqueue time) so a sign-in that completes AFTER an
/// event was queued is picked up on the next flush pass. Two outcomes:
/// <list type="bullet">
///   <item>Signed in -> <see cref="TryGetAccessToken"/> returns true and yields the token to attach.</item>
///   <item>Not signed in -> returns false; the queue must NOT forward and leaves the event queued for a
///     later pass (issue #639 acceptance criterion 2: queue-when-not-signed-in, flush-after-sign-in).</item>
/// </list>
///
/// Security (rule DT-05): the token value is for attaching to the outbound request ONLY and is NEVER
/// written to the log on any path - implementations log only whether a token was available.
/// </summary>
public interface IGatewayTelemetryTokenSource
{
    /// <summary>
    /// Tries to read the Gateway's current account access token to attach to an outbound forward.
    /// </summary>
    /// <param name="accessToken">The token to attach when the Gateway is signed in; null otherwise.</param>
    /// <returns>True when the Gateway is signed in and a token is available; false otherwise.</returns>
    bool TryGetAccessToken(out string? accessToken);
}
