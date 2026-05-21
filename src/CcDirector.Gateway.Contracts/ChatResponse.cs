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

    /// <summary>Short ear-friendly summary intended for TTS. Empty when the
    /// summary step has not been performed (Phase 1).</summary>
    public string Summary { get; set; } = "";

    /// <summary>Activity state of the session at the end of the round-trip.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>Total milliseconds the round-trip took.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>"ok" | "no_session_configured" | "session_not_found" |
    /// "session_busy" | "timeout" | "send_failed".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error detail when Status != "ok".</summary>
    public string? Error { get; set; }
}
