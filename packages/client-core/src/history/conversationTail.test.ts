import { describe, expect, it } from "vitest";
import { applyHistoryPage, type HeldConversation, type SessionHistoryPage } from "./conversationTail";
import { renderChatHistory } from "./chatView";
import type { HistoryBubbleFilter } from "./bubbleMapper";
import type { HistoryMessageDto, SessionHistoryDto } from "./types";

// Traffic optimization, phase 2: the client half of the conversation tail. What is pinned here is that a tail
// applied to what is held gives the renderer EXACTLY the SessionHistoryDto a full read gives it - so the bubbles
// are identical - and that a tail which does not fit what is held is refused (null), never stitched on.
//
// Revert-proof: drop the length check in applyHistoryPage and the "does not start where the held copy ends" case
// goes red; build the envelope from the HELD copy instead of the new answer and the stale-notice case goes red.

const FILTERS: HistoryBubbleFilter[] = [
  { showToolCalls: false, showToolResults: false, showThinking: false, myPromptsOnly: false },
  { showToolCalls: true, showToolResults: true, showThinking: true, myPromptsOnly: false },
  { showToolCalls: false, showToolResults: false, showThinking: false, myPromptsOnly: true },
];

function message(i: number): HistoryMessageDto {
  return {
    role: i % 2 === 0 ? "User" : "Assistant",
    timestamp: `2026-09-21T12:00:${String(i % 60).padStart(2, "0")}Z`,
    parts: [
      { kind: "Text", text: `message ${i} with **markdown** and a link https://example.com/${i}` },
      { kind: "ToolUse", text: `{"cmd":"ls ${i}"}`, toolName: "Bash", toolId: `t${i}` },
      { kind: "ToolResult", text: `result ${i}`, toolId: `t${i}` },
      { kind: "Thinking", text: `thinking ${i}` },
    ],
  };
}

function full(count: number, extra: Partial<SessionHistoryDto> = {}): SessionHistoryDto {
  return {
    sessionId: "s",
    directorId: "d",
    agent: "ClaudeCode",
    isSupported: true,
    isRawText: false,
    historyState: "Working",
    messages: Array.from({ length: count }, (_, i) => message(i)),
    staleNotice: null,
    emptyText: null,
    status: "ok",
    error: null,
    ...extra,
  };
}

// What the Gateway sends for a cursor request: the full answer's envelope, the messages from `from`, and a cursor.
function page(count: number, from: number, extra: Partial<SessionHistoryDto> = {}): SessionHistoryPage {
  const f = full(count, extra);
  return { ...f, messages: f.messages.slice(from), tailFrom: from, cursor: `c${count}` };
}

describe("applyHistoryPage", () => {
  it("a run of tails, each applied to what is held, always equals a full read - and renders the same bubbles", () => {
    let held: HeldConversation | null = null;
    let lengthHeld = 0;
    for (const length of [0, 2, 2, 3, 10, 11, 40]) {
      const sent = held?.cursor ?? "";
      const answer = page(length, held === null ? 0 : lengthHeld);
      const next = applyHistoryPage(held, "s", sent, answer);
      expect(next).not.toBeNull();
      held = next!;
      lengthHeld = length;

      expect(held.history).toEqual(full(length));
      for (const filter of FILTERS) {
        expect(renderChatHistory(held.history, filter)).toEqual(renderChatHistory(full(length), filter));
      }
    }
  });

  it("takes every envelope field from the NEW answer, not from what was held", () => {
    const held = applyHistoryPage(null, "s", "", page(3, 0))!;
    const next = applyHistoryPage(held, "s", held.cursor, page(4, 3, {
      staleNotice: "This session's computer has not checked in recently.",
      historyState: "Idle",
      status: "ok",
    }))!;
    expect(next.history).toEqual(full(4, {
      staleNotice: "This session's computer has not checked in recently.",
      historyState: "Idle",
    }));
  });

  it("a whole answer (tailFrom 0) replaces whatever was held", () => {
    const held = applyHistoryPage(null, "s", "", page(5, 0))!;
    const next = applyHistoryPage(held, "s", held.cursor, page(2, 0))!;
    expect(next.history).toEqual(full(2));
  });

  it("an answer from an older Gateway, with no tailFrom and no cursor, is the whole conversation", () => {
    const next = applyHistoryPage(null, "s", "", full(3) as SessionHistoryPage)!;
    expect(next.history).toEqual(full(3));
    expect(next.cursor).toBe("");
  });

  it("refuses a tail that does not start exactly where the held copy ends", () => {
    const held = applyHistoryPage(null, "s", "", page(3, 0))!;
    expect(applyHistoryPage(held, "s", held.cursor, page(6, 2))).toBeNull(); // would duplicate message 2
    expect(applyHistoryPage(held, "s", held.cursor, page(6, 4))).toBeNull(); // would skip message 3
  });

  it("refuses a tail with nothing held, for another session, or for a cursor that is not the one held", () => {
    const held = applyHistoryPage(null, "s", "", page(3, 0))!;
    expect(applyHistoryPage(null, "s", "c3", page(5, 3))).toBeNull();
    expect(applyHistoryPage(held, "other", held.cursor, page(5, 3))).toBeNull();
    expect(applyHistoryPage(held, "s", "c2", page(5, 3))).toBeNull();
    expect(applyHistoryPage(held, "s", "", page(5, 3))).toBeNull();
  });

  it("never changes what was held", () => {
    const held = applyHistoryPage(null, "s", "", page(3, 0))!;
    const before = JSON.stringify(held);
    applyHistoryPage(held, "s", held.cursor, page(6, 3));
    expect(JSON.stringify(held)).toBe(before);
  });
});
