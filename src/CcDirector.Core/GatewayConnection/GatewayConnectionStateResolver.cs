namespace CcDirector.Core.GatewayConnection;

/// <summary>
/// The connection-verification outcome, abstracted into a plain input for the resolver (spec section 4).
/// The panel maps the raw <c>GatewayConnectionMonitor.Status</c> (in CcDirector.ControlApi) onto these
/// values, so the resolver stays UI-free and I/O-free and can live in Core with no ControlApi dependency.
/// </summary>
public enum GatewayConnectionVerification
{
    /// <summary>No handshake result known yet (initial, or just after a reset). Nothing proven either way.</summary>
    Unknown,

    /// <summary>A handshake is in flight; neither leg has a final verdict yet.</summary>
    Verifying,

    /// <summary>The two-way nonce handshake proved BOTH legs. This is the only value that earns Connected green.</summary>
    Connected,

    /// <summary>The last handshake failed; <see cref="GatewayConnectionInputs.FailedLeg"/> names which leg.</summary>
    Failed,

    /// <summary>The Gateway refused this Director's key with a 401 and the Director has stopped trying
    /// (devthrottle_internal#2311, live proof F3); <see cref="GatewayConnectionInputs.Refusal"/> says why.</summary>
    KeyRefused,
}

/// <summary>
/// When the handshake failed, which leg went down (spec section 4). Carried on the input snapshot so the
/// panel's Step 1 repair view can name the failing leg and its fix (decision 11, no fallback). The
/// six-state resolution itself does not branch on the leg - it is presentation detail for the surface.
/// </summary>
public enum GatewayConnectionFailedLeg
{
    /// <summary>Not in a failed state, or the leg is not identified.</summary>
    None,

    /// <summary>The outbound leg: this Director could not reach the Gateway at all.</summary>
    OutboundReach,

    /// <summary>The callback leg: the Gateway could not reach this Director back on its advertised address.</summary>
    Callback,
}

/// <summary>
/// The Gateway's signed-in report, abstracted into a plain input for the resolver (spec section 4). The
/// panel maps a <see cref="Account.GatewayAccountStatus"/> onto these values. The load-bearing distinction
/// is <see cref="Unavailable"/> versus <see cref="SignedOut"/>: an unreachable Gateway tells us nothing
/// about the credential and must never read as a false sign-out (decision 3, and the AccountIndicator's
/// "never a false signed out" rule).
/// </summary>
public enum GatewayAccountSignInState
{
    /// <summary>Account status has not been read yet. Muted - "cannot tell yet", never an alarm.</summary>
    Unknown,

    /// <summary>The Gateway is configured but could not be reached to read status. Muted - never a false sign-out.</summary>
    Unavailable,

    /// <summary>The Gateway answered and reports no signed-in credential. The actionable "sign in" nudge.</summary>
    SignedOut,

    /// <summary>The Gateway answered and reports a signed-in credential.</summary>
    SignedIn,
}

/// <summary>
/// The full snapshot the resolver reduces to a single state (spec section 4). A value type with plain
/// fields so tests construct it directly and the resolver has nothing to null-check.
/// </summary>
/// <param name="GatewayConfigured">Whether a Gateway address is set in config.json at all.</param>
/// <param name="Connection">The abstracted handshake outcome from GatewayConnectionMonitor.</param>
/// <param name="FailedLeg">When <paramref name="Connection"/> is Failed, which leg went down (for the panel's message).</param>
/// <param name="WasEverConnected">Whether the handshake has ever succeeded in this run (distinguishes
/// "never set up" from "was working, now unreachable").</param>
/// <param name="DeviceKeyPresent">Whether a per-device key is stored for this Director.</param>
/// <param name="Account">The abstracted signed-in report from GatewayAccountStatusClient.</param>
/// <param name="Refusal">When <paramref name="Connection"/> is KeyRefused, why the Gateway refused the key; null
/// otherwise.</param>
public readonly record struct GatewayConnectionInputs(
    bool GatewayConfigured,
    GatewayConnectionVerification Connection,
    GatewayConnectionFailedLeg FailedLeg,
    bool WasEverConnected,
    bool DeviceKeyPresent,
    GatewayAccountSignInState Account,
    GatewayKeyRefusal? Refusal = null);

