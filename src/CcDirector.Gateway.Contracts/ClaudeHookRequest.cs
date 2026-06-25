namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /sessions/{id}/claude-hook. A Claude Code SessionStart hook posts the
/// current session id and transcript path so the Director keeps tracking the right
/// transcript across /clear and auto-compaction. Field names match the values the hook
/// script extracts from Claude's hook event JSON.
/// </summary>
/// <param name="ClaudeSessionId">Claude's current session_id.</param>
/// <param name="TranscriptPath">Absolute path to the current transcript .jsonl.</param>
/// <param name="HookEvent">The hook_event_name (e.g. "SessionStart").</param>
/// <param name="Source">The SessionStart source: startup, resume, clear, or compact.</param>
public sealed record ClaudeHookRequest(
    string? ClaudeSessionId,
    string? TranscriptPath,
    string? HookEvent,
    string? Source);
