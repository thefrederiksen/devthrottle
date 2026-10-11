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
/// This is the DevThrottle account, distinct from the Claude accounts a machine holds (the license key
/// store, <c>cc-devthrottle license</c>). The store binding is injected so Windows Data Protection is used on Windows
/// and the macOS Keychain can be a later drop-in.
/// </summary>
public sealed class DevThrottleAccountService
{
    /// <summary>
    /// How much life must remain on the access token before the background refresh renews it (issue
    /// #876). Renewing PROACTIVELY - inside this margin, not only after expiry - means outbound calls
    /// device and hosted-service calls never present an already-expired token.
    /// </summary>
    public static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(10);

    private readonly IProtectedTokenStore _store;
    private readonly JwtAccessTokenValidator _validator;
    private readonly ITokenRefresher _refresher;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    // Token rotation makes overlapping exchanges actively harmful (the second one presents an
    // already-rotated refresh token and looks revoked), so refresh passes are single-flight.
    private readonly SemaphoreSlim _refreshFlight = new(1, 1);
    // The persistent-refresh-failure signal (issue #911): set when a refresh attempt fails with a
    // misconfiguration that retrying alone will not fix (a missing/invalid apikey - the endpoint
    // answering 401 "No API key found in request"). While this is set an expired access token can
    // never be renewed, so the cached credential is not genuinely usable and must not read as signed
    // in. Cleared on the next successful refresh. Guarded by _gate.
    private bool _refreshPersistentlyFailing;

    /// <summary>
    /// Creates the service from its collaborators. None is optional - each one is a real dependency
    /// the service needs to do its job (no fallback construction).
    /// </summary>
    /// <param name="store">The operating system credential store binding (encrypted at rest).</param>
    /// <param name="validator">The local signature-and-expiry validator (no network).</param>
    /// <param name="refresher">The backend refresh-token exchange (the one network-touching seam).</param>
    /// <param name="timeProvider">Time source for the proactive-renewal margin; defaults to the system clock. Injected so tests control "now".</param>
    public DevThrottleAccountService(
        IProtectedTokenStore store,
        JwtAccessTokenValidator validator,
        ITokenRefresher refresher,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Stores the token pair handed back by the login-completion flow in the operating system
    /// credential store.
    /// </summary>
    public void StoreTokens(DevThrottleTokens tokens)
    {
        if (tokens is null)
            throw new ArgumentNullException(nameof(tokens));

        FileLog.Write("[DevThrottleAccountService] StoreTokens: storing token pair in credential store");
        lock (_gate)
        {
            _store.Save(tokens);
        }
        FileLog.Write("[DevThrottleAccountService] StoreTokens: stored");
    }

    /// <summary>
    /// True when a refresh attempt has persistently failed on a misconfiguration that retrying alone
    /// will not fix (a missing/invalid <c>apikey</c>; issue #911). Surfaces the persistent-refresh-
    /// failure signal so the tray/Cockpit can show "sign-in needs attention" rather than a stale
    /// "Signed in", and so an expired-and-unrenewable credential does not read as usable. Cleared by
    /// the next successful refresh. No network call.
    /// </summary>
    public bool HasPersistentRefreshFailure
    {
        get
        {
            lock (_gate)
            {
                return _refreshPersistentlyFailing;
            }
        }
    }

    /// <summary>
    /// Whether ANY credential is stored at all, regardless of whether it is valid, expired, renewable, or
    /// even decodable. Read locally with NO network call.
    ///
    /// This exists to separate two states that used to collapse into one wrong answer (issue #984). An
    /// account route that finds <see cref="GetAccessTokenForForwarding"/> empty needs to know WHICH of these
    /// it is looking at: nothing stored (the user really is signed out, and telling them to sign in is
    /// useful), or something stored that will not forward (an internal inconsistency, where telling them to
    /// sign in sends them to redo an action that was already done and cannot help). Only the first is a
    /// sign-out. <see cref="IsLoggedIn"/> cannot answer this - it folds storage, validity and renewability
    /// into a single boolean and returns false for both.
    /// </summary>
    public bool HasStoredCredential()
    {
        DevThrottleTokens? tokens;
        lock (_gate)
        {
            tokens = _store.Load();
        }

        var stored = tokens is not null;
        FileLog.Write($"[DevThrottleAccountService] HasStoredCredential: {stored} (no network call)");
        return stored;
    }

    /// <summary>
    /// Answers "is this install logged in with a genuinely-usable token?" entirely from the cached
    /// credential, with NO outbound network call. Returns true when a stored access token's signature
    /// verifies AND it either has not expired, or is expired-but-well-formed WHILE the background
    /// refresh is healthy (a genuinely-ours token the refresh can still renew). Returns false when no
    /// credential is stored, the stored entry cannot be decrypted, the access token is tampered /
    /// wrong-signature, or the token is expired AND refresh is persistently failing (issue #911) - so
    /// a dead-and-unrenewable credential the cloud rejects does not read as signed in.
    /// </summary>
    public bool IsLoggedIn()
    {
        FileLog.Write("[DevThrottleAccountService] IsLoggedIn: checking cached credential locally (no network call)");

        DevThrottleTokens? tokens;
        bool refreshPersistentlyFailing;
        lock (_gate)
        {
            tokens = _store.Load();
            refreshPersistentlyFailing = _refreshPersistentlyFailing;
        }

        if (tokens is null)
        {
            FileLog.Write("[DevThrottleAccountService] IsLoggedIn: no stored credential -> false");
            return false;
        }

        var validation = _validator.Validate(tokens.AccessToken);
        // An expired-but-well-formed token normally still reads as signed in (a grace window while the
        // background refresh renews it). But once refresh is persistently failing it can never be
        // renewed and the cloud rejects it, so it is no longer a usable credential (issue #911).
        var expiredButRenewable = validation.IsExpiredButWellFormed && !refreshPersistentlyFailing;
        var loggedIn = validation.IsValid || expiredButRenewable;
        FileLog.Write($"[DevThrottleAccountService] IsLoggedIn: valid={validation.IsValid}, expiredButWellFormed={validation.IsExpiredButWellFormed}, refreshPersistentlyFailing={refreshPersistentlyFailing}, result={loggedIn} (no network call)");
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
                    // A successful renewal proves refresh is healthy again - clear any prior
                    // persistent-failure signal (issue #911).
                    _refreshPersistentlyFailing = false;
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
                    _store.Clear();
                    // The credential is gone; there is nothing left to renew, so reset the signal.
                    _refreshPersistentlyFailing = false;
                }
                return false;
            }

