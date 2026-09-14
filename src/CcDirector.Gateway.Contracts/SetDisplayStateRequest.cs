namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The payload of the <c>set-display-state</c> command - the Gateway telling a Director the FOLDED display
/// state of one of its sessions, so the desktop rail renders exactly what the phone and the Cockpit render
/// instead of re-folding from local facts it cannot see (dictation, transcription, voice generation, the
/// snooze clock).
///
/// Like <see cref="SetResolvedRoleRequest"/> this is a FACT being delivered, not a request for the Director
/// to decide anything. The Director stores it verbatim on <c>Session</c> (via
/// <c>ApplyGatewayDisplayState</c>) and reports it back out through <c>ControlEndpoints.Map</c>; it never
/// computes, adjusts, or second-guesses the fold. The Gateway is the single fold, and the one screen that
/// cannot poll the Gateway for itself - the local rail - has to be told. See
/// docs/new_architecture/sessions.html.
/// </summary>
public sealed class SetDisplayStateRequest
{
    /// <summary>The folded effective color (<see cref="SessionOrdering.EffectiveColor"/>). Blank/whitespace
    /// clears the stamp back to "no answer", and the desktop shows its neutral waiting-for-gateway
    /// placeholder rather than guessing a colour.</summary>
    public string? EffectiveColor { get; set; }

    /// <summary>The folded human-readable label (<see cref="SessionOrdering.StateLabel"/>).</summary>
    public string? StateLabel { get; set; }

    /// <summary>The folded triage bucket: "needsYou" | "active" | "onHold".</summary>
    public string? TriageBucket { get; set; }

    /// <summary>The Gateway-owned instant the session entered red (<see cref="SessionDto.NeedsYouSince"/>),
    /// so the rail's "waiting Xm" matches every surface. Null when not red.</summary>
    public DateTime? NeedsYouSince { get; set; }

    /// <summary>The Gateway-owned armed-snooze deadline (<see cref="SessionDto.SnoozeUntil"/>), so the rail
    /// can show "Snoozed - wakes in Xh". Null when there is no running snooze clock.</summary>
    public DateTime? SnoozeUntil { get; set; }

    /// <summary>The Gateway's "just came back from an expired snooze" marker
    /// (<see cref="SessionDto.SnoozeExpired"/>), rendered as a distinct "Snooze ended" badge.</summary>
    public bool SnoozeExpired { get; set; }

    /// <summary>
    /// The Gateway-owned hold state (<see cref="SessionDto.HoldState"/>): "None" | "Held" | "DeferredHold".
    /// Carried on THIS reliable, change-gated, self-healing channel - not only on the one-shot hold mirror -
    /// so the desktop's raw <c>Session.OnHold</c> (which still drives the rail's Snooze-versus-Unsnooze menu)
    /// reconciles even if a fire-and-forget mirror is dropped or arrives out of order. The Director applies it
    /// via <c>ApplyGatewayHold</c>. Blank/unrecognised leaves the existing mirror untouched (the fold always
    /// stamps a real value, so blank only occurs for an older Gateway that does not send this field).
    /// </summary>
    public string? HoldState { get; set; }
}
