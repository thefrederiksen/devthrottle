using CcDirector.Core.Account;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The production <see cref="IGatewayTelemetryTokenSource"/>: reads the Gateway's own account token
/// from the Gateway-hosted credential service (issue #636, the reused
/// <see cref="DevThrottleAccountService"/> exposed as <c>GatewayHost.Account</c>) so the Gateway can
/// attach it when forwarding telemetry to the cloud (issue #639). Entirely local - no network call.
///
/// "Signed in" is the credential service's own local check: a stored access token whose signature
/// verifies and is valid-or-renewable yields the token; an absent or tampered credential yields null,
/// and the queue then leaves the event queued rather than forwarding it without the Gateway's token.
/// The token value is NEVER logged (security rule DT-05) - that guarantee lives in the credential
/// service's accessor, and this adapter adds no logging of its own.
/// </summary>
public sealed class GatewayAccountTelemetryTokenSource : IGatewayTelemetryTokenSource
{
    private readonly DevThrottleAccountService _account;

    /// <param name="account">The Gateway-hosted DevThrottle credential service. Required.</param>
    /// <exception cref="ArgumentNullException">account is null.</exception>
    public GatewayAccountTelemetryTokenSource(DevThrottleAccountService account)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
    }

    /// <inheritdoc />
    public bool TryGetAccessToken(out string? accessToken)
    {
        accessToken = _account.GetAccessTokenForForwarding();
        return accessToken is not null;
    }
}
