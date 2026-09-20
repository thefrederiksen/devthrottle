import { anyHidden, mapHistory, type HistoryBubble, type HistoryBubbleFilter } from "./bubbleMapper";
import { cleanForReading } from "./historyText";
import { markdownToHtml } from "./historyMarkdown";
import { extractLinks, type HistoryLink } from "./historyLinks";
import type { SessionHistoryDto } from "./types";

// The shared Session Chat model (hoisted from apps/mobile/src/pages/Chat.tsx for issue #1213 so both
// the mobile Chat page and the Cockpit Chat tab render thin views over the same code). It carries the
// pure logic that turns GET /sessions/{sid}/history into rendered conversation bubbles: the desktop
// History-tab "Show:" filter persistence, the cheap change-signature (so a steady poll never yanks a
// scrolled-up reader), the per-bubble clean + Markdown + link extraction, and the empty-state text.
// Transcript integrity (CodingStyle.md s16): the server already returned the user's words corrected by
// the single dictionary-correction engine; this only displays them - it never rewrites the words.

export const CHAT_FILTER_STORAGE_KEY = "ccHistoryFilter";

export interface RenderedBubble {
  bubble: HistoryBubble;
  html: string;
  links: HistoryLink[];
}

export interface RenderedHistory {
  bubbles: RenderedBubble[];
  emptyText: string;
}

// Read the persisted "Show:" filter; defaults to all hidden (the desktop HistoryFilterConfig default -
// just the conversation). Format matches the desktop's comma-joined booleans.
export function loadChatFilter(): HistoryBubbleFilter {
  const fallback: HistoryBubbleFilter = {
    showToolCalls: false,
    showToolResults: false,
    showThinking: false,
    myPromptsOnly: false,
  };
  try {
    const raw = window.localStorage.getItem(CHAT_FILTER_STORAGE_KEY);
    if (!raw) return fallback;
    const parts = raw.split(",");
    // THREE OR FOUR, and a missing fourth is off. Every browser that used this screen before "my prompts
    // only" existed has three values saved; an exact length check would fail all of them and silently
    // reset the three choices the reader had made. A fourth value is read when it is there.
    if (parts.length === 3 || parts.length === 4) {
      return {
        showToolCalls: parts[0] === "true",
        showToolResults: parts[1] === "true",
        showThinking: parts[2] === "true",
        // Deliberately NOT persisted-on by default even when it was on last time - see persistChatFilter.
        myPromptsOnly: false,
      };
    }
  } catch {
    /* localStorage unavailable - fall back to the hidden-machinery default */
  }
  return fallback;
}

/**
 * Persist the reader's "Show:" choices. Written with a trailing fourth value so the format is honest
 * about its own length, but "my prompts only" is ALWAYS written off.
 *
 * It is a way of finding something, not a way of reading a conversation. A reader who left it on,
 * closed the tab and came back a day later would be shown a conversation with every answer missing and
 * no memory of having asked for that - and the most likely reading of that screen is "the Cockpit has
 * lost my history". The three machinery toggles are settings and persist; this is an action and does not.
 */
export function persistChatFilter(filter: HistoryBubbleFilter): void {
  try {
    window.localStorage.setItem(
      CHAT_FILTER_STORAGE_KEY,
      `${filter.showToolCalls},${filter.showToolResults},${filter.showThinking},false`,
    );
  } catch {
    /* localStorage unavailable - the choice simply will not persist this session */
  }
}

// Cheap change signature: count + total chars + last bubble tail + history state + filter. Mirrors the
// desktop HistoryPane.BuildSignature so an unchanged poll never disturbs a scrolled-up reader.
export function buildChatSignature(
  bubbles: HistoryBubble[],
  state: string | null | undefined,
  filter: HistoryBubbleFilter,
): string {
  const f = `${filter.showToolCalls}${filter.showToolResults}${filter.showThinking}${filter.myPromptsOnly}`;
  if (bubbles.length === 0) return `0|${state ?? ""}|${f}`;
  let total = 0;
  for (const b of bubbles) total += b.body.length;
  const last = bubbles[bubbles.length - 1].body;
  const tail = last.length <= 64 ? last : last.slice(-64);
  return `${bubbles.length}|${total}|${tail}|${state ?? ""}|${f}`;
}

// A link label, truncated in the middle for long URLs/paths (mirrors HistoryPane.LinkLabel).
export function chatLinkLabel(text: string): string {
  return text.length <= 60 ? text : text.slice(0, 28) + "..." + text.slice(-28);
}

// Map the given history through the filter, clean + render each bubble, and derive the empty-state
// text. Pure: no state, no scroll - the caller decides whether the signature changed before committing.
export function renderChatHistory(history: SessionHistoryDto | null, filter: HistoryBubbleFilter): RenderedHistory {
  const mapped = mapHistory(history, filter);

  // THE GATEWAY WRITES THIS SENTENCE (turn-push mission, phase 2). It used to be guessed here from a
  // boolean, so every reason an empty screen could be empty - a computer that never sent its conversation,
  // one too old to send one, one that is offline - rendered as "waiting for the conversation to start",
  // and a person could sit in front of that waiting for something that was never coming. The one line
  // still written here is about the reader's OWN filters, which the Gateway cannot see.
  let emptyText = history?.emptyText
    // No sentence from the Gateway: fall back to what this file used to derive, so a response from any
    // older build still reads sensibly rather than telling an unsupported agent's reader to keep waiting.
    ?? (history?.isSupported === false ? "History is not available for this agent yet." : null)
    ?? "Waiting for the conversation to start...";
  if (mapped.length === 0 && anyHidden(filter) && history && history.messages.length > 0)
    emptyText = "No messages match the current filters.";

  const bubbles: RenderedBubble[] = [];
  for (const b of mapped) {
    // Raw terminal scrollback (Gemini) is shown verbatim; everything else is cleaned of transcript
    // machinery (command wrapper tags, system-reminder blocks, ANSI codes) before Markdown.
    if (b.isRawText) {
      bubbles.push({ bubble: b, html: "", links: [] });
      continue;
    }
    const clean = cleanForReading(b.body);
    if (clean.length === 0) continue; // the whole message was machinery - drop the empty bubble
    bubbles.push({ bubble: { ...b, body: clean }, html: markdownToHtml(clean), links: extractLinks(clean) });
  }
  return { bubbles, emptyText };
}
