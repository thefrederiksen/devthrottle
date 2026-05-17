namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Structured snapshot of a session's recent activity, synthesised from its JSONL log.
/// Returned by GET /sessions/{sid}/summary. Designed to be the data source for an
/// automated handover -- everything needed to brief a fresh session on what the
/// previous session was doing, without sending the whole transcript.
/// </summary>
public sealed class SessionSummaryDto
{
    public string SessionId { get; set; } = "";

    /// <summary>Director that owns the session. Empty in Director-local responses.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Agent CLI kind (ClaudeCode / Pi / Codex / Gemini).</summary>
    public string Agent { get; set; } = "";

    /// <summary>Repository / working directory.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Cognitive activity state at the time the summary was built.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>UTC timestamp the session was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Total number of turn-widgets we found in the JSONL log.</summary>
    public int TurnCount { get; set; }

    /// <summary>Most recent prompt the user typed into this session (truncated).</summary>
    public string? LastUserPrompt { get; set; }

    /// <summary>Most recent text reply Claude wrote (truncated).</summary>
    public string? LastAssistantText { get; set; }

    /// <summary>Files Claude has read / written / edited in this session.</summary>
    public List<FileTouch> FilesTouched { get; set; } = new();

    /// <summary>Most recent shell commands the session ran (Bash tool uses).</summary>
    public List<string> RecentCommands { get; set; } = new();

    /// <summary>Open TODO items pulled from the most recent TodoWrite tool use, if any.</summary>
    public List<TodoItem> OpenTodos { get; set; } = new();

    /// <summary>"ok" | "no_session_id" | "no_jsonl" | "parse_error".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error message if Status != "ok".</summary>
    public string? Error { get; set; }
}

public sealed class FileTouch
{
    public string Path { get; set; } = "";

    /// <summary>One of: Read, Write, Edit.</summary>
    public string Tool { get; set; } = "";
}

public sealed class TodoItem
{
    /// <summary>"pending" | "in_progress" | "completed".</summary>
    public string Status { get; set; } = "";

    public string Content { get; set; } = "";
}
