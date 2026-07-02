namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /sessions on a Director's Control API.
/// </summary>
public sealed class NewSessionRequest
{
    /// <summary>Absolute path to the repository / working directory the session should open in.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>
    /// Optional explicit display name for the new session (issue #800). When provided it is used
    /// verbatim, EXCEPT that a blank name or one equal (case-insensitive) to the bare repository
    /// folder name is rejected with HTTP 400 - pass a meaningful name or a <see cref="Purpose"/>.
    /// When omitted, the Director auto-composes a meaningful name from the folder and a
    /// disambiguator so a session never displays as the bare folder name alone.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Optional short free-text description of what the session is FOR (issue #800), e.g.
    /// "implement #799". When no explicit <see cref="Name"/> is given, the Director builds the
    /// session name from the folder name plus this purpose (e.g. "devthrottle: implement #799").
    /// Trimmed and capped when building the name; not stored as a separate field on the session.
    /// </summary>
    public string? Purpose { get; set; }

    /// <summary>
    /// Which agent CLI to launch. Valid values: "ClaudeCode" (default), "Pi", "Codex",
    /// "Gemini", "OpenCode", "Grok", "RawCli". When "RawCli" is specified, <see cref="Command"/>
    /// must also be set to the executable to run.
    /// </summary>
    public string Agent { get; set; } = "ClaudeCode";

    /// <summary>Optional extra arguments to pass to the agent CLI.</summary>
    public string? Args { get; set; }

    /// <summary>
    /// For <see cref="Agent"/> = "RawCli": the executable to run (e.g. "pwsh", "aider",
    /// or an absolute path). Resolved against PATH+PATHEXT before spawning; a path that
    /// cannot be resolved fails loudly at launch. Ignored for all other agent kinds.
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// For <see cref="Agent"/> = "RawCli": optional arguments appended to
    /// <see cref="Command"/> before any <see cref="Args"/>. Ignored for all other
    /// agent kinds.
    /// </summary>
    public string? CommandArgs { get; set; }

    /// <summary>
    /// Optional Claude session ID to resume. When set, the new session re-attaches to the
    /// given Claude Code conversation instead of starting fresh. Used by the Resume Session
    /// tab. Ignored by agents that don't support resume (e.g. Pi).
    /// </summary>
    public string? ResumeSessionId { get; set; }

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
    /// Defaults to false, matching <c>Session.WingmanEnabled</c>'s default; set to true to
    /// create a session with the Wingman experience on.
    /// </summary>
    public bool WingmanEnabled { get; set; } = false;

    /// <summary>
    /// Optional id of the session that is spawning and controlling this one (issue #815). When set,
    /// the new session is born as a controlled "Supporting" sub-agent of that session and shows the
    /// recessive Supporting status color while its controller is alive (a red "needs you" still
    /// breaks through). Set ONLY at birth - there is no way to mark/unmark control later. Must be a
    /// session id (GUID string); an unparseable value is ignored and the session is born normal.
    /// </summary>
    public string? ControllerSessionId { get; set; }
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

/// <summary>Body of POST /repos: register a repository explicitly (no session needed).</summary>
public sealed class RepoAddRequest
{
    /// <summary>Required. Absolute path of the repository directory. Must exist on the Director.</summary>
    public string Path { get; set; } = "";

    /// <summary>Optional display name; defaults to the folder name.</summary>
    public string? Name { get; set; }
}

/// <summary>Body of PATCH /repos: rename a registered repository (path is the identity).</summary>
public sealed class RepoRenameRequest
{
    /// <summary>Required. Path of the registered repository to rename.</summary>
    public string Path { get; set; } = "";

    /// <summary>Required. New display name.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// One repository with everything the repositories page needs, aggregated by the Director
/// from the repo registry, live sessions, session history, Claude Code session metadata,
/// and handover documents (returned by GET /repos/overview).
/// </summary>
public sealed class RepoOverviewDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>When a session was last started in this repo (UTC, from the registry).</summary>
    public DateTime? LastUsed { get; set; }

    /// <summary>False when the registered directory no longer exists on disk.</summary>
    public bool PathExists { get; set; }

    /// <summary>Live (non-exited) Director sessions currently open in this repo.</summary>
    public int LiveSessionCount { get; set; }

    /// <summary>Display names of the live sessions (custom name or folder name).</summary>
    public List<string> LiveSessionNames { get; set; } = new();

    /// <summary>Resumable Claude Code sessions recorded for this repo (~/.claude/projects).</summary>
    public int ResumableSessionCount { get; set; }

    /// <summary>CC Director workspace-history entries for this repo.</summary>
    public int HistorySessionCount { get; set; }

    /// <summary>When the most recent session (history or Claude metadata) was active (UTC).</summary>
    public DateTime? LastSessionAtUtc { get; set; }

    /// <summary>One-line summary of the most recent session, when available.</summary>
    public string? LastSessionSummary { get; set; }

    /// <summary>Git branch recorded by the most recent Claude session in this repo.</summary>
    public string? GitBranch { get; set; }

    /// <summary>Handover documents referencing this repo.</summary>
    public int HandoverCount { get; set; }

    /// <summary>Date of the newest handover referencing this repo (UTC).</summary>
    public DateTime? LastHandoverUtc { get; set; }
}
