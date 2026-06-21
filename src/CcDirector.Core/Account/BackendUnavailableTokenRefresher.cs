using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The token refresher wired into the running Director until the live DevThrottle backend sign-in
/// exists (the live exchange is owned by the internal-repository backend and is flagged as a
/// dependency on issue #580). It performs no network exchange and always reports the refresh as
/// unavailable, so <see cref="DevThrottleAccountService.RefreshIfNeededAsync"/> keeps the Director
/// running on the cached credential exactly as it does when offline.
///
/// This is NOT a fallback that hides a failure: there is genuinely no backend endpoint to exchange a
/// refresh token against yet, and this type states that honestly by returning null (the same signal
/// the live refresher returns when offline). When the backend lands, it is replaced by a real
/// <see cref="ITokenRefresher"/> that calls the exchange - the seam is already in place.
/// </summary>
public sealed class BackendUnavailableTokenRefresher : ITokenRefresher
{
    /// <summary>
    /// Reports the refresh as unavailable (returns null) without any network call, so the caller
    /// keeps running on the cached credential. The refresh token is never logged.
    /// </summary>
    public Task<DevThrottleTokens?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        FileLog.Write("[BackendUnavailableTokenRefresher] RefreshAsync: no backend exchange wired yet -> reporting refresh unavailable (cached credential kept)");
        return Task.FromResult<DevThrottleTokens?>(null);
    }
}
