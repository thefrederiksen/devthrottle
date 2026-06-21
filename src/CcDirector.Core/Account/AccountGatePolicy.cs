using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The startup gate policy (issue #580). It consumes the offline "is this install logged in?" check
/// delivered by the credential service (issue #583) and decides whether the Director may reach a
/// usable main window (<see cref="GateDecision.Start"/>) or must block and route the user to log in
/// (<see cref="GateDecision.Block"/>). It also owns the rule that, once started, the online session
/// validation or refresh runs in the background without blocking the main window.
///
/// The decision is a fast, local check with NO outbound network call (the credential service answers
/// from the cached credential), so it never delays the splash-to-window transition. The background
/// validation - the one network-touching step - is started only after the window is shown, by the
/// caller, via <see cref="StartBackgroundValidation"/>.
/// </summary>
public sealed class AccountGatePolicy
{
    private readonly DevThrottleAccountService _account;

    /// <summary>
    /// Creates the policy over the credential service that answers the logged-in check and performs
    /// the background refresh. The service is required (no fallback construction).
    /// </summary>
    /// <param name="account">The DevThrottle credential service from issue #583.</param>
    public AccountGatePolicy(DevThrottleAccountService account)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
    }

    /// <summary>
    /// Decides whether the Director may start to the main window. Returns <see cref="GateDecision.Start"/>
    /// when a usable cached credential exists (online or offline), and <see cref="GateDecision.Block"/>
    /// when no credential has ever been stored on this install. This is a local check - no network call.
    /// </summary>
    public GateDecision Decide()
    {
        FileLog.Write("[AccountGatePolicy] Decide: evaluating startup gate (local check, no network call)");
        var loggedIn = _account.IsLoggedIn();
        var decision = loggedIn ? GateDecision.Start : GateDecision.Block;
        FileLog.Write($"[AccountGatePolicy] Decide: loggedIn={loggedIn}, decision={decision}");
        return decision;
    }

    /// <summary>
    /// Starts the quiet online session validation or refresh in the background. This is called only
    /// AFTER the main window is shown, so it never delays the window appearing - when offline the
    /// refresh is unavailable and the Director keeps running on the cached credential; a revoked or
    /// expired session takes effect on the next successful online validation. The returned task runs
    /// the refresh on a background thread; failures only log and never surface to the user.
    /// </summary>
    public Task StartBackgroundValidation(CancellationToken ct = default)
    {
        FileLog.Write("[AccountGatePolicy] StartBackgroundValidation: scheduling background session validation (does not block the main window)");
        return Task.Run(async () =>
        {
            try
            {
                var refreshed = await _account.RefreshIfNeededAsync(ct);
                FileLog.Write($"[AccountGatePolicy] StartBackgroundValidation: completed, refreshed={refreshed}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AccountGatePolicy] StartBackgroundValidation FAILED (keeping cached credential): {ex.Message}");
            }
        }, ct);
    }
}
