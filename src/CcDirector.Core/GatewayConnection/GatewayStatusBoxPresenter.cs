namespace CcDirector.Core.GatewayConnection;

/// <summary>
/// The four visual states the single bottom-left status box paints (design spec section 6). The box
/// replaces the two former boxes (GatewayIndicator + AccountIndicator) with ONE box carrying two check
/// lines. Colors live with the Avalonia surface; this enumeration is the surface-free instruction for
/// which of the four looks to paint.
/// </summary>
public enum GatewayStatusBoxVisual
{
    /// <summary>Needs attention: NotConfigured or ConnectedNotSignedIn. One or both lines show the next action.</summary>
    Amber,

    /// <summary>Working: the gateway host is shown with a "Connecting..." suffix.</summary>
    Yellow,

    /// <summary>All good: handshake proven AND the Gateway reports signed in. Both lines filled.</summary>
    Green,

    /// <summary>Was working and is now broken (or a first-time connect failed).</summary>
    Red,
}

/// <summary>
/// One of the two check lines the status box shows (spec section 6). The <see cref="Marker"/> is the paint
/// state of the line (filled / hollow / working / failed / muted) which the surface maps to a glyph and
/// color; the <see cref="Text"/> is the finished plain-English label the surface renders verbatim.
/// </summary>
/// <param name="Marker">The paint state of this line's marker.</param>
/// <param name="Text">The finished plain-English label for this line.</param>
public sealed record GatewayStatusLine(GatewayCheckState Marker, string Text);

/// <summary>
/// The complete content the single status box renders (spec section 6): the overall visual state, the two
/// check lines (Gateway reachable, Account signed in), and a tooltip. Colors and glyphs are applied by the
/// surface from the visual and the per-line markers; this record carries no color.
/// </summary>
/// <param name="Visual">Which of the four looks to paint.</param>
/// <param name="Connected">The "Connected" (Gateway reachable) check line.</param>
/// <param name="SignedIn">The "Signed in" (account) check line.</param>
/// <param name="Tooltip">The hover text summarizing the current state and the click action.</param>
/// <param name="ChipText">The two-or-three-word verdict the compact chip renders next to its status
/// dot. The chip is the box's slim form: the dot carries the visual state, this text names it, and
/// the two full check lines live on in <paramref name="Tooltip"/>.</param>
public sealed record GatewayStatusBoxContent(
    GatewayStatusBoxVisual Visual,
    GatewayStatusLine Connected,
    GatewayStatusLine SignedIn,
    string Tooltip,
    string ChipText);

