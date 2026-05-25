namespace CcDirectorClient.Voice;

/// <summary>
/// Outcome of one /chat round-trip (or poll) projected from the server's
/// ChatResponse. Pure data + small decision helpers, no MAUI/Android dependency,
/// so the turn-following state machine is unit tested off-device.
/// </summary>
public sealed class ChatTurnResult
{
    /// <summary>Server status: ok | working | timeout | session_not_found | session_busy | send_failed | no_session_configured.</summary>
    public string Status { get; set; } = "";

    /// <summary>Short ear-friendly spoken version of the final reply (Summary). Empty until the turn finishes.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Cleaned reply text for on-screen display.</summary>
    public string DisplayText { get; set; } = "";

    /// <summary>Short note of what the agent is doing right now (only on a progress poll).</summary>
    public string ProgressNote { get; set; } = "";

    /// <summary>Session activity state at the end of the round-trip.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>Friendly session name echoed by the server.</summary>
    public string SessionName { get; set; } = "";

    /// <summary>Error detail when <see cref="Status"/> is not a success.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// True when the turn has reached a final state and the client should stop
    /// following it: the agent finished ("ok"), the session is gone, or the send
    /// itself failed. "working" and "timeout" are NOT terminal - the turn is
    /// still running and the client keeps polling.
    /// </summary>
    public bool IsTerminal => Status is "ok" or "session_not_found" or "send_failed"
        or "no_session_configured" or "session_busy";

    /// <summary>True while the turn is still in progress and the client should keep polling.</summary>
    public bool ShouldKeepPolling => Status is "working" or "timeout";

    /// <summary>
    /// True when a poll reports the session is still mid-turn. The client must not
    /// inject a new question while this is true: the prompt would interleave with
    /// the running turn and the reply read back would be that turn's output, not
    /// an answer. Used by the pre-send readiness gate.
    /// </summary>
    public bool IsWorking => string.Equals(Status, "working", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the session is gone and the round-trip cannot proceed.</summary>
    public bool IsGone => string.Equals(Status, "session_not_found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The text to read aloud for a finished turn: the ear-friendly Summary when
    /// the server produced one, otherwise the cleaned reply itself. This is a
    /// content preference, not an error fallback - the Summary is the preferred
    /// spoken form and the display text is the genuine reply when no summary was
    /// generated (e.g. the summarizer was unavailable), so the user still hears
    /// the real answer rather than silence.
    /// </summary>
    public string SpokenText()
    {
        if (!string.IsNullOrWhiteSpace(Summary)) return Summary.Trim();
        return (DisplayText ?? "").Trim();
    }
}
