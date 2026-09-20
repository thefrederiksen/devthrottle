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
  /**
   * WHO AUTHORED a user message: "owner", "agent", or "unknown". Null/absent on an assistant message,
   * and absent entirely from a Director too old to stamp it.
   *
   * The agent's transcript cannot answer this - in it, a line the owner typed and a line the fleet
   * doorbell typed are the same shape - so the Director stamps it from what it saw at its own submit
   * choke point. UNKNOWN IS NOT "AGENT": anything that hides turns on this field treats unknown as the
   * owner's and shows it, because hiding a prompt he really did type is a failure he cannot detect.
   */
  origin?: string | null;
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
  /**
   * The finished sentence to show ABOVE a conversation that is frozen - its computer is offline, or too old
   * to send new turns. Null while the session is live. Unlike emptyText it rides alongside the content: a
   * conversation that stopped an hour ago otherwise looks exactly like a live one.
   */
  staleNotice?: string | null;
  /**
   * The finished sentence to show when nothing renders, or null/absent when there is content. Written by
   * the GATEWAY (SessionConversationFold), because "no messages" has several causes - a session that has
   * not spoken, an agent that keeps no history, a computer that has not sent its conversation, one too old
   * to send it, one that is offline - and only one of them means "wait, it is coming". Rendered verbatim.
   */
  emptyText?: string | null;
  /**
   * "ok" | "unsupported" | "unknown-session" | "director-offline" | "director-too-old" | "not-pushed-yet".
   * A machine-readable companion to emptyText, for logs and diagnostics - the screen renders emptyText.
   */
  status: string;
  /**
   * The same sentence as emptyText when the empty screen is a FAULT - the computer is offline, too old to
   * send conversations, or the session is unknown here. Null for the two states that are not faults: a
   * session that has simply not spoken yet, and an agent that keeps no history at all.
   */
  error?: string | null;
}
