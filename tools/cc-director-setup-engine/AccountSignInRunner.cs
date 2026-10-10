using System.Diagnostics;
using CcDirector.Core.Account;
using CcDirector.Core.Storage;

namespace CcDirector.Setup.Engine;

/// <summary>What a headless account sign-in attempt ended as.</summary>
public enum AccountSignInOutcome
{
    /// <summary>The browser handed a credential back over the loopback callback and it was stored.</summary>
    SignedIn,

    /// <summary>The caller cancelled before the credential arrived.</summary>
    Cancelled,

    /// <summary>No credential arrived within the timeout (an abandoned sign-in).</summary>
    TimedOut,

    /// <summary>The browser could not be opened, the hand-back was malformed, or the credential could not be stored.</summary>
    Failed,
}

/// <summary>
/// The outcome of a headless account sign-in. Carries only whether a credential was captured and stored
/// and, on a non-success, a short user-safe reason. It deliberately does NOT carry the token: keeping the
/// token off this result is what guarantees it can never reach the CLI output or a log (security rule DT-05).
/// </summary>
/// <param name="Outcome">What happened: signed in, cancelled, timed out, or failed.</param>
/// <param name="Message">A user-safe message describing the outcome; never the token.</param>
public sealed record AccountSignInResult(AccountSignInOutcome Outcome, string Message)
{
    public bool Succeeded => Outcome == AccountSignInOutcome.SignedIn;
}

