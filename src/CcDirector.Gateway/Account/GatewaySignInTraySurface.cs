namespace CcDirector.Gateway.Account;

/// <summary>
/// The pure, UI-framework-free decisions the Gateway tray (CcDirector.GatewayApp) makes about the
/// DevThrottle sign-in surface (issue #637). Kept here in the library - not buried in the Avalonia
/// flyout-building method - so the "when does the tray prompt / show the Sign in action / show the
/// account row" logic is unit-testable without an Avalonia UI thread. The tray reads these to decide:
/// whether to auto-prompt on launch, whether to show the "Sign in to DevThrottle" action, and what to
/// show in the account status row.
/// </summary>
public static class GatewaySignInTraySurface
{
    /// <summary>The label of the tray action that starts/retries the browser loopback sign-in.</summary>
    public const string SignInActionText = "Sign in to DevThrottle";

    /// <summary>The account status-row label shown on the tray flyout.</summary>
    public const string AccountRowLabel = "Account";

    /// <summary>
    /// Whether the Gateway should AUTO-PROMPT the browser sign-in on launch: only when a sign-in flow
    /// exists on this host and the Gateway is not already signed in. A host with no credential service
    /// (<paramref name="signIn"/> null) never prompts; an already-signed-in Gateway never re-prompts
    /// on a subsequent launch (acceptance criterion 3).
    /// </summary>
    public static bool ShouldPromptOnLaunch(GatewaySignInService? signIn) =>
        signIn is not null && !signIn.IsSignedIn();

    /// <summary>
    /// Whether the tray flyout should SHOW the "Sign in to DevThrottle" action: when a sign-in flow
    /// exists and the Gateway is not signed in, so it is the start/retry surface for the forced
    /// first-launch sign-in and the retry after a failed or cancelled attempt (acceptance criteria
    /// 1 and 4). Hidden once signed in.
    /// </summary>
    public static bool ShouldShowSignInAction(GatewaySignInService? signIn) =>
        signIn is not null && !signIn.IsSignedIn();

    /// <summary>
    /// The account status-row value for the tray flyout, read locally from the cached credential (no
    /// network call): "Signed in (email)" when signed in with a readable identity, "Signed in" when
    /// signed in without a readable email, otherwise "Not signed in". Returns null when there is no
    /// sign-in flow on this host (the row is omitted rather than showing a misleading state).
    /// </summary>
    public static string? AccountRowValue(GatewaySignInService? signIn)
    {
        if (signIn is null)
            return null;
        if (!signIn.IsSignedIn())
            return "Not signed in";
        var email = signIn.GetIdentity()?.Email;
        return string.IsNullOrEmpty(email) ? "Signed in" : $"Signed in ({email})";
    }
}
