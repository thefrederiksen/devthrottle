namespace CcDirector.ControlApi;

/// <summary>
/// The ONE seam through which the desktop takes the wheel of an API-mode session or hands it back (mission #4070,
/// phase 4). The desktop goes THROUGH the Gateway's verbs, <c>POST /sessions/{sid}/wheel/take</c> and
/// <c>POST /sessions/{sid}/wheel/hand-back</c>, exactly as the Cockpit and the phone do - never
/// <c>Session.TakeWheelAsync</c> in-process - so the Gateway's ruling, its refusal sentences, and the Director's
/// contract to report the new holder before it answers all run the same way for every surface.
///
/// Fail-loud (no fallback): each call THROWS when the Gateway is not configured or the verb is refused, and the
/// exception's message is the Gateway's own sentence when it sent one, for the desktop to show verbatim.
/// </summary>
public interface IGatewayWheel
{
    /// <summary>Take the wheel of the session. Returns once the Gateway accepted it; throws on any refusal.</summary>
    Task TakeWheelAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Hand the wheel of the session back. Returns once the Gateway accepted it; throws on any refusal.</summary>
    Task HandBackWheelAsync(string sessionId, CancellationToken ct = default);
}