/// <summary>
/// The resolved overall state (spec section 4). Drives the status box color and which step the panel
/// opens on. Colors and labels live with the Avalonia surface, not here.
/// </summary>
public enum GatewayConnectionState
{
    /// <summary>No Gateway address, never connected. Amber. Panel opens on Step 1 (connect).</summary>
    NotConfigured,

    /// <summary>Address set, handshake verifying. Yellow. Panel opens on Step 1 (progress).</summary>
    Connecting,

    /// <summary>Handshake failed and this run never connected. Red. Panel opens on Step 1 (repair).</summary>
    ConnectFailed,

    /// <summary>Handshake proven, but device not paired or account signed out. Amber. Panel opens on Step 2 (sign in).</summary>
    ConnectedNotSignedIn,

    /// <summary>Handshake proven AND the Gateway reports signed in. Green. Panel opens on the Done view.</summary>
    AllGreen,

    /// <summary>Was green this run, now the handshake is failing. Red. Panel opens on Step 1 (repair).</summary>
    WasConnectedNowUnreachable,

    /// <summary>The Gateway refused this Director's key and the Director stopped trying - removed from its team, or
    /// its key revoked. Red, worded by the refusal itself, never "Connecting...". Panel opens on Step 1, where the
    /// Director is set up again.</summary>
    KeyRefused,
}

/// <summary>Which step of <c>GatewayConnectionPanel</c> a given state routes to (spec section 4, last column).</summary>
public enum GatewayPanelStep
{
    /// <summary>Step 1 - connect (or its progress / repair variants).</summary>
    Connect,

    /// <summary>Step 2 - sign in.</summary>
    SignIn,

    /// <summary>The Done view - both checks green.</summary>
    Done,
}

/// <summary>
/// The paint state of one of the two check lines (Connected, Signed in) shown in the panel and the status
/// box (spec sections 5, 6). The surface maps these to markers and colors; this stays UI-free.
/// </summary>
public enum GatewayCheckState
{
    /// <summary>Not done yet - a hollow marker plus the next action. The amber first-run line.</summary>
    Pending,

    /// <summary>In progress - the handshake is verifying, or account status is being read.</summary>
    Working,

    /// <summary>Proven - a filled marker. Green.</summary>
    Passed,

    /// <summary>The connection failed. Red; the named detail appears in the tooltip and repair panel.</summary>
    Failed,

    /// <summary>Cannot tell yet - muted, explicitly NOT an alarm (an Unavailable Gateway, decision 3).</summary>
    Unknown,
}

/// <summary>
/// The complete resolved view the panel and the status box render from (spec section 4). One overall state
/// (color + routing), the step the panel opens on, and the paint state of each of the two check lines.
/// </summary>
/// <param name="State">The overall six-state result.</param>
/// <param name="TargetStep">Which panel step this state opens on.</param>
/// <param name="ConnectedCheck">The paint state of the "Connected" line.</param>
/// <param name="SignedInCheck">The paint state of the "Signed in" line.</param>
public sealed record GatewayConnectionResolved(
    GatewayConnectionState State,
    GatewayPanelStep TargetStep,
    GatewayCheckState ConnectedCheck,
    GatewayCheckState SignedInCheck);

/// <summary>
/// Reduces the two Gateway verification sources into ONE state (spec section 4). This is the single, pure,
/// unit-tested decision point the whole Gateway Connection redesign rests on: plain inputs in, resolved
/// state out - no UI, no I/O (CodingStyle Section 8: Core has no UI dependencies), following the same
/// pattern as <see cref="Account.AccountIndicatorPresenter"/>.
///
/// The load-bearing rules it enforces (spec section 4):
///   - Connected green requires <see cref="GatewayConnectionVerification.Connected"/> - a proven two-way
///     handshake. A heartbeat or a cached value never paints green (decision 4).
///   - Signed-in green requires BOTH a device key AND the Gateway reporting signed in (decision 3).
///   - An Unavailable account while Connected reads as "cannot tell yet" (muted), never a false sign-out.
///   - WasConnectedNowUnreachable outranks ConnectFailed only when the handshake succeeded earlier this run.
/// </summary>
public static class GatewayConnectionStateResolver
{
    /// <summary>
    /// Resolve the full snapshot to its overall state, the panel step it opens on, and the paint state of
    /// each of the two check lines.
    /// </summary>
    public static GatewayConnectionResolved Resolve(GatewayConnectionInputs inputs)
    {
        var state = ResolveState(inputs);
        return new GatewayConnectionResolved(
            state,
            StepFor(state),
            ResolveConnectedCheck(inputs),
            ResolveSignedInCheck(inputs));
    }

