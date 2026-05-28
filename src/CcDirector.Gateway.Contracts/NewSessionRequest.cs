namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /sessions on a Director's Control API.
/// </summary>
public sealed class NewSessionRequest
{
    /// <summary>Absolute path to the repository / working directory the session should open in.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>
    /// Which agent CLI to launch. Valid values: "ClaudeCode" (default), "Pi", "Codex", "Gemini".
    /// </summary>
    public string Agent { get; set; } = "ClaudeCode";

    /// <summary>Optional extra arguments to pass to the agent CLI.</summary>
    public string? Args { get; set; }

    /// <summary>
    /// Optional first prompt to send into the new session as soon as the agent is up
    /// and Idle. Used by handovers and by phone clients that want to launch a session
    /// already loaded with context. The Director waits up to PrePromptWaitMs for the
    /// SessionStart hook before dispatching.
    /// </summary>
    public string? PrePrompt { get; set; }

    /// <summary>How long to wait for the new session to reach Idle before sending the
    /// PrePrompt (milliseconds). Default 30000.</summary>
    public int PrePromptWaitMs { get; set; } = 30_000;

    /// <summary>
    /// Whether the new session should boot with the Wingman experience on (auto-explain
    /// briefing on turn-end + Voice/Wingman tabs + Yellow "Wingman is reading" state).
    /// Defaults to true, matching <c>Session.WingmanEnabled</c>'s default; set to false to
    /// create a plain-terminal session with no auto-explain and no voice tabs.
    /// </summary>
    public bool WingmanEnabled { get; set; } = true;
}

/// <summary>
/// Describes a registered repository (returned by GET /repos).
/// </summary>
public sealed class RepositoryDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime? LastUsed { get; set; }
}
