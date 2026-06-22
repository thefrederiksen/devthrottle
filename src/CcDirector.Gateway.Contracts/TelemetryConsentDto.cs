namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The shape of the Gateway telemetry-consent endpoints (Gateway Centralization Phase 3, issue #649):
/// the body of <c>GET /gateway/telemetry-consent</c> and the body returned by
/// <c>PUT /gateway/telemetry-consent</c>. The Cockpit telemetry surface deserializes this to show and
/// toggle the one fleet-wide richer-usage-telemetry consent (opt-out), which defaults to ON.
///
/// This consent gates ONLY the richer usage telemetry; the always-on login/director-startup auth-floor
/// events are never gated by it. The contract carries no token and no user data - only the boolean.
/// </summary>
public sealed class TelemetryConsentDto
{
    /// <summary>Whether the fleet has consented to the richer usage telemetry (default ON).</summary>
    public bool Enabled { get; set; } = true;
}
