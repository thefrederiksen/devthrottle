import type { HistoryMessageDto, SessionHistoryDto } from "./types";

// A 1:1 TypeScript port of src/CcDirector.Cockpit/Services/HistoryBubbleMapper.cs.
//
// Maps the agent-agnostic SessionHistoryDto into display bubbles, mirroring the desktop
// HistoryView.MapMessage exactly so the web and desktop History views read identically: an
// assistant turn flattens text / thinking / tool-use / tool-result into one bubble; a user message
// is either a real prompt ("You") or tool results fed back ("Tool result"). The same per-part
// length caps are applied. Pure (no React), so it is unit-testable directly.

/** One rendered bubble. */
export interface HistoryBubble {
  speaker: string;
  body: string;
  /** "user" | "assistant" | "tool" - drives the bubble color (CSS class). */
  kind: string;
  /** True for Gemini raw terminal scrollback: render verbatim, not as Markdown. */
  isRawText: boolean;
}

/**
 * The History "Show:" filter (issue #760): which kinds of content the reader wants to see. Mirrors
 * the desktop HistoryFilterConfig so the web and desktop tabs filter identically.
 */
export interface HistoryBubbleFilter {
  showToolCalls: boolean;
  showToolResults: boolean;
  showThinking: boolean;
}

/** True when at least one kind is hidden (drives the "no messages match" empty text). */
export function anyHidden(filter: HistoryBubbleFilter): boolean {
  return !filter.showToolCalls || !filter.showToolResults || !filter.showThinking;
}

// Per-part / per-bubble length caps, identical to the desktop HistoryView so neither surface janks
// on a multi-hundred-KB tool result and both truncate at the same place.
const AssistantBodyMax = 4000;
const AssistantToolResultMax = 400;
const ToolInputSuffixMax = 160;
const UserBodyMax = 4000;
const UserToolResultMax = 600;
const ToolResultBubbleMax = 2000;

/** Map applying the History tab's "Show:" filter. */
export function mapHistory(
  history: SessionHistoryDto | null | undefined,
  filter: HistoryBubbleFilter,
): HistoryBubble[] {
  const list: HistoryBubble[] = [];
  if (!history) return list;

  // Gemini has no structured transcript - its history is raw terminal scrollback the view must
  // render verbatim (a <pre> block), not as Markdown. The flag is per-history; carry it onto every
  // bubble so the renderer picks the raw path (matches the desktop).
  const isRawText = history.isRawText;
  for (const message of history.messages) {
    const bubble = mapMessage(message, isRawText, filter);
    if (bubble !== null) list.push(bubble);
  }
  return list;
}

function mapMessage(
  message: HistoryMessageDto,
  isRawText: boolean,
  filter: HistoryBubbleFilter,
): HistoryBubble | null {
  const parts = message.parts ?? [];

  if (message.role === "Assistant") {
    let sb = "";
    for (const part of parts) {
      switch (part.kind) {
        case "Text":
          sb = append(sb, part.text);
          break;
        case "Thinking":
          if (filter.showThinking && part.text.length > 0) sb = append(sb, "(thinking) " + part.text);
          break;
        case "ToolUse":
          if (filter.showToolCalls)
            sb = append(sb, "[tool] " + (part.toolName ?? "?") + toolInputSuffix(part.text));
          break;
        case "ToolResult":
          if (filter.showToolResults) sb = append(sb, "[result] " + truncate(part.text, AssistantToolResultMax));
          break;
      }
    }

    const body = sb.trim();
    return body.length === 0
      ? null
      : { speaker: "Assistant", body: truncate(body, AssistantBodyMax), kind: "assistant", isRawText };
  }

  // User role: either a real prompt, or tool results being fed back to the assistant.
  const onlyToolResults = parts.length > 0 && parts.every((p) => p.kind === "ToolResult");

  // A pure tool-result bubble is hidden entirely when results are filtered out.
  if (onlyToolResults && !filter.showToolResults) return null;

  let sb = "";
  for (const part of parts) {
    switch (part.kind) {
      case "Text":
        sb = append(sb, part.text);
        break;
      case "ToolResult":
        if (filter.showToolResults) sb = append(sb, truncate(part.text, UserToolResultMax));
        break;
    }
  }

  const userBody = sb.trim();
  if (userBody.length === 0) return null;

  return onlyToolResults
    ? { speaker: "Tool result", body: truncate(userBody, ToolResultBubbleMax), kind: "tool", isRawText }
    : { speaker: "You", body: truncate(userBody, UserBodyMax), kind: "user", isRawText };
}

function append(sb: string, text: string): string {
  if (!text) return sb;
  return sb.length > 0 ? sb + "\n" + text : text;
}

function toolInputSuffix(inputJson: string): string {
  const trimmed = inputJson.trim();
  if (trimmed.length === 0 || trimmed === "{}") return "";
  return "  " + truncate(trimmed, ToolInputSuffixMax);
}

function truncate(text: string, max: number): string {
  return text.length <= max ? text : text.substring(0, max) + " ...";
}
