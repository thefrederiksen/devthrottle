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

    /// <summary>
    /// When true, this is a status poll, NOT a new message: the Director does
    /// NOT send <see cref="Text"/> to the session. It reads the session's
    /// current activity state and latest assistant reply and returns
    /// immediately (no blocking wait). Used by the voice client to follow a
    /// long-running turn without holding one HTTP request open for the whole
    /// turn. <see cref="Text"/> is ignored and may be empty in this mode.
    /// </summary>
    public bool PollOnly { get; set; }

    /// <summary>
    /// When true the reply is going to be read aloud (phone voice mode), so the
    /// Director produces an ear-friendly spoken version in
    /// <see cref="ChatResponse.Summary"/>: plain concepts, no code, no file paths,
    /// no symbols. When false (typed chat) the summary step is skipped and
    /// <see cref="ChatResponse.Summary"/> stays empty.
    /// </summary>
    public bool Voice { get; set; }

    /// <summary>
    /// When true on a poll request (<see cref="PollOnly"/>) AND the turn is still
    /// in progress, the Director reads the session's recent terminal activity and
    /// returns a short ear-friendly note of what the agent is doing right now in
    /// <see cref="ChatResponse.ProgressNote"/>. The voice client sets this only
    /// occasionally (about every two minutes), NOT on every cheap status poll,
    /// because generating the note costs a Haiku call. Ignored unless
    /// <see cref="PollOnly"/> is also true.
    /// </summary>
    public bool WantProgress { get; set; }
}
