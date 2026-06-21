using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The outcome of a first-run login attempt (issue #581). Carries whether a credential was captured
/// and stored, and - on failure - a short, user-safe reason (never the token, never an internal
/// stack trace; security rule DT-05).
/// </summary>
/// <param name="Succeeded">True when a credential was captured from the browser and stored through #583.</param>
/// <param name="FailureReason">A user-safe reason when <paramref name="Succeeded"/> is false; null on success.</param>
public sealed record FirstRunLoginResult(bool Succeeded, string? FailureReason)
{
    public static FirstRunLoginResult Success() => new(true, null);
    public static FirstRunLoginResult Failure(string reason) => new(false, reason);
}

/// <summary>
/// Orchestrates the first-run login hand-off (issue #581): from the startup gate's "Log in" action,
/// it stands up the loopback listener that will receive the credential, opens the user's system
/// browser at the DevThrottle sign-in address (carrying the loopback callback so the completion knows
/// where to hand the credential back), waits for the browser to hand the credential back, and stores
/// the captured token pair through the credential service (issue #583). After this returns success
/// the caller clears the gate and shows the main window with no restart.
///
/// The sign-in page and the token hand-back are owned by the internal-repository backend (a flagged
/// dependency). Until that exists the same path is exercised by a local stand-in completion that
/// posts a test-issued token to the loopback callback - the listener and this coordinator do not
/// know or care whether the caller of the callback is the real backend or the stand-in.
/// </summary>
public sealed class FirstRunLoginCoordinator
{
    /// <summary>
    /// The environment variable carrying the DevThrottle sign-in base address. The live backend
    /// sign-in page is owned by the internal-repository backend; this is a documented seam so the
    /// flow can point at the stand-in completion while the backend does not yet exist. When unset the
    /// documented default below is used.
    /// </summary>
    public const string SignInBaseUrlEnvVar = "DEVTHROTTLE_SIGNIN_URL";

    /// <summary>The default DevThrottle sign-in address used when the env seam is unset.</summary>
    public const string DefaultSignInBaseUrl = "https://devthrottle.com/signin";

    private readonly DevThrottleAccountService _account;
    private readonly Func<string, Task> _openBrowser;
    private readonly Func<LoopbackLoginListener> _listenerFactory;

    /// <summary>
    /// Creates the coordinator. The collaborators are injected so the flow is testable without a real
    /// browser or a real credential store (no fallback construction - each is required).
    /// </summary>
    /// <param name="account">The credential service (issue #583) the captured tokens are stored through.</param>
    /// <param name="openBrowser">
    /// Opens the system browser at the sign-in URL. Defaults to <see cref="BrowserLauncher.OpenSystemDefault"/>.
    /// </param>
    /// <param name="listenerFactory">
    /// Creates the loopback listener that receives the hand-back. Defaults to a real
    /// <see cref="LoopbackLoginListener"/>.
    /// </param>
    public FirstRunLoginCoordinator(
        DevThrottleAccountService account,
        Func<string, Task>? openBrowser = null,
        Func<LoopbackLoginListener>? listenerFactory = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _openBrowser = openBrowser ?? (url =>
        {
            BrowserLauncher.OpenSystemDefault(url);
            return Task.CompletedTask;
        });
        _listenerFactory = listenerFactory ?? (() => new LoopbackLoginListener());
    }

    /// <summary>
    /// Computes the sign-in URL for a given loopback callback - the configured sign-in base address
    /// with the loopback callback appended as the <c>redirect_uri</c> query parameter, so the sign-in
    /// completion knows where to hand the credential back.
    /// </summary>
    public static string BuildSignInUrl(Uri callbackUrl)
    {
        if (callbackUrl is null)
            throw new ArgumentNullException(nameof(callbackUrl));

        var baseUrl = Environment.GetEnvironmentVariable(SignInBaseUrlEnvVar);
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = DefaultSignInBaseUrl;

        var separator = baseUrl.Contains('?') ? '&' : '?';
        return $"{baseUrl}{separator}redirect_uri={Uri.EscapeDataString(callbackUrl.ToString())}";
    }

    /// <summary>
    /// Runs the first-run login: opens the system browser at the sign-in address, waits for the
    /// browser hand-back on the loopback callback, and stores the captured credential through #583.
    /// Returns a <see cref="FirstRunLoginResult"/> the caller uses to clear the gate (success) or
    /// keep the user on the gate with a message (failure or cancellation). Never throws to the caller
    /// for an expected failure - it returns a user-safe failure instead.
    /// </summary>
    public async Task<FirstRunLoginResult> RunAsync(CancellationToken ct = default)
    {
        FileLog.Write("[FirstRunLoginCoordinator] RunAsync: starting first-run login hand-off");

        using var listener = _listenerFactory();
        var signInUrl = BuildSignInUrl(listener.CallbackUrl);

        try
        {
            FileLog.Write($"[FirstRunLoginCoordinator] RunAsync: launching system browser at {signInUrl}");
            await _openBrowser(signInUrl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunLoginCoordinator] RunAsync: could not open the system browser: {ex.Message}");
            return FirstRunLoginResult.Failure(
                "DevThrottle could not open your web browser to sign in. Please check that you have a default browser set, then try again.");
        }

        DevThrottleTokens tokens;
        try
        {
            tokens = await listener.WaitForCredentialAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[FirstRunLoginCoordinator] RunAsync: login cancelled before a credential arrived");
            return FirstRunLoginResult.Failure("Sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FirstRunLoginCoordinator] RunAsync: hand-back capture failed: {ex.Message}");
            return FirstRunLoginResult.Failure(
                "Sign-in did not complete. Please return to your browser and finish signing in, then try again.");
        }

        _account.StoreTokens(tokens);
        FileLog.Write("[FirstRunLoginCoordinator] RunAsync: credential captured and stored through the credential service");
        return FirstRunLoginResult.Success();
    }
}
