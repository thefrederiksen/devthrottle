namespace CcDirector.Gateway.Contracts;

/// <summary>
/// POST /chat response.
///
/// Three text fields with different purposes:
/// - <see cref="Reply"/> is the raw text the agent produced (for the chat history
///   panel that shows everything).
/// - <see cref="DisplayText"/> is the cleaned / trimmed version intended for the
///   chat bubble (no ANSI codes, no extra blank lines). Usually equal to Reply
///   after light formatting; clients can render this directly.
/// - <see cref="Summary"/> is the SHORT ear-friendly version intended for TTS.
///   Empty in Phase 1 (the chat layer or a later phase fills it via a Haiku
///   side-call once that step lands).
/// </summary>
public sealed class ChatResponse
{
    /// <summary>The session the message was sent to.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Friendly session name (CustomName or repo folder).</summary>
    public string SessionName { get; set; } = "";

    /// <summary>The raw agent reply text (everything that arrived on the
    /// terminal buffer since the user prompt was sent).</summary>
    public string Reply { get; set; } = "";

    /// <summary>Cleaned / trimmed version of <see cref="Reply"/> intended for
    /// rendering in a chat bubble.</summary>
    public string DisplayText { get; set; } = "";

    /// <summary>Short ear-friendly summary of the FINAL reply intended for TTS.
    /// Filled only when the turn finished (Status "ok") and the request set
    /// Voice=true; empty otherwise.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Short ear-friendly note describing what the agent is doing RIGHT
    /// NOW, for a long-running turn. Filled only on a poll request that set
    /// WantProgress=true while the turn is still in progress (Status "working");
    /// empty otherwise. Distinct from <see cref="Summary"/>, which is the final
    /// answer. The voice client speaks this periodically so a driver hears that
    /// work is still happening, with a plain-language sense of what.</summary>
    public string ProgressNote { get; set; } = "";

    /// <summary>Activity state of the session at the end of the round-trip.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>Total milliseconds the round-trip took.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>"ok" | "working" | "no_session_configured" |
    /// "session_not_found" | "session_busy" | "timeout" | "send_failed".
    /// "working" is returned only for poll requests (ChatRequest.PollOnly):
    /// it means the agent's turn is still in progress, keep polling.</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error detail when Status != "ok".</summary>
    public string? Error { get; set; }
}
