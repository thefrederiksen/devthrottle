using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The token refresher wired into the running Director until the live DevThrottle backend sign-in
/// exists (the live exchange is owned by the internal-repository backend and is flagged as a
/// dependency on issue #580). It performs no network exchange and always reports the refresh as
/// unavailable, so <see cref="DevThrottleAccountService.RefreshIfNeededAsync"/> keeps the Director
/// running on the cached credential exactly as it does when offline.
///
/// This is NOT a fallback that hides a failure: there is genuinely no backend endpoint wired on this
/// host, and this type states that honestly by reporting <see cref="TokenRefreshResult.Unavailable"/>
/// (the same signal the live refresher returns when offline). The Gateway hosts the real exchange
/// (<c>GatewayHttpTokenRefresher</c>, issue #876) - the seam is already in place.
/// </summary>
public sealed class BackendUnavailableTokenRefresher : ITokenRefresher
{
    /// <summary>
    /// Reports the refresh as unavailable without any network call, so the caller keeps running on
    /// the cached credential. The refresh token is never logged.
    /// </summary>
    public Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        FileLog.Write("[BackendUnavailableTokenRefresher] RefreshAsync: no backend exchange wired on this host -> reporting refresh unavailable (cached credential kept)");
        return Task.FromResult(TokenRefreshResult.Unavailable);
    }
}