/// <summary>
/// Signs a machine's DevThrottle account in from the command line, with no wizard window, and stores the
/// captured credential where the Gateway reads it - so a headless install (an agent, a script) can complete
/// the account sign-in the graphical wizard's sign-in step performs. It is the engine-side sibling of the
/// Windows wizard's <c>SignInRunner</c>, living in the engine so the headless CLI can drive the SAME flow:
/// a human and an agent sign in identically.
///
/// It reuses the exact loopback contract the app and the wizard use: it stands up a
/// <see cref="LoopbackLoginListener"/> on <c>127.0.0.1</c>, builds the sign-in URL with that loopback
/// callback as the <c>redirect_uri</c> via <see cref="FirstRunLoginCoordinator.BuildSignInUrl"/>, opens the
/// system browser there, and waits for the browser to hand the account token pair back. The user signs in -
/// or creates a free account - on devthrottle.com; that browser step is the only human action.
///
/// On a successful hand-back the captured token pair is persisted into the same operating-system credential
/// store the GATEWAY reads (Gateway Centralization, issues #636 / #642 / #651 / #906): a
/// <see cref="WindowsProtectedTokenStore"/> rooted at <see cref="CcStorage.GatewayDevThrottleCredentialBlob"/>
/// encrypts it at rest, so the Gateway's first-launch sign-in check sees "already signed in" and does not
/// re-prompt. Persistence happens ONLY on success; on cancel, timeout, or failure nothing is written. The
/// captured access token is NEVER written to a log and NEVER returned (security rule DT-05).
///
/// The default persist writes the Gateway store, which is Windows-only (Data Protection is per-user, and the
/// Gateway role is Windows-only); the caller guards the OS. The browser, listener, persist, and timeout are
/// all injectable so the flow is unit-testable with no browser, no backend, and no disk.
/// </summary>
public sealed class AccountSignInRunner
{
    /// <summary>The default time to wait for the browser hand-back before treating the attempt as
    /// abandoned. Mirrors the app and wizard gate: long enough for a real sign-in, short enough to recover.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly Func<LoopbackLoginListener> _listenerFactory;
    private readonly Action<string> _openBrowser;
    private readonly Action<DevThrottleTokens> _persistCredential;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Creates the runner. The collaborators are injected so the flow is testable without a real browser, a
    /// real loopback callback, or the real credential store (no fallback construction - each has an explicit
    /// default).
    /// </summary>
    /// <param name="listenerFactory">Creates the loopback listener that receives the hand-back. Defaults to a
    /// real <see cref="LoopbackLoginListener"/>.</param>
    /// <param name="openBrowser">Opens the system browser at the sign-in URL. Defaults to a shell-executed
    /// <see cref="Process.Start(ProcessStartInfo)"/>.</param>
    /// <param name="persistCredential">Persists the captured token pair into the Gateway's credential store.
    /// Defaults to a real <see cref="WindowsProtectedTokenStore"/> writing the encrypted blob the Gateway
    /// reads. Tests inject a recording seam so persistence is provable without Windows Data Protection.</param>
    /// <param name="timeout">How long to wait for the hand-back before timing out. Defaults to
    /// <see cref="DefaultTimeout"/>.</param>
    public AccountSignInRunner(
        Func<LoopbackLoginListener>? listenerFactory = null,
        Action<string>? openBrowser = null,
        Action<DevThrottleTokens>? persistCredential = null,
        TimeSpan? timeout = null)
    {
        _listenerFactory = listenerFactory ?? (() => new LoopbackLoginListener());
        _openBrowser = openBrowser ?? OpenSystemBrowser;
        _persistCredential = persistCredential ?? PersistToGatewayCredentialStore;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Runs one sign-in attempt: opens the system browser at the sign-in address and waits for the browser to
    /// hand a credential back on the loopback callback, then stores it. Returns an
    /// <see cref="AccountSignInResult"/> the caller renders. It never throws for an expected outcome - it
    /// returns a user-safe result (cancel, timeout, or failure) instead.
    /// </summary>
    /// <param name="ct">Cancelled by the caller. A cancellation produces <see cref="AccountSignInOutcome.Cancelled"/>.</param>
    public async Task<AccountSignInResult> RunAsync(CancellationToken ct = default)
    {
        EngineLog.Write("[AccountSignInRunner] RunAsync: starting headless account sign-in hand-off");

        using var listener = _listenerFactory();
        var signInUrl = FirstRunLoginCoordinator.BuildSignInUrl(listener.CallbackUrl);
        EngineLog.Write($"[AccountSignInRunner] RunAsync: sign-in url={signInUrl}");

        try
        {
            _openBrowser(signInUrl);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[AccountSignInRunner] RunAsync FAILED: could not open the system browser: {ex.Message}");
            return new AccountSignInResult(AccountSignInOutcome.Failed,
                "Could not open your web browser to sign in. Please check that you have a default browser set, then try again.");
        }

        // The wait ends on whichever happens first: the caller's cancellation (ct), the timeout, or the
        // browser hand-back. The timeout is its own source so we can tell "abandoned" from "cancelled".
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);

        DevThrottleTokens capturedTokens;
        try
        {
            // The token pair captured here is handed to the Gateway credential store below. The value is
            // never logged and never returned (security rule DT-05).
            capturedTokens = await listener.WaitForCredentialAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (timeoutSource.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // not-an-error: the person did not finish signing in within the wait
                EngineLog.Write("[AccountSignInRunner] RunAsync: timed out waiting for the browser hand-back");
                return new AccountSignInResult(AccountSignInOutcome.TimedOut,
                    "Sign-in timed out. The browser sign-in was not completed in time - please try again.");
            }

            // not-an-error: the sign-in was cancelled before a credential arrived (the person or the closing window stopped it); nothing failed
            EngineLog.Write("[AccountSignInRunner] RunAsync: sign-in cancelled before a credential arrived");
            return new AccountSignInResult(AccountSignInOutcome.Cancelled,
                "Sign-in was cancelled. Run 'signin' again to retry.");
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[AccountSignInRunner] RunAsync: hand-back capture FAILED: {ex.Message}");
            return new AccountSignInResult(AccountSignInOutcome.Failed,
                "Sign-in did not complete. Please return to your browser and finish signing in, then try again.");
        }

        if (capturedTokens is null || string.IsNullOrWhiteSpace(capturedTokens.AccessToken))
        {
            EngineLog.Write("[AccountSignInRunner] RunAsync: hand-back returned no usable credential");
            return new AccountSignInResult(AccountSignInOutcome.Failed,
                "Sign-in did not return a usable credential. Please try again.");
        }

        EngineLog.Write("[AccountSignInRunner] RunAsync: credential captured from the browser hand-back");

        // Persist ONLY on a successful capture - the cancel/timeout/failure paths above all returned first,
        // so no credential is ever written for an incomplete sign-in. A persistence failure is surfaced as a
        // failure (no fallback): if the credential cannot be stored, the machine is not "signed in", so we
        // must not report success.
        try
        {
            _persistCredential(capturedTokens);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[AccountSignInRunner] RunAsync: persisting the captured credential FAILED: {ex.Message}");
            return new AccountSignInResult(AccountSignInOutcome.Failed,
                "Signed in, but the sign-in could not be saved for the app. Please try again.");
        }

        EngineLog.Write("[AccountSignInRunner] RunAsync: captured credential persisted to the Gateway credential store");
        return new AccountSignInResult(AccountSignInOutcome.SignedIn, "Signed in to DevThrottle.");
    }

    /// <summary>Opens the user's default browser at the given URL via the shell.</summary>
    private static void OpenSystemBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>
    /// Persists the captured token pair into the GATEWAY's operating-system credential store (issue #906) -
    /// the same store the Windows wizard's SignInRunner writes, so the Gateway's first-launch sign-in check
    /// sees "already signed in" and does not re-prompt. Writing to the Director store would be thrown away
    /// (the Director deletes any credential of its own on startup - the Gateway is the account authority).
    /// The credential store creates its directory on write. The token value is never logged.
    /// </summary>
    private static void PersistToGatewayCredentialStore(DevThrottleTokens tokens)
    {
        // The Gateway credential store is Windows-only (Data Protection is per-user, and the Gateway role is
        // Windows-only). The CLI 'signin' command already refuses on non-Windows; this guard fails loud rather
        // than silently skipping the persist (no fallback), and satisfies the platform analyzer.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Gateway credential store is Windows-only.");

        EngineLog.Write("[AccountSignInRunner] PersistToGatewayCredentialStore: storing captured credential for the Gateway");
        var store = new WindowsProtectedTokenStore(CcStorage.GatewayDevThrottleCredentialBlob());
        var service = DevThrottleAccountFactory.Build(store);
        service.StoreTokens(tokens);
    }
}
