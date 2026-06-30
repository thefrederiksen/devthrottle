namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The body of <c>POST /account/sign-in</c> (issue #853): the result of asking the Gateway to START the
/// DevThrottle browser loopback sign-in (the existing #637 flow). The sign-in itself runs in the
/// background on the Gateway (it opens the system browser and waits for the loopback hand-back, which can
/// take as long as the person takes in the browser), so this response reports only whether the flow was
/// kicked off - the Cockpit then polls <c>GET /account/status</c> to observe completion.
///
/// Security (carries DT-05): this contract intentionally carries NO access- or refresh-token field. The
/// captured credential never leaves the Gateway, so the Cockpit-facing response can never include it.
/// </summary>
public sealed class SignInStartResponseDto
{
    /// <summary>
    /// True when a sign-in flow is now in flight on the Gateway (either this request started it, or one
    /// was already running). The Cockpit shows "finish in your browser" and begins polling status.
    /// </summary>
    public bool Started { get; set; }

    /// <summary>
    /// True when the Gateway is already signed in, so no browser hand-off was started. The Cockpit
    /// re-reads status and shows the signed-in view.
    /// </summary>
    public bool AlreadySignedIn { get; set; }

    /// <summary>
    /// A user-safe reason the sign-in could not be started (for example: the host has no credential
    /// service so there is nothing to sign in to), or null when <see cref="Started"/> is true. Never
    /// carries an internal stack trace or any credential material.
    /// </summary>
    public string? Error { get; set; }
}
