namespace CcDirector.Core.Account;

/// <summary>
/// The outcome of a refresh-token exchange (issue #876). Three cases, because the caller must react
/// differently to each: <see cref="Renewed"/> carries the fresh pair on success;
/// <see cref="RefreshTokenRejected"/> true means the backend DEFINITIVELY refused the refresh token
/// (rotated away or the session was revoked server-side) so the cached credential is dead and must be
/// cleared; both unset means the exchange was merely UNAVAILABLE (offline, backend unreachable, or a
/// server-side error) so the caller keeps the cached credential and retries later. Collapsing the last
/// two into one signal was the pre-#876 shape, and it made a revoked session indistinguishable from a
/// network blip.
/// </summary>
public sealed record TokenRefreshResult
{
    /// <summary>The renewed token pair, when the exchange succeeded. Null otherwise.</summary>
    public DevThrottleTokens? Renewed { get; }

    /// <summary>
    /// True when the backend definitively refused the refresh token (invalid, rotated away, or the
    /// session was revoked). The cached credential can never work again - the caller clears it.
    /// </summary>
    public bool RefreshTokenRejected { get; }

    private TokenRefreshResult(DevThrottleTokens? renewed, bool rejected)
    {
        Renewed = renewed;
        RefreshTokenRejected = rejected;
    }

    /// <summary>The exchange succeeded and returned a fresh pair.</summary>
    public static TokenRefreshResult Success(DevThrottleTokens renewed) =>
        new(renewed ?? throw new ArgumentNullException(nameof(renewed)), rejected: false);

    /// <summary>The exchange could not run or complete (offline, unconfigured, backend error). Retry later.</summary>
    public static readonly TokenRefreshResult Unavailable = new(renewed: null, rejected: false);

    /// <summary>The backend definitively refused the refresh token. The cached credential is dead.</summary>
    public static readonly TokenRefreshResult Rejected = new(renewed: null, rejected: true);
}

/// <summary>
/// Exchanges a refresh token for a fresh token pair against the DevThrottle backend. This is the
/// one network-touching seam in the credential service: the live exchange is supplied as an
/// implementation of this interface, and the offline logged-in check never calls it. Tests supply a
/// stub so the refresh paths can be proven without the live backend.
/// </summary>
public interface ITokenRefresher
{
    /// <summary>
    /// Attempts to renew the token pair using the current refresh token. Returns
    /// <see cref="TokenRefreshResult.Success"/> with the new pair when the exchange succeeds,
    /// <see cref="TokenRefreshResult.Rejected"/> when the backend definitively refuses the refresh
    /// token (the caller clears the dead credential), or <see cref="TokenRefreshResult.Unavailable"/>
    /// when the exchange cannot run or complete (the caller keeps the cached credential and retries).
    /// </summary>
    Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default);
}
