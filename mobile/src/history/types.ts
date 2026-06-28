// The parsed, agent-agnostic conversation history returned by GET /sessions/{sid}/history and
// proxied through the Gateway's catch-all /sessions/{sid}/{**rest} leg with the injected Bearer.
// These TypeScript shapes mirror the C# SessionHistoryDto / HistoryMessageDto / HistoryPartDto
// (src/CcDirector.Gateway.Contracts/SessionHistoryDto.cs). The history endpoint is NOT declared in
// the Gateway's OpenAPI document (it rides the generic per-session proxy), so - exactly like the
// buffer/escape/interrupt responses in api/client.ts - it is read with a narrow local shape rather
// than from the generated schema. Keep these fields in step with the C# DTO.

/** One content part of a normalized message. Kind = "Text" | "Thinking" | "ToolUse" | "ToolResult". */
export interface HistoryPartDto {
  kind: string;
  /** Message text, thinking text, the tool input as raw JSON (ToolUse), or the result text. */
  text: string;
  /** For a tool call, the tool's name; otherwise null/absent. */
  toolName?: string | null;
  /** For a tool call, its id; for a tool result, the id of the call it answers. */
  toolId?: string | null;
}

/** One normalized message: a role ("User" | "Assistant") plus its ordered content parts. */
export interface HistoryMessageDto {
  role: string;
  parts: HistoryPartDto[];
  timestamp?: string | null;
}

/** The parsed conversation history for one session. */
export interface SessionHistoryDto {
  sessionId: string;
  directorId: string;
  /** Agent CLI kind (ClaudeCode / Codex / Pi / Grok / Copilot / OpenCode / Gemini). */
  agent: string;
  /** True when a history provider exists for this session's agent. */
  isSupported: boolean;
  /** True for raw terminal scrollback (Gemini): render verbatim, not as Markdown. */
  isRawText: boolean;
  /** Transcript-derived history state (Idle / Working / NeedsYou / BackgroundRunning), or null. */
  historyState?: string | null;
  /** The conversation messages, in chronological order. */
  messages: HistoryMessageDto[];
  /** "ok" | "unsupported". */
  status: string;
  /** Free-text error message if status != "ok". */
  error?: string | null;
}
