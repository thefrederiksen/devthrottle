// MY PROMPTS ONLY - the cut, and the one mistake it must never make.
//
// The reader wants to find something they asked a long way up a conversation. Showing their own turns
// and nothing else is the fastest way to it. What makes this more than a one-line filter is that the
// agent's transcript files SEVERAL things under "user" that the reader never said: tool results fed back
// to the agent, and anything the product itself typed into the session - a fleet doorbell, a handover, a
// queue drain.
//
// So the cut leans to KEEP. A turn whose author the Director could not establish comes back "unknown",
// and unknown is shown. Getting it wrong that way puts a doorbell in the list, which the reader can see.
// Getting it wrong the other way hides something they typed, which they cannot see and would have no
// reason to suspect - and the whole feature exists to be trusted to contain everything they asked.
import { describe, it, expect } from "vitest";
import { mapHistory, isOwnPrompt, anyHidden, type HistoryBubbleFilter } from "./bubbleMapper";
import type { SessionHistoryDto } from "./types";

const MINE: HistoryBubbleFilter = {
  showToolCalls: false,
  showToolResults: false,
  showThinking: false,
  myPromptsOnly: true,
};
const EVERYTHING: HistoryBubbleFilter = { ...MINE, myPromptsOnly: false };

function history(...messages: SessionHistoryDto["messages"]): SessionHistoryDto {
  return {
    sessionId: "s",
    directorId: "d",
    agent: "ClaudeCode",
    isSupported: true,
    isRawText: false,
    status: "ok",
    messages,
  };
}

const user = (text: string, origin?: string) => ({
  role: "User",
  parts: [{ kind: "Text", text }],
  ...(origin === undefined ? {} : { origin }),
});
const assistant = (text: string) => ({ role: "Assistant", parts: [{ kind: "Text", text }] });
const toolResultFedBack = (text: string) => ({ role: "User", parts: [{ kind: "ToolResult", text }] });

describe("my prompts only", () => {
  it("keeps the owner's turns and drops the agent's answers", () => {
    const bubbles = mapHistory(
      history(user("find my last prompt", "owner"), assistant("Here it is."), user("thanks", "owner")),
      MINE,
    );

    expect(bubbles.map((b) => b.body)).toEqual(["find my last prompt", "thanks"]);
  });

  it("drops what the product typed into the session - the fleet doorbell", () => {
    const bubbles = mapHistory(
      history(
        user("find my last prompt", "owner"),
        user("[DevThrottle doorbell] 2 fleet messages are waiting for you.", "agent"),
        user("thanks", "owner"),
      ),
      MINE,
    );

    expect(bubbles.map((b) => b.body)).toEqual(["find my last prompt", "thanks"]);
  });

  it("KEEPS a turn whose author is unknown, rather than guessing it away", () => {
    const bubbles = mapHistory(history(user("typed before this Director started", "unknown")), MINE);

    expect(bubbles.map((b) => b.body)).toEqual(["typed before this Director started"]);
  });

  it("KEEPS a turn from a Director too old to stamp an author at all", () => {
    // No origin field on the wire whatsoever. It must read as unknown, and unknown is shown.
    const bubbles = mapHistory(history(user("from an older Director")), MINE);

    expect(bubbles.map((b) => b.body)).toEqual(["from an older Director"]);
  });

  it("drops tool results the agent fed back to itself, which no one typed", () => {
    const bubbles = mapHistory(
      history(user("run the tests", "owner"), toolResultFedBack("exit code 0")),
      { ...MINE, showToolResults: true },
    );

    expect(bubbles.map((b) => b.body)).toEqual(["run the tests"]);
  });

  it("changes nothing when it is off", () => {
    const all = mapHistory(
      history(user("find my last prompt", "owner"), assistant("Here it is."), user("doorbell", "agent")),
      EVERYTHING,
    );

    expect(all).toHaveLength(3);
  });

  it("counts as a filter, so the empty screen can say so", () => {
    expect(anyHidden({ showToolCalls: true, showToolResults: true, showThinking: true, myPromptsOnly: true })).toBe(true);
    expect(anyHidden({ showToolCalls: true, showToolResults: true, showThinking: true, myPromptsOnly: false })).toBe(false);
  });
});

describe("isOwnPrompt", () => {
  it("is true for the owner and for an unestablished author, false for the product", () => {
    expect(isOwnPrompt({ speaker: "You", body: "a", kind: "user", isRawText: false, origin: "owner" })).toBe(true);
    expect(isOwnPrompt({ speaker: "You", body: "a", kind: "user", isRawText: false, origin: "unknown" })).toBe(true);
    expect(isOwnPrompt({ speaker: "You", body: "a", kind: "user", isRawText: false })).toBe(true);
    expect(isOwnPrompt({ speaker: "You", body: "a", kind: "user", isRawText: false, origin: "agent" })).toBe(false);
  });

  it("is false for an answer and for a tool result, whatever their origin says", () => {
    expect(isOwnPrompt({ speaker: "Assistant", body: "a", kind: "assistant", isRawText: false })).toBe(false);
    expect(isOwnPrompt({ speaker: "Tool result", body: "a", kind: "tool", isRawText: false })).toBe(false);
  });
});
