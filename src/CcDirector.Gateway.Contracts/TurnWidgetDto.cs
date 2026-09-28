namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One card / "widget" in the structured Agent view, transport-friendly DTO.
/// Built by CcDirector.Core.Claude.WidgetBuilder, which is the ONLY widget builder.
/// It used to have a desktop twin (CleanWidgetViewModel, built for the alpha
/// Wingman panel); that panel was removed in commit 9e711eb6 and the twin was deleted with
/// the rest of its cluster on 14 July 2026, so there is nothing to mirror or keep in step.
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

    /// <summary>When the message this widget came from was recorded, if its source carries it. Set by the Gateway's
    /// stored-conversation adapter, where it is part of what names ONE occurrence of a reply: two stops can end with
    /// the same words, never at the same place in the conversation at the same moment.</summary>
    public DateTimeOffset? Timestamp { get; set; }
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

    /// <summary>
    /// Status string: "ok" | "unsupported" | "no_session_id" | "no_jsonl" | "no_transcript" |
    /// "empty_history" | "parse_error".
    ///
    /// ONLY "ok" means the conversation was actually read. Every other value is a FAILED read that still
    /// arrives as a SUCCESSFUL command result (the transport worked; the read did not), carrying an empty
    /// <see cref="Widgets"/> list. So a caller that looks only at the widgets cannot tell a failed read from
    /// a session that genuinely has nothing to say - which is how voice narration went permanently silent on
    /// unreadable sessions, recording "nothing to narrate" and never retrying (issue #2561). Check this
    /// before drawing ANY conclusion from an empty widget list.
    /// </summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error message if Status != "ok".</summary>
    public string? Error { get; set; }
}
