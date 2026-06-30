namespace CcDirector.Core.Account;

/// <summary>
/// The three visual states the Director's read-only sidebar ACCOUNT box can paint (issue #852).
/// These are presentation states, NOT a sign-in gate: #651/#664 removed the Director's own account
/// gate, so the Director always boots regardless and this box only mirrors the Gateway's status.
/// </summary>
public enum AccountIndicatorState
{
    /// <summary>The Gateway holds a valid DevThrottle credential. Painted green with the email.</summary>
    SignedIn,

    /// <summary>The Gateway answered and is signed out. Painted as the amber, clickable nudge.</summary>
    NotSignedIn,

    /// <summary>
    /// No Gateway is configured, or the Gateway could not be reached to read the status. Painted
    /// muted - explicitly NOT a "signed out" state, because an absent or unreachable Gateway tells us
    /// nothing about the credential and must never be shown as a false sign-out (issue #852).
    /// </summary>
    Unavailable,
}

/// <summary>The label and sub-text the ACCOUNT box shows for a given <see cref="AccountIndicatorState"/>.</summary>
/// <param name="State">Which of the three visual states to paint.</param>
/// <param name="Label">The bold status label (e.g. "SIGNED IN", "NOT SIGNED IN").</param>
/// <param name="Sub">The smaller descriptive line under the label (the email, the nudge, or the reason).</param>
public sealed record AccountIndicatorContent(AccountIndicatorState State, string Label, string Sub);

/// <summary>
/// Maps a <see cref="GatewayAccountStatus"/> (read from the Gateway's <c>GET /account/status</c>) to the
/// presentation content the Director's read-only sidebar ACCOUNT box renders (issue #852). This is the
/// single, pure, unit-tested decision point for the three visual states, and it encodes the load-bearing
/// rule that an unreachable / not-configured Gateway is shown as a MUTED "unavailable" state, never a
/// false "signed out". Colors live with the surface (the Avalonia code-behind) - this layer is UI-free so
/// it can be tested without a UI thread (CodingStyle Section 8: Core has no UI dependencies).
///
/// It only ever surfaces the identity (email + provider) the status carries; it can never render a token,
/// because <see cref="GatewayAccountStatus"/> carries none (security rule DT-05).
/// </summary>
public static class AccountIndicatorPresenter
{
    /// <summary>
    /// Describe how the ACCOUNT box should paint for the given Gateway account status.
    /// </summary>
    /// <param name="status">The status read from the Gateway (or the not-configured sentinel).</param>
    /// <returns>The visual state plus the label and sub-text to display.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="status"/> is null.</exception>
    public static AccountIndicatorContent Describe(GatewayAccountStatus status)
    {
        if (status is null)
            throw new ArgumentNullException(nameof(status));

        // No Gateway configured at all: the account lives on the Gateway, so without one there is
        // nothing to read. Muted, never "signed out".
        if (!status.GatewayConfigured)
            return new AccountIndicatorContent(
                AccountIndicatorState.Unavailable,
                "ACCOUNT",
                "no Gateway configured - account lives on the Gateway");

        // Configured but the read failed (Gateway down, tailnet unreachable, non-success): the
        // credential state is genuinely unknown. Muted, never a false "signed out" (issue #852).
        if (!status.Reachable)
            return new AccountIndicatorContent(
                AccountIndicatorState.Unavailable,
                "ACCOUNT UNAVAILABLE",
                "cannot reach the Gateway to read account status");

        // Reachable and signed out: the actionable nudge. The click target opens the Cockpit Account
        // page so the person can sign in on the Gateway (the Director itself never signs in).
        if (!status.SignedIn)
            return new AccountIndicatorContent(
                AccountIndicatorState.NotSignedIn,
                "NOT SIGNED IN",
                "click to sign in on the Gateway");

        // Signed in: show the identity. The email is present in the normal case; guard the rare
        // signed-in-but-identity-unavailable case so the line is never empty.
        var identity = string.IsNullOrWhiteSpace(status.Email)
            ? "signed in (identity unavailable)"
            : status.Email;
        return new AccountIndicatorContent(AccountIndicatorState.SignedIn, "SIGNED IN", identity);
    }
}