    /// <summary>The overall six-state result (spec section 4). Public so the status box can read it directly.</summary>
    public static GatewayConnectionState ResolveState(GatewayConnectionInputs inputs)
    {
        // A refused key outranks the rest: the Director has stopped trying, so nothing else here can change until it
        // is set up again, and it must never read as "Connecting...".
        if (inputs.Connection == GatewayConnectionVerification.KeyRefused)
        {
            if (inputs.Refusal is null)
                throw new InvalidOperationException("A refused key was reported with no refusal to say why.");
            return GatewayConnectionState.KeyRefused;
        }

        // Connected outranks everything: the handshake is proven right now. Whether that is fully green or
        // still needs sign-in depends on the device key and the Gateway's account report.
        if (inputs.Connection == GatewayConnectionVerification.Connected)
        {
            return inputs.DeviceKeyPresent && inputs.Account == GatewayAccountSignInState.SignedIn
                ? GatewayConnectionState.AllGreen
                : GatewayConnectionState.ConnectedNotSignedIn;
        }

        // A handshake in flight is Connecting regardless of history (a re-verify after a Gateway move shows
        // yellow briefly, not red).
        if (inputs.Connection == GatewayConnectionVerification.Verifying)
            return GatewayConnectionState.Connecting;

        // A failed handshake is a red state. It reads as "was working, now unreachable" only when the
        // handshake actually succeeded earlier this run (a mid-session Gateway move); otherwise it is a
        // first-time connect failure.
        if (inputs.Connection == GatewayConnectionVerification.Failed)
        {
            return inputs.WasEverConnected
                ? GatewayConnectionState.WasConnectedNowUnreachable
                : GatewayConnectionState.ConnectFailed;
        }

        // Connection Unknown: nothing proven and nothing failing. With an address set we are pending a
        // handshake (Connecting/yellow); with no address this is a legitimate local-only Director
        // (NotConfigured/amber, never an error - decision 2).
        return inputs.GatewayConfigured
            ? GatewayConnectionState.Connecting
            : GatewayConnectionState.NotConfigured;
    }

    /// <summary>Which panel step a resolved state opens on (spec section 4, last column). Pure mapping.</summary>
    public static GatewayPanelStep StepFor(GatewayConnectionState state) => state switch
    {
        GatewayConnectionState.ConnectedNotSignedIn => GatewayPanelStep.SignIn,
        GatewayConnectionState.AllGreen => GatewayPanelStep.Done,
        // NotConfigured, Connecting, ConnectFailed, WasConnectedNowUnreachable all live on Step 1.
        _ => GatewayPanelStep.Connect,
    };

    // The "Connected" line follows the handshake outcome directly - green is earned only by a proven
    // handshake (decision 4).
    private static GatewayCheckState ResolveConnectedCheck(GatewayConnectionInputs inputs) => inputs.Connection switch
    {
        GatewayConnectionVerification.Connected => GatewayCheckState.Passed,
        GatewayConnectionVerification.Verifying => GatewayCheckState.Working,
        GatewayConnectionVerification.Failed => GatewayCheckState.Failed,
        GatewayConnectionVerification.KeyRefused => GatewayCheckState.Failed,
        _ => GatewayCheckState.Pending,
    };

    // The "Signed in" line needs BOTH a device key and the Gateway reporting signed in to go green
    // (decision 3). An Unavailable/Unknown account is "cannot tell yet" (muted), never a false sign-out.
    private static GatewayCheckState ResolveSignedInCheck(GatewayConnectionInputs inputs)
    {
        if (inputs.DeviceKeyPresent && inputs.Account == GatewayAccountSignInState.SignedIn)
            return GatewayCheckState.Passed;

        return inputs.Account switch
        {
            // Reachable and signed out (or signed in but this device not yet paired): the actionable nudge.
            GatewayAccountSignInState.SignedOut => GatewayCheckState.Pending,
            GatewayAccountSignInState.SignedIn => GatewayCheckState.Pending,
            // Unreachable or not-yet-read: muted, never an alarm.
            _ => GatewayCheckState.Unknown,
        };
    }
}
