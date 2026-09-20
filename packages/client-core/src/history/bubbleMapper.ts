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
  /**
   * For a "user" bubble, who authored it: "owner", "agent" or "unknown" as the Gateway stamped it, and
   * "unknown" when it stamped nothing. Absent on the other kinds, where the question does not arise.
   */
  origin?: string;
  /** When the message was written (ISO), as the history carried it, or undefined when it carried none. A view
   *  that places other items among the bubbles by time reads this; the bubble itself does not show it. */
  timestamp?: string;
}

/**
 * The History "Show:" filter (issue #760): which kinds of content the reader wants to see. Mirrors
 * the desktop HistoryFilterConfig so the web and desktop tabs filter identically.
 */
export interface HistoryBubbleFilter {
  showToolCalls: boolean;
  showToolResults: boolean;
  showThinking: boolean;
  /**
   * "My prompts only": drop everything that is not a turn the reader themself submitted, so a long
   * conversation collapses to the questions that drove it and the one they are looking for is on screen.
   *
   * Unlike the other three - which ADD machinery back into the conversation - this one takes the
   * conversation away, which is why it is not drawn beside them.
   */
  myPromptsOnly: boolean;
}

/** True when at least one kind is hidden (drives the "no messages match" empty text). */
export function anyHidden(filter: HistoryBubbleFilter): boolean {
  return !filter.showToolCalls || !filter.showToolResults || !filter.showThinking || filter.myPromptsOnly;
}

/**
 * Is this bubble one of the reader's OWN prompts?
 *
 * Two rules, and the second is the one that matters. A tool result fed back to the agent is not a prompt
 * even though the transcript files it under the user - the mapper has already sorted that into its own
 * kind. And an author the Director could not establish is treated as the reader's and KEPT: "unknown"
 * means the question went unanswered, not that the answer was "the product". Hiding a turn they really
 * did type is the one failure they cannot see, so this leans to keep every time.
 */
export function isOwnPrompt(bubble: HistoryBubble): boolean {
  return bubble.kind === "user" && bubble.origin !== "agent";
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
    if (bubble === null) continue;
    // "My prompts only" is applied HERE rather than inside mapMessage, so it reads as what it is: a cut
    // across finished bubbles, not another content filter woven through the per-part switch above.
    if (filter.myPromptsOnly && !isOwnPrompt(bubble)) continue;
    if (message.timestamp) bubble.timestamp = message.timestamp;
    list.push(bubble);
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
    : {
        speaker: "You",
        body: truncate(userBody, UserBodyMax),
        kind: "user",
        isRawText,
        // A Director too old to stamp this sends nothing, and nothing reads as unknown - which is shown.
        origin: message.origin ?? "unknown",
      };
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
