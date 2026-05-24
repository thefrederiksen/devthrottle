namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /chat on the Director.
///
/// The Director chat is intentionally simple in v1: one configured session
/// (Chat.SessionRepoPath in appsettings) plays the role of "the agent." Every
/// chat message becomes a SendTextAsync call against that session; the agent
/// reply is read back from the session's terminal buffer once the session
/// returns to Idle / WaitingForInput.
/// </summary>
public sealed class ChatRequest
{
    /// <summary>The user message. Plain text. Required.</summary>
    public string Text { get; set; } = "";

    /// <summary>How long, in milliseconds, the Director will wait for the
    /// agent to finish its turn before returning a timeout. Default 120 s.</summary>
    public int TimeoutMs { get; set; } = 120_000;

    /// <summary>Optional override for which session to talk to. When set, must
    /// be the GUID of a session on this Director. When unset (the common case)
    /// the Director uses Chat.SessionRepoPath to pick one.</summary>
    public string? SessionId { get; set; }
}
