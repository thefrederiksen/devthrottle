namespace CcDirector.Core.Account;

/// <summary>
/// Exchanges a refresh token for a fresh token pair against the DevThrottle backend. This is the
/// one network-touching seam in the credential service: the live exchange against the
/// internal-repository backend is supplied as an implementation of this interface, and the offline
/// logged-in check never calls it. Tests supply a stub so the background-refresh path can be proven
/// without the live backend (the issue authorizes a test-issued token pair).
/// </summary>
public interface ITokenRefresher
{
    /// <summary>
    /// Attempts to renew the token pair using the current refresh token. Returns the new token pair
    /// when the exchange succeeds, or null when connectivity is unavailable or the backend declines
    /// (the caller keeps running on the cached credential).
    /// </summary>
    Task<DevThrottleTokens?> RefreshAsync(string refreshToken, CancellationToken ct = default);
}