            if (result.RefreshMisconfigured)
            {
                // The exchange is persistently broken by a client-side misconfiguration (a missing or
                // invalid apikey - the endpoint answering 401 "No API key found in request"; issue
                // #911). The refresh token itself may be fine, so keep the cached credential, but raise
                // the persistent-failure signal so an expired token no longer reads as signed in and
                // the failure is surfaced instead of masked forever as a transient outage.
                FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: refresh persistently misconfigured (apikey missing/invalid) -> keeping cached credential but surfacing the persistent-refresh-failure signal; a fix or re-sign-in is required");
                lock (_gate)
                {
                    _refreshPersistentlyFailing = true;
                }
                return false;
            }

            FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: refresh unavailable (offline or backend error) -> keeping cached credential (transient; signal unchanged)");
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
    /// Returns this install's own account subject - the stable <c>sub</c> (account/user id) claim read
    /// locally from the cached access token, with NO network call - or null when no credential is stored
    /// or the cached token carries no subject claim. This is the account KEY the Gateway's account-
    /// membership check compares a presented cloud token against (issue #1079): a presented token is a
    /// member only when its subject equals this value. Read from the same cached credential
    /// <see cref="GetIdentity"/> uses, and like it this decodes the token's claims without a signature
    /// re-check (the credential was already accepted by the gate). The subject is a personally-
    /// identifying account id and is NEVER written to the log; this method logs only whether one was
    /// resolved (security: no personally identifiable information in logs).
    /// </summary>
    public string? GetAccountSubject()
    {
        FileLog.Write("[DevThrottleAccountService] GetAccountSubject: reading the account subject from the cached credential (no network call)");

        DevThrottleTokens? tokens;
        lock (_gate)
        {
            tokens = _store.Load();
        }

        if (tokens is null)
        {
            FileLog.Write("[DevThrottleAccountService] GetAccountSubject: no stored credential -> no subject");
            return null;
        }

        var subject = JwtIdentityReader.ReadSubject(tokens.AccessToken);
        FileLog.Write($"[DevThrottleAccountService] GetAccountSubject: subject={(subject is null ? "<none>" : "resolved")}");
        return subject;
    }

    /// <summary>
    /// Returns the stored access token to attach when this install acts as the single egress to the
    /// cloud (the Gateway performing an authenticated account operation), or null when the
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
    /// Clears the stored credential. After this the next
    /// <see cref="IsLoggedIn"/> returns false.
    /// </summary>
    public void Logout()
    {
        FileLog.Write("[DevThrottleAccountService] Logout: clearing credential store");
        lock (_gate)
        {
            _store.Clear();
        }
        FileLog.Write("[DevThrottleAccountService] Logout: cleared");
    }
}
