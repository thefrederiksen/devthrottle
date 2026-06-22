using System.Diagnostics;
using CcDirector.Core.Account;

namespace CcDirectorSetup.Services;

/// <summary>
/// The outcome of an installer sign-in attempt (issue #657). Carries only whether a credential was
/// captured and, on a non-success, a short user-safe reason. It deliberately does NOT carry the token:
/// the installer's job for this issue is to force a completed sign-in, not to keep the credential
/// (persisting it for the app to reuse is issue #658). Keeping the token out of this result is also
/// what guarantees the token can never reach the installer log.
/// </summary>
/// <param name="Outcome">What happened: signed in, cancelled, timed out, or failed.</param>
/// <param name="Message">A user-safe message describing the outcome; never the token.</param>
public sealed record SignInResult(SignInOutcome Outcome, string Message)
{
    public bool Succeeded => Outcome == SignInOutcome.SignedIn;
}

/// <summary>What an installer sign-in attempt ended as.</summary>
public enum SignInOutcome
{
    /// <summary>The browser handed a credential back over the loopback callback.</summary>
    SignedIn,

    /// <summary>The user pressed Cancel before the credential arrived.</summary>
    Cancelled,

    /// <summary>No credential arrived within the timeout (an abandoned sign-in).</summary>
    TimedOut,

    /// <summary>The browser could not be opened, or the hand-back was malformed.</summary>
    Failed
}

/// <summary>
/// Drives the forced first-run sign-in inside the setup wizard (issue #657). It reuses the exact same
/// loopback contract the Director uses: it stands up a <see cref="LoopbackLoginListener"/> on
/// <c>127.0.0.1</c>, builds the sign-in URL with the loopback callback as the <c>redirect_uri</c> via
/// <see cref="FirstRunLoginCoordinator.BuildSignInUrl"/>, opens the system browser there, and waits for
/// the browser to hand the credential back. The wait honors both a caller cancellation (the Cancel
/// button) and a timeout (an abandoned sign-in), so the step is never a dead end.
///
/// For development and QA the same path runs against the local stand-in
/// <c>tools/devthrottle-dev-signin</c> (pointed at by <c>DEVTHROTTLE_SIGNIN_URL</c>); the listener and
/// this runner do not know or care whether the real backend or the stand-in completes the hand-back.
///
/// The captured access token is NEVER written to the installer log (an explicit acceptance criterion):
/// this runner logs only that a credential was captured, never its value, and does not return the token.
/// </summary>
public sealed class SignInRunner
{
    /// <summary>The default time to wait for the browser hand-back before treating the attempt as
    /// abandoned. Mirrors the app's gate: long enough for a real sign-in, short enough to recover.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly Func<LoopbackLoginListener> _listenerFactory;
    private readonly Action<string> _openBrowser;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Creates the runner. The collaborators are injected so the flow is testable without a real
    /// browser or a real loopback callback (no fallback construction - each has an explicit default).
    /// </summary>
    /// <param name="listenerFactory">
    /// Creates the loopback listener that receives the hand-back. Defaults to a real
    /// <see cref="LoopbackLoginListener"/>.
    /// </param>
    /// <param name="openBrowser">
    /// Opens the system browser at the sign-in URL. Defaults to a shell-executed
    /// <see cref="Process.Start(ProcessStartInfo)"/>.
    /// </param>
    /// <param name="timeout">
    /// How long to wait for the hand-back before timing out. Defaults to <see cref="DefaultTimeout"/>.
    /// </param>
    public SignInRunner(
        Func<LoopbackLoginListener>? listenerFactory = null,
        Action<string>? openBrowser = null,
        TimeSpan? timeout = null)
    {
        _listenerFactory = listenerFactory ?? (() => new LoopbackLoginListener());
        _openBrowser = openBrowser ?? OpenSystemBrowser;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Runs one sign-in attempt: opens the system browser at the sign-in address and waits for the
    /// browser to hand a credential back on the loopback callback. Returns a <see cref="SignInResult"/>
    /// the wizard uses to enable Next (success) or stay on a retryable state (cancel, timeout, or
    /// failure). It never throws for an expected outcome - it returns a user-safe result instead.
    /// </summary>
    /// <param name="ct">
    /// Cancelled by the wizard's Cancel button. A cancellation produces <see cref="SignInOutcome.Cancelled"/>.
    /// </param>
    public async Task<SignInResult> RunAsync(CancellationToken ct = default)
    {
        SetupLog.Write("[SignInRunner] RunAsync: starting installer sign-in hand-off");

        using var listener = _listenerFactory();
        var signInUrl = FirstRunLoginCoordinator.BuildSignInUrl(listener.CallbackUrl);
        SetupLog.Write($"[SignInRunner] RunAsync: sign-in url={signInUrl}");

        try
        {
            _openBrowser(signInUrl);
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[SignInRunner] RunAsync: could not open the system browser: {ex.Message}");
            return new SignInResult(SignInOutcome.Failed,
                "Could not open your web browser to sign in. Please check that you have a default browser set, then try again.");
        }

        // The wait ends on whichever happens first: the user's Cancel (ct), the timeout, or the
        // browser hand-back. The timeout is its own source so we can tell "abandoned" from "cancelled".
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);

        try
        {
            // The token pair is captured here and then deliberately discarded - persistence is issue
            // #658. The value is never logged and never returned (acceptance criterion: no token in
            // the installer log).
            await listener.WaitForCredentialAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (timeoutSource.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                SetupLog.Write("[SignInRunner] RunAsync: timed out waiting for the browser hand-back");
                return new SignInResult(SignInOutcome.TimedOut,
                    "Sign-in timed out. The browser sign-in was not completed in time - please try again.");
            }

            SetupLog.Write("[SignInRunner] RunAsync: sign-in cancelled before a credential arrived");
            return new SignInResult(SignInOutcome.Cancelled,
                "Sign-in was cancelled. Click \"Sign in to DevThrottle\" to try again.");
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[SignInRunner] RunAsync: hand-back capture failed: {ex.Message}");
            return new SignInResult(SignInOutcome.Failed,
                "Sign-in did not complete. Please return to your browser and finish signing in, then try again.");
        }

        SetupLog.Write("[SignInRunner] RunAsync: credential captured from the browser hand-back");
        return new SignInResult(SignInOutcome.SignedIn, "Signed in to DevThrottle.");
    }

    /// <summary>Opens the user's default browser at the given URL via the shell.</summary>
    private static void OpenSystemBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
