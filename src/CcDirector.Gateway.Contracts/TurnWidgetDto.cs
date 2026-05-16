namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One card / "widget" in the structured Agent view, transport-friendly DTO.
/// Mirrors the CleanWidgetViewModel in the Avalonia UI but with no UI types.
/// Source: parsed from claude.exe's JSONL session log via StreamMessageParser.
/// </summary>
public sealed class TurnWidgetDto
{
    /// <summary>
    /// One of: Text, Thinking, Bash, Read, Write, Edit, Grep, Glob, TodoWrite,
    /// Agent, Skill, UserMessage, GenericTool.
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>Header text shown at the top of the card (e.g. "Claude", "Edit File").</summary>
    public string Header { get; set; } = "";

    /// <summary>Optional sub-header (e.g. file path, command description).</summary>
    public string? Subheader { get; set; }

    /// <summary>Primary body (command text, message text, search pattern, etc.).</summary>
    public string Content { get; set; } = "";

    /// <summary>Tool result output paired with this widget (empty for non-tool widgets).</summary>
    public string Result { get; set; } = "";

    /// <summary>True if the tool result was reported as an error.</summary>
    public bool IsError { get; set; }

    /// <summary>True while the tool call has not yet been answered (no result block matched).</summary>
    public bool IsPending { get; set; }

    /// <summary>The Anthropic tool_use_id (for pairing with results in clients).</summary>
    public string ToolUseId { get; set; } = "";
}

/// <summary>
/// GET /sessions/{sid}/turns response.
/// </summary>
public sealed class TurnsResponse
{
    public string SessionId { get; set; } = "";

    /// <summary>Claude's session id (the GUID claude.exe owns); null if not yet linked.</summary>
    public string? ClaudeSessionId { get; set; }

    /// <summary>Resolved path to the JSONL log we read from (informational).</summary>
    public string? JsonlPath { get; set; }

    /// <summary>List of widgets in chronological order.</summary>
    public List<TurnWidgetDto> Widgets { get; set; } = new();

    /// <summary>How many JSONL lines were parsed.</summary>
    public int LineCount { get; set; }

    /// <summary>Status string: "ok" | "no_session_id" | "no_jsonl" | "parse_error".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error message if Status != "ok".</summary>
    public string? Error { get; set; }
}
