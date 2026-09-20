import { describe, it, expect } from "vitest";
import type { HistoryBubbleFilter } from "./bubbleMapper";
import type { SessionHistoryDto } from "./types";
import { buildChatSignature, chatLinkLabel, renderChatHistory } from "./chatView";

// Covers the Chat logic hoisted out of the mobile Chat page into client-core (issue #1213), so both the
// mobile page and the Cockpit tab render from one tested source.

const ALL_HIDDEN: HistoryBubbleFilter = { showToolCalls: false, showToolResults: false, showThinking: false, myPromptsOnly: false };

function history(messages: SessionHistoryDto["messages"], over: Partial<SessionHistoryDto> = {}): SessionHistoryDto {
  return {
    sessionId: "s",
    directorId: "d",
    agent: "ClaudeCode",
    isSupported: true,
    isRawText: false,
    historyState: "idle",
    messages,
    status: "ok",
    ...over,
  };
}

describe("renderChatHistory", () => {
  it("renders the conversation (user + assistant text) as bubbles with the default all-hidden filter", () => {
    const h = history([
      { role: "User", parts: [{ kind: "Text", text: "hello there" }] },
      { role: "Assistant", parts: [{ kind: "Text", text: "**hi** back" }] },
    ]);
    const { bubbles } = renderChatHistory(h, ALL_HIDDEN);
    expect(bubbles).toHaveLength(2);
    expect(bubbles[0].bubble.body).toContain("hello there");
    // Assistant text is rendered through Markdown (HTML disabled), so bold becomes <strong>.
    expect(bubbles[1].html).toContain("<strong>hi</strong>");
  });

  it("renders raw-text (Gemini) history verbatim, not as Markdown", () => {
    const h = history([{ role: "Assistant", parts: [{ kind: "Text", text: "raw **not bold**" }] }], {
      isRawText: true,
    });
    const { bubbles } = renderChatHistory(h, ALL_HIDDEN);
    expect(bubbles).toHaveLength(1);
    expect(bubbles[0].bubble.isRawText).toBe(true);
    expect(bubbles[0].html).toBe(""); // raw bubbles carry no rendered HTML
  });

  it("renders the Gateway's empty-state sentence verbatim", () => {
    // The turn-push mission moved this sentence to the Gateway, which knows WHY a conversation is empty:
    // an agent that keeps no history, a computer that has not sent its conversation, one too old to send
    // one, one that is offline. The client used to guess between the first two and show "waiting" for all
    // the rest, which is how a person waits for something that is never coming.
    const h = history([], { isSupported: false, emptyText: "History is not available for this agent yet." });
    const { bubbles, emptyText } = renderChatHistory(h, ALL_HIDDEN);
    expect(bubbles).toHaveLength(0);
    expect(emptyText).toBe("History is not available for this agent yet.");
  });

  it("renders whatever sentence the Gateway sends, including ones it has never seen before", () => {
    // A new reason for an empty screen is added on the Gateway alone; this must not need a client change.
    const h = history([], { emptyText: "This session's computer is offline, so its conversation has not arrived here." });
    expect(renderChatHistory(h, ALL_HIDDEN).emptyText)
      .toBe("This session's computer is offline, so its conversation has not arrived here.");
  });

  it("falls back to the waiting line only when the Gateway sent no sentence", () => {
    const h = history([]);
    expect(renderChatHistory(h, ALL_HIDDEN).emptyText).toBe("Waiting for the conversation to start...");
  });

  it("keeps its OWN filter line above the Gateway's sentence - the Gateway cannot see the filters", () => {
    const h = history([{ role: "Assistant", parts: [{ kind: "ToolUse", text: "ls", toolName: "bash" }] }],
      { emptyText: "This session's computer is offline, so its conversation has not arrived here." });
    const { bubbles, emptyText } = renderChatHistory(h, ALL_HIDDEN);
    expect(bubbles).toHaveLength(0);
    expect(emptyText).toBe("No messages match the current filters.");
  });

  it("reports the no-match empty text when everything is filtered out", () => {
    const h = history([{ role: "Assistant", parts: [{ kind: "ToolUse", text: "ls", toolName: "bash" }] }]);
    const { bubbles, emptyText } = renderChatHistory(h, ALL_HIDDEN);
    expect(bubbles).toHaveLength(0);
    expect(emptyText).toBe("No messages match the current filters.");
  });
});

describe("buildChatSignature", () => {
  it("is stable for identical input and changes when the conversation grows or the state changes", () => {
    const h = history([
      { role: "User", parts: [{ kind: "Text", text: "one" }] },
      { role: "Assistant", parts: [{ kind: "Text", text: "two" }] },
    ]);
    const bubbles = renderChatHistory(h, ALL_HIDDEN).bubbles.map((r) => r.bubble);
    const a = buildChatSignature(bubbles, "idle", ALL_HIDDEN);
    const b = buildChatSignature(bubbles, "idle", ALL_HIDDEN);
    expect(a).toBe(b);
    expect(buildChatSignature(bubbles, "working", ALL_HIDDEN)).not.toBe(a);
    expect(buildChatSignature(bubbles, "idle", { ...ALL_HIDDEN, showThinking: true })).not.toBe(a);
    expect(buildChatSignature([], "idle", ALL_HIDDEN)).not.toBe(a);
  });
});

describe("chatLinkLabel", () => {
  it("leaves a short link intact and middle-truncates a long one", () => {
    expect(chatLinkLabel("https://example.com")).toBe("https://example.com");
    const long = "https://example.com/" + "a".repeat(80);
    const label = chatLinkLabel(long);
    expect(label).toContain("...");
    expect(label.length).toBeLessThan(long.length);
    expect(label.startsWith("https://example.com/")).toBe(true);
  });
});