/// <summary>
/// Turns a resolved Gateway connection snapshot into the single status box's content (design spec section
/// 6). This is the pure, unit-tested presenter for the box - it runs the same
/// <see cref="GatewayConnectionStateResolver"/> the panel uses (so the box and the panel can never
/// disagree) and maps the six resolver states down to the box's four visual states, plus the plain-English
/// text of the two check lines. It has NO UI and NO I/O (CodingStyle Section 8: Core has no UI
/// dependencies), following the same pattern as <see cref="Account.AccountIndicatorPresenter"/>.
///
/// The load-bearing rules it carries (spec sections 4, 6):
///   - The four visual states collapse the six resolver states: NotConfigured and ConnectedNotSignedIn are
///     both amber (needs attention); Connecting is yellow; AllGreen is green; ConnectFailed and
///     WasConnectedNowUnreachable are both red.
///   - The Connected line shows WHICH gateway plus the verdict: the marker (green check / amber ring /
///     red cross) reinforces connected / connecting / failed. The gateway host is shown first in every state
///     where a gateway is configured - failed included - and the resolved state follows in parentheses so
///     the verdict remains readable without relying on the glyph or color. The named failing leg stays in
///     the tooltip and the panel's repair banner. With no gateway selected (brand new, NotConfigured) the
///     line is left empty and the surface hides the row.
///   - A muted/unknown account line is "cannot tell yet", never a false sign-out (decision 3).
/// </summary>
public static class GatewayStatusBoxPresenter
{
    /// <summary>
    /// Describe how the single status box should paint for the given live snapshot.
    /// </summary>
    /// <param name="inputs">The full verification snapshot (the same inputs the panel resolves).</param>
    /// <param name="gatewayHost">The Gateway host name, shown on the Connected line and in the tooltip when
    /// a gateway is configured (may be null when no gateway is selected yet).</param>
    /// <param name="accountEmail">The signed-in account email, shown on the signed-in line when green (may be null).</param>
    public static GatewayStatusBoxContent Describe(
        GatewayConnectionInputs inputs, string? gatewayHost, string? accountEmail)
    {
        var resolved = GatewayConnectionStateResolver.Resolve(inputs);
        var visual = VisualFor(resolved.State);
        var connected = ConnectedLine(resolved, inputs.GatewayConfigured, gatewayHost, inputs.Refusal);
        var signedIn = SignedInLine(resolved.SignedInCheck, accountEmail);
        if (resolved.State == GatewayConnectionState.KeyRefused)
        {
            // The refusal words both: the chip says what happened where it said "Connecting...", and the tooltip
            // says what happened and what to do (devthrottle_internal#2311, live proof F3).
            var refusal = inputs.Refusal!;
            return new GatewayStatusBoxContent(visual, connected, signedIn, refusal.Summary, refusal.ChipText);
        }
        var tooltip = Tooltip(resolved.State, inputs.FailedLeg, gatewayHost, accountEmail);
        var chipText = ChipText(resolved.State);
        return new GatewayStatusBoxContent(visual, connected, signedIn, tooltip, chipText);
    }

    /// <summary>
    /// The compact chip's verdict, one short phrase per resolver state. Folded here - not in the
    /// surface - so the slim chip can never invent a wording of its own: green earns "Connected",
    /// connected-but-signed-out nudges "Sign in", and the failure states name the failure.
    /// </summary>
    public static string ChipText(GatewayConnectionState state) => state switch
    {
        GatewayConnectionState.NotConfigured => "No Gateway",
        GatewayConnectionState.Connecting => "Connecting...",
        GatewayConnectionState.ConnectFailed => "Connection failed",
        GatewayConnectionState.WasConnectedNowUnreachable => "Unreachable",
        GatewayConnectionState.ConnectedNotSignedIn => "Sign in",
        GatewayConnectionState.AllGreen => "Connected",
        GatewayConnectionState.KeyRefused => throw new InvalidOperationException(
            "A refused key is worded by its refusal; use Describe, which has it."),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown gateway connection state"),
    };

    /// <summary>Collapse the six resolver states to the box's four visual states (spec section 6 table).</summary>
    public static GatewayStatusBoxVisual VisualFor(GatewayConnectionState state) => state switch
    {
        GatewayConnectionState.Connecting => GatewayStatusBoxVisual.Yellow,
        GatewayConnectionState.AllGreen => GatewayStatusBoxVisual.Green,
        GatewayConnectionState.ConnectFailed => GatewayStatusBoxVisual.Red,
        GatewayConnectionState.WasConnectedNowUnreachable => GatewayStatusBoxVisual.Red,
        GatewayConnectionState.KeyRefused => GatewayStatusBoxVisual.Red,
        // NotConfigured and ConnectedNotSignedIn are both the amber needs-attention look.
        _ => GatewayStatusBoxVisual.Amber,
    };

