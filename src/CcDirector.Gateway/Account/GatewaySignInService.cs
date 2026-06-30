using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Drives the DevThrottle browser loopback sign-in ON THE GATEWAY (issue #637, Gateway Centralization
/// Phase 2). Per plan decision #2 the forced sign-in happens at the Gateway's first launch, so the
/// browser loopback flow that used to live on the Director now runs here: when the Gateway has no
/// stored credential it prompts sign-in (opens the system browser at the configured sign-in address),
/// captures the credential the sign-in completion hands back on the loopback callback, and stores it
/// through the Gateway-hosted credential service (issue #636, the reused
/// <see cref="DevThrottleAccountService"/> exposed as <c>GatewayHost.Account</c>).
///
/// The browser-open and loopback capture are the reused Core <see cref="FirstRunLoginCoordinator"/> +
/// <see cref="LoopbackLoginListener"/> - the exact same mechanism the Director used, not a copy. This
/// service adds the Gateway-side concerns: a single-flight guard so an auto-prompt at launch and a
/// tray "Sign in" click never run two browser hand-offs at once, and the "is the Gateway signed in?"
/// query the tray reads to decide whether to prompt. Login-telemetry forwarding is out of scope for
/// this issue (it is the Gateway's own relay, issues #628 / #630), so a no-op login reporter is
/// injected here rather than having the Gateway report a login to itself.
///
/// The access and refresh tokens are never written to the log on any path (security rule DT-05,
/// carried over from #636) - only the outcome (signed in / failed-with-user-safe-reason) is logged.
/// </summary>
public sealed class GatewaySignInService
{
    private readonly DevThrottleAccountService _account;
    private readonly FirstRunLoginCoordinator _coordinator;
    private readonly Func<CancellationToken, Task>? _onSignedIn;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    /// <summary>
    /// Creates the Gateway sign-in service over the Gateway-hosted credential service.
    /// </summary>
    /// <param name="account">
    /// The Gateway-hosted DevThrottle credential service (issue #636) the captured token is stored
    /// through and queried for the signed-in state. Required.
    /// </param>
    /// <param name="openBrowser">
    /// Opens the system browser at the sign-in URL. Defaults to the Core coordinator's real
    /// system-browser launcher; tests inject a recording opener so the flow is provable without a
    /// real browser.
    /// </param>
    /// <param name="listenerFactory">
    /// Creates the loopback listener that receives the credential hand-back. Defaults to the Core
    /// coordinator's real <see cref="LoopbackLoginListener"/>; tests inject a shared real listener so
    /// a stand-in completion can post the token to it.
    /// </param>
    /// <param name="onSignedIn">
    /// An optional best-effort hook fired AFTER a successful sign-in once the credential is stored
    /// (issue #857: register this Gateway as a device with the cloud). It runs detached on the thread
    /// pool with its own boundary try/catch, so a slow or failed hook never blocks or fails the sign-in -
    /// the Gateway stays signed in regardless. Null when no post-sign-in action is wired.
    /// </param>
    public GatewaySignInService(
        DevThrottleAccountService account,
        Func<string, Task>? openBrowser = null,
        Func<LoopbackLoginListener>? listenerFactory = null,
        Func<CancellationToken, Task>? onSignedIn = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _onSignedIn = onSignedIn;
        // The login telemetry report is the Gateway's OWN relay (issues #628/#630), not this flow's
        // job, so inject a no-op reporter rather than have the Gateway POST a login to itself.
        _coordinator = new FirstRunLoginCoordinator(
            account,
            openBrowser,
            listenerFactory,
            loginReporter: new NoOpLoginTelemetryReporter());
    }

    /// <summary>
    /// Answers "is the Gateway signed in to DevThrottle?" entirely from the cached credential, with no
    /// network call. The tray reads this on launch to decide whether to auto-prompt sign-in.
    /// </summary>
    public bool IsSignedIn() => _account.IsLoggedIn();

    /// <summary>
    /// True while a sign-in hand-off is already in flight, so the tray can show "signing in..." and not
    /// start a second one. The single-flight guard makes a concurrent call a no-op regardless.
    /// </summary>
    public bool IsSignInRunning => _singleFlight.CurrentCount == 0;

    /// <summary>
    /// Returns the signed-in identity (email and provider) read locally from the cached token, or null
    /// when the Gateway is not signed in. The tray surfaces it so the user sees who is signed in.
    /// </summary>
    public AccountIdentity? GetIdentity() => _account.GetIdentity();

    /// <summary>
    /// Runs the browser loopback sign-in once: opens the system browser at the configured sign-in
    /// address, waits for the credential hand-back on the loopback callback, and stores the captured
    /// token through the credential service. Single-flight: if a sign-in is already running this
    /// returns a failure that says so rather than starting a second browser hand-off. Never throws for
    /// an expected failure (browser cannot open, cancellation, half-credential) - it returns a
    /// user-safe <see cref="FirstRunLoginResult"/> the caller surfaces, so a failed or cancelled
    /// sign-in leaves the Gateway un-signed-in and retryable.
    /// </summary>
    public async Task<FirstRunLoginResult> RunSignInAsync(CancellationToken ct = default)
    {
        FileLog.Write("[GatewaySignInService] RunSignInAsync: requested");

        if (!await _singleFlight.WaitAsync(0, ct).ConfigureAwait(false))
        {
            FileLog.Write("[GatewaySignInService] RunSignInAsync: a sign-in is already in flight -> ignoring the duplicate request");
            return FirstRunLoginResult.Failure("A sign-in is already in progress. Finish it in your browser, or try again once it completes.");
        }

        try
        {
            var result = await _coordinator.RunAsync(ct).ConfigureAwait(false);
            FileLog.Write(result.Succeeded
                ? "[GatewaySignInService] RunSignInAsync: signed in - credential stored through the Gateway credential service"
                : $"[GatewaySignInService] RunSignInAsync: not signed in - {result.FailureReason}");

            if (result.Succeeded)
                FireOnSignedInBestEffort(ct);

            return result;
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    /// <summary>
    /// Fires the post-sign-in hook (issue #857: register this Gateway as a device) fully detached on the
    /// thread pool with its own boundary try/catch, so a slow or failed registration can never block or
    /// fail the user's sign-in - the Gateway stays signed in either way. A no-op when no hook is wired.
    /// </summary>
    private void FireOnSignedInBestEffort(CancellationToken ct)
    {
        var hook = _onSignedIn;
        if (hook is null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await hook(ct).ConfigureAwait(false);
                FileLog.Write("[GatewaySignInService] FireOnSignedInBestEffort: post-sign-in hook completed");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewaySignInService] FireOnSignedInBestEffort: post-sign-in hook failed (ignored, best-effort; Gateway stays signed in): {ex.Message}");
            }
        }, ct);
    }

    /// <summary>
    /// A login-telemetry reporter that does nothing. On the Gateway the login event egress is the
    /// Gateway's own login-telemetry relay (issues #628 / #630), not this sign-in flow, so the
    /// coordinator's best-effort report is a deliberate no-op here.
    /// </summary>
    private sealed class NoOpLoginTelemetryReporter : ILoginTelemetryReporter
    {
        public Task ReportLoginAsync(string accessToken, CancellationToken ct = default) => Task.CompletedTask;
    }
}
