using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The DevThrottle credential and authentication service - the single foundation the startup gate
/// (issue #580), the first-run login (issue #581), and the account area (issue #582) build on. It
/// receives the access-plus-refresh token pair from the login-completion flow, stores it in the
/// operating system credential store (encrypted at rest), answers "is this install logged in?"
/// locally with no network call, renews the access token in the background using the refresh token
/// when connectivity is available, and clears the credential on logout.
///
/// This is the DevThrottle account, distinct from the Claude sign-in account store
/// (<c>ClaudeAccountStore</c>, which holds Claude OAuth credentials as plain-text JSON for a
/// different purpose). The store binding is injected so Windows Data Protection is used on Windows
/// and the macOS Keychain can be a later drop-in.
/// </summary>
public sealed class DevThrottleAccountService
{
    /// <summary>
    /// How much life must remain on the access token before the background refresh renews it (issue
    /// #876). Renewing PROACTIVELY - inside this margin, not only after expiry - means outbound calls
    /// (device heartbeat, telemetry forwarding) never present an already-expired token.
    /// </summary>
    public static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(10);

    private readonly IProtectedTokenStore _store;
    private readonly JwtAccessTokenValidator _validator;
    private readonly AuthEventLog _eventLog;
    private readonly ITokenRefresher _refresher;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    // Token rotation makes overlapping exchanges actively harmful (the second one presents an
    // already-rotated refresh token and looks revoked), so refresh passes are single-flight.
    private readonly SemaphoreSlim _refreshFlight = new(1, 1);

    /// <summary>
    /// Creates the service from its collaborators. None is optional - each one is a real dependency
    /// the service needs to do its job (no fallback construction).
    /// </summary>
    /// <param name="store">The operating system credential store binding (encrypted at rest).</param>
    /// <param name="validator">The local signature-and-expiry validator (no network).</param>
    /// <param name="eventLog">The authentication-floor event recorder.</param>
    /// <param name="refresher">The backend refresh-token exchange (the one network-touching seam).</param>
    /// <param name="timeProvider">Time source for the proactive-renewal margin; defaults to the system clock. Injected so tests control "now".</param>
    public DevThrottleAccountService(
        IProtectedTokenStore store,
        JwtAccessTokenValidator validator,
        AuthEventLog eventLog,
        ITokenRefresher refresher,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _eventLog = eventLog ?? throw new ArgumentNullException(nameof(eventLog));
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Stores the token pair handed back by the login-completion flow in the operating system
    /// credential store. Records a "logged-in" event the first time a credential is stored on this
    /// install (a re-store of an already logged-in install does not re-record).
    /// </summary>
    public void StoreTokens(DevThrottleTokens tokens)
    {
        if (tokens is null)
            throw new ArgumentNullException(nameof(tokens));

        FileLog.Write("[DevThrottleAccountService] StoreTokens: storing token pair in credential store");
        lock (_gate)
        {
            var wasLoggedInBefore = _store.HasTokens;
            _store.Save(tokens);
            if (!wasLoggedInBefore)
                _eventLog.RecordLoggedIn();
        }
        FileLog.Write("[DevThrottleAccountService] StoreTokens: stored");
    }

    /// <summary>
    /// Answers "is this install logged in?" entirely from the cached credential, with NO outbound
    /// network call. Returns true when a stored access token's signature verifies and either has not
    /// expired or is expired-but-well-formed (a genuinely-ours token that the background refresh can
    /// renew). Returns false when no credential is stored, the stored entry cannot be decrypted, or
    /// the access token is tampered / wrong-signature.
    /// </summary>
    public bool IsLoggedIn()
    {
        FileLog.Write("[DevThrottleAccountService] IsLoggedIn: checking cached credential locally (no network call)");

        DevThrottleTokens? tokens;
        lock (_gate)
        {
            tokens = _store.Load();
        }

        if (tokens is null)
        {
            FileLog.Write("[DevThrottleAccountService] IsLoggedIn: no stored credential -> false");
            return false;
        }

        var validation = _validator.Validate(tokens.AccessToken);
        var loggedIn = validation.IsValid || validation.IsExpiredButWellFormed;
        FileLog.Write($"[DevThrottleAccountService] IsLoggedIn: valid={validation.IsValid}, expiredButWellFormed={validation.IsExpiredButWellFormed}, result={loggedIn} (no network call)");
        return loggedIn;
    }

    /// <summary>
    /// Renews the access token in the background using the refresh token when the cached access
    /// token has expired OR is inside the proactive <see cref="RenewalMargin"/> (issue #876), so
    /// outbound calls never present an already-expired token. A token with comfortable life left is
    /// a no-op. When the exchange is unavailable (offline, backend error) the service keeps running
    /// on the cached credential and returns false; when the backend DEFINITIVELY rejects the refresh
    /// token (rotated away or the session was revoked) the dead credential is cleared so the install
    /// reads as signed out and prompts a new sign-in. Returns true only when a renewed token pair
    /// was stored. Single-flight: a pass that starts while another is running is a no-op, because
    /// token rotation makes overlapping exchanges harmful.
    /// </summary>
    public async Task<bool> RefreshIfNeededAsync(CancellationToken ct = default)
    {
        FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: evaluating cached credential");

        if (!await _refreshFlight.WaitAsync(0, ct))
        {
            FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: a refresh pass is already in flight -> skipping this one");
            return false;
        }

        try
        {
            DevThrottleTokens? tokens;
            lock (_gate)
            {
                tokens = _store.Load();
            }

            if (tokens is null)
            {
                FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: no stored credential -> nothing to refresh");
                return false;
            }

            var validation = _validator.Validate(tokens.AccessToken);
            if (!validation.IsValid && !validation.IsExpiredButWellFormed)
            {
                FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: access token not renewable (tampered/wrong-signature) -> not refreshing");
                return false;
            }

            if (validation.IsValid)
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                var remaining = validation.ExpiresAtUtc is null ? (TimeSpan?)null : validation.ExpiresAtUtc.Value - nowUtc;
                if (remaining is null || remaining.Value > RenewalMargin)
                {
                    FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: access token has comfortable life left -> no refresh needed");
                    return false;
                }
                FileLog.Write($"[DevThrottleAccountService] RefreshIfNeededAsync: access token expires in {remaining.Value.TotalMinutes:0.0} minute(s) (inside the {RenewalMargin.TotalMinutes:0}-minute renewal margin) -> renewing proactively");
            }
            else
            {
                FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: access token expired -> attempting background refresh");
            }

            var result = await _refresher.RefreshAsync(tokens.RefreshToken, ct);
            if (result.Renewed is not null)
            {
                lock (_gate)
                {
                    _store.Save(result.Renewed);
                }
                FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: refreshed access token stored");
                return true;
            }

            if (result.RefreshTokenRejected)
            {
                // The backend definitively refused the refresh token: the session was revoked or the
                // token rotated away. The cached credential can never work again, so keeping it would
                // only fake a "Signed in" state - clear it so the tray/Cockpit prompt a new sign-in.
                FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: backend definitively rejected the refresh token (session revoked or rotated away) -> clearing the dead credential; a new sign-in is required");
                lock (_gate)
                {
                    var wasLoggedIn = _store.HasTokens;
                    _store.Clear();
                    if (wasLoggedIn)
                        _eventLog.RecordLoggedOut();
                }
                return false;
            }

            FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: refresh unavailable (offline or backend error) -> keeping cached credential");
            return false;
        }
        finally
        {
            _refreshFlight.Release();
        }
    }