    // The Connected line names the configured gateway first, then states the resolver's finished verdict in
    // parentheses. The marker remains resolver-owned reinforcement, not the only way to learn the verdict.
    // No configured gateway means no line. A configured gateway without a displayable host is an invalid
    // presenter input and fails explicitly instead of silently painting the box as if it were brand new.
    private static GatewayStatusLine ConnectedLine(
        GatewayConnectionResolved resolved, bool gatewayConfigured, string? gatewayHost, GatewayKeyRefusal? refusal)
    {
        if (!gatewayConfigured)
            return new GatewayStatusLine(resolved.ConnectedCheck, string.Empty);

        if (string.IsNullOrWhiteSpace(gatewayHost))
            throw new InvalidOperationException(
                $"Gateway host is required when the resolved state is {resolved.State} and a gateway is configured");

        var stateText = resolved.State switch
        {
            GatewayConnectionState.Connecting => "Connecting...",
            GatewayConnectionState.ConnectFailed => "Connection failed",
            GatewayConnectionState.WasConnectedNowUnreachable => "Unreachable",
            GatewayConnectionState.ConnectedNotSignedIn => "Connected",
            GatewayConnectionState.AllGreen => "Connected",
            GatewayConnectionState.KeyRefused => refusal!.ChipText,
            GatewayConnectionState.NotConfigured => throw new InvalidOperationException(
                "A configured gateway cannot resolve to NotConfigured"),
            _ => throw new ArgumentOutOfRangeException(nameof(resolved), resolved.State, "Unknown gateway connection state"),
        };
        return new GatewayStatusLine(resolved.ConnectedCheck, $"{gatewayHost.Trim()} ({stateText})");
    }

    // The Signed-in line shows the identity when green, the next action when pending, and a muted
    // "Checking account..." while the read is in flight or cannot be told yet (never a false sign-out).
    private static GatewayStatusLine SignedInLine(GatewayCheckState check, string? accountEmail) => check switch
    {
        GatewayCheckState.Passed => new GatewayStatusLine(check, SignedInText(accountEmail)),
        GatewayCheckState.Working => new GatewayStatusLine(check, "Checking account..."),
        // Unknown = cannot tell yet (an unreachable/unread account); muted, still points at signing in.
        GatewayCheckState.Unknown => new GatewayStatusLine(check, "Sign in"),
        // Pending (reachable and signed out) is the actionable nudge.
        _ => new GatewayStatusLine(check, "Sign in"),
    };

    private static string SignedInText(string? accountEmail) =>
        string.IsNullOrWhiteSpace(accountEmail) ? "Signed in" : $"Signed in: {accountEmail}";

    private static string Tooltip(
        GatewayConnectionState state,
        GatewayConnectionFailedLeg failedLeg,
        string? gatewayHost,
        string? accountEmail)
    {
        var host = string.IsNullOrWhiteSpace(gatewayHost) ? "the Gateway" : $"Gateway on {gatewayHost}";
        var failure = FailedLegText(failedLeg);
        return state switch
        {
            GatewayConnectionState.NotConfigured =>
                "No Gateway is connected. Click to find and connect to your Gateway.",
            GatewayConnectionState.Connecting =>
                $"Connecting to {host}. Click to see progress.",
            GatewayConnectionState.ConnectFailed =>
                $"Could not connect to {host}. {failure} Click to see the fix.",
            GatewayConnectionState.ConnectedNotSignedIn =>
                $"Connected to {host}, but this device is not signed in. Click to sign in with DevThrottle.",
            GatewayConnectionState.AllGreen =>
                string.IsNullOrWhiteSpace(accountEmail)
                    ? $"Connected to {host} and signed in. Click for details."
                    : $"Connected to {host} and signed in as {accountEmail}. Click for details.",
            GatewayConnectionState.WasConnectedNowUnreachable =>
                $"Was connected to {host}, now unreachable. {failure} Click to reconnect.",
            _ => "Click to open the Gateway connection panel.",
        };
    }

    private static string FailedLegText(GatewayConnectionFailedLeg leg) => leg switch
    {
        GatewayConnectionFailedLeg.Callback => "The Gateway cannot reach this Director back.",
        GatewayConnectionFailedLeg.OutboundReach => "This Director cannot reach the Gateway.",
        _ => "The connection failed.",
    };
}
