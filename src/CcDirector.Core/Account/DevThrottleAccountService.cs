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
    private readonly IProtectedTokenStore _store;
    private readonly JwtAccessTokenValidator _validator;
    private readonly AuthEventLog _eventLog;
    private readonly ITokenRefresher _refresher;
    private readonly object _gate = new();

    /// <summary>
    /// Creates the service from its collaborators. None is optional - each one is a real dependency
    /// the service needs to do its job (no fallback construction).
    /// </summary>
    /// <param name="store">The operating system credential store binding (encrypted at rest).</param>
    /// <param name="validator">The local signature-and-expiry validator (no network).</param>
    /// <param name="eventLog">The authentication-floor event recorder.</param>
    /// <param name="refresher">The backend refresh-token exchange (the one network-touching seam).</param>
    public DevThrottleAccountService(
        IProtectedTokenStore store,
        JwtAccessTokenValidator validator,
        AuthEventLog eventLog,
        ITokenRefresher refresher)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _eventLog = eventLog ?? throw new ArgumentNullException(nameof(eventLog));
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
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
    /// token has expired and connectivity is available. When the access token is still valid this
    /// is a no-op. When the refresh exchange returns no token pair (offline or the backend declines)
    /// the service keeps running on the cached credential and returns false. Returns true only when
    /// a renewed token pair was stored.
    /// </summary>
    public async Task<bool> RefreshIfNeededAsync(CancellationToken ct = default)
    {
        FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: evaluating cached credential");

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
        if (validation.IsValid)
        {
            FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: access token still valid -> no refresh needed");
            return false;
        }

        if (!validation.IsExpiredButWellFormed)
        {
            FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: access token not renewable (tampered/wrong-signature) -> not refreshing");
            return false;
        }

        FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: access token expired, attempting background refresh");
        var renewed = await _refresher.RefreshAsync(tokens.RefreshToken, ct);
        if (renewed is null)
        {
            FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: refresh unavailable (offline or declined) -> keeping cached credential");
            return false;
        }

        lock (_gate)
        {
            _store.Save(renewed);
        }
        FileLog.Write("[DevThrottleAccountService] RefreshIfNeededAsync: refreshed access token stored");
        return true;
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