    /// <summary>
    /// Returns the signed-in identity (email and provider) read locally from the cached access
    /// token's claims, with NO network call (the account area, issue #582). Returns null when no
    /// credential is stored or the cached token carries no email claim - the caller shows an explicit
    /// "identity unavailable" state rather than a fabricated one.
    /// </summary>
    public AccountIdentity? GetIdentity()
    {
        FileLog.Write("[DevThrottleAccountService] GetIdentity: reading identity from the cached credential (no network call)");

        DevThrottleTokens? tokens;
        lock (_gate)
        {
            tokens = _store.Load();
        }

        if (tokens is null)
        {
            FileLog.Write("[DevThrottleAccountService] GetIdentity: no stored credential -> no identity");
            return null;
        }

        var identity = JwtIdentityReader.Read(tokens.AccessToken);
        FileLog.Write($"[DevThrottleAccountService] GetIdentity: identity={(identity is null ? "<none>" : "resolved")}");
        return identity;
    }

    /// <summary>
    /// Returns the stored access token to attach when this install acts as the single egress to the
    /// cloud (the Gateway forwarding telemetry on the Director's behalf, issue #639), or null when the
    /// install is not signed in. "Signed in" here is the same local check <see cref="IsLoggedIn"/>
    /// applies - a stored token whose signature verifies and is valid-or-renewable - so a tampered or
    /// absent credential yields null and the caller must NOT forward. The returned token value is for
    /// attaching to an outbound request ONLY and is NEVER written to the log; this method logs only
    /// whether a token was available, never the token itself (security rule DT-05).
    /// </summary>
    public string? GetAccessTokenForForwarding()
    {
        DevThrottleTokens? tokens;
        lock (_gate)
        {
            tokens = _store.Load();
        }

        if (tokens is null)
        {
            FileLog.Write("[DevThrottleAccountService] GetAccessTokenForForwarding: no stored credential -> no token (caller must not forward)");
            return null;
        }

        var validation = _validator.Validate(tokens.AccessToken);
        var available = validation.IsValid || validation.IsExpiredButWellFormed;
        FileLog.Write($"[DevThrottleAccountService] GetAccessTokenForForwarding: tokenAvailable={available} (no network call)");
        return available ? tokens.AccessToken : null;
    }

    /// <summary>
    /// Clears the stored credential and records a "logout" event. After this the next
    /// <see cref="IsLoggedIn"/> returns false.
    /// </summary>
    public void Logout()
    {
        FileLog.Write("[DevThrottleAccountService] Logout: clearing credential store");
        lock (_gate)
        {
            var wasLoggedIn = _store.HasTokens;
            _store.Clear();
            if (wasLoggedIn)
                _eventLog.RecordLoggedOut();
        }
        FileLog.Write("[DevThrottleAccountService] Logout: cleared");
    }
}
