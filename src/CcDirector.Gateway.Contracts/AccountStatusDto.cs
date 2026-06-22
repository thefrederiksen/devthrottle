namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The shared shape of the Gateway account endpoints (Gateway Centralization Phase 2/3): the body of
/// <c>GET /account/status</c> and the body returned by <c>POST /account/logout</c>. The Cockpit account
/// surface (issue #648) deserializes this to show the signed-in DevThrottle identity (email + provider)
/// and to confirm the gateway is signed out after a logout.
///
/// Security (carries DT-05 from issue #636): this contract intentionally carries NO access- or
/// refresh-token field, so neither endpoint can ever return the raw token and the Cockpit can never
/// display it. <see cref="Email"/> and <see cref="Provider"/> are present only when
/// <see cref="SignedIn"/> is true with a resolvable identity; they are omitted otherwise.
/// </summary>
public sealed class AccountStatusDto
{
    /// <summary>Whether the Gateway holds a valid DevThrottle credential (computed locally, no cloud call).</summary>
    public bool SignedIn { get; set; }

    /// <summary>The signed-in user's email, or null when not signed in / unavailable.</summary>
    public string? Email { get; set; }

    /// <summary>The authentication provider, or null when not signed in / unavailable.</summary>
    public string? Provider { get; set; }
}
