import { describe, it, expect } from "vitest";
import type { RenderedBubble } from "@devthrottle/client-core/history/chatView";
import { mergeConversation } from "./conversation";
import { DECISION_CARD, FINDING_CARD, READY_CARD } from "./fixtures";

function bubble(body: string, timestamp?: string): RenderedBubble {
  return { bubble: { speaker: "Assistant", body, kind: "assistant", isRawText: false, timestamp }, html: body, links: [] };
}

function keys(items: ReturnType<typeof mergeConversation>): string[] {
  return items.map((i) => (i.kind === "card" ? `card:${i.card.title}` : `bubble:${i.bubble.bubble.body}`));
}

describe("mergeConversation", () => {
  it("places each card before the first bubble written after it was filed", () => {
    // READY 14:14, FINDING 14:31, DECISION 14:32.
    const items = mergeConversation(
      [bubble("a", "2026-09-16T14:00:00Z"), bubble("b", "2026-09-16T14:20:00Z"), bubble("c", "2026-09-16T14:40:00Z")],
      [DECISION_CARD, READY_CARD, FINDING_CARD],
    );

    expect(keys(items)).toEqual([
      "bubble:a",
      `card:${READY_CARD.title}`,
      "bubble:b",
      `card:${FINDING_CARD.title}`,
      `card:${DECISION_CARD.title}`,
      "bubble:c",
    ]);
  });

  it("never places a card by a missing bubble time, and puts late cards at the end", () => {
    const items = mergeConversation([bubble("untimed"), bubble("early", "2026-09-16T14:00:00Z")], [READY_CARD]);

    expect(keys(items)).toEqual(["bubble:untimed", "bubble:early", `card:${READY_CARD.title}`]);
  });

  it("shows the cards alone when there is no conversation", () => {
    expect(keys(mergeConversation([], [FINDING_CARD, READY_CARD]))).toEqual([
      `card:${READY_CARD.title}`,
      `card:${FINDING_CARD.title}`,
    ]);
  });
});
