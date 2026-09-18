import { describe, it, expect, vi } from "vitest";
import { GatewayError } from "../api/client";
import { answerWalkthroughItem, snoozeWalkthroughItem, type WalkthroughActionDeps } from "./walkthroughActions";

// The walkthrough's buttons (the Fleet Manager mission, step 7): the session is acted on FIRST, and the record is
// updated only after it took the act. A refusal is shown as the route's own sentence and nothing else is sent.

function deps(overrides: Partial<WalkthroughActionDeps> = {}, order: string[] = []): WalkthroughActionDeps {
  return {
    answer: vi.fn(async (sid: string, verdict: string, indexes: readonly number[]) => {
      order.push(`answer:${sid}:${verdict}:${indexes.join("+")}`);
      return { reason: "Sent to the session." };
    }),
    recordAnswer: vi.fn(async (rec: string, verdict: string, indexes: readonly number[]) => {
      order.push(`record:${rec}:${verdict}:${indexes.join("+")}`);
    }),
    snooze: vi.fn(async (sid: string, minutes: number) => {
      order.push(`snooze:${sid}:${minutes}`);
    }),
    recordSnooze: vi.fn(async (rec: string) => {
      order.push(`note:${rec}`);
    }),
    ...overrides,
  };
}

describe("answerWalkthroughItem", () => {
  it("answers the session first, then records the same options", async () => {
    const order: string[] = [];

    const result = await answerWalkthroughItem("sid-1", "rec-1", "verdict-1", [1, 0], deps({}, order));

    expect(result).toEqual({ kind: "done", sentence: "Sent to the session." });
    expect(order).toEqual(["answer:sid-1:verdict-1:1+0", "record:rec-1:verdict-1:1+0"]);
  });

  it("shows a refused answer verbatim and records nothing", async () => {
    const refusal = new GatewayError(409, "The session's screen changed since the Wingman read it, so nothing was sent.", {
      reason: "The session's screen changed since the Wingman read it, so nothing was sent.",
      code: "answer-screen-changed",
    });
    const d = deps({ answer: vi.fn(async () => Promise.reject(refusal)) });

    const result = await answerWalkthroughItem("sid-1", "rec-1", "verdict-1", [0], d);

    expect(result).toEqual({
      kind: "refused",
      sentence: "The session's screen changed since the Wingman read it, so nothing was sent.",
    });
    expect(d.recordAnswer).not.toHaveBeenCalled();
  });

  it("says when the session took the answer but the record was not updated", async () => {
    const d = deps({ recordAnswer: vi.fn(async () => Promise.reject(new Error("outcome was already answered"))) });

    const result = await answerWalkthroughItem("sid-1", "rec-1", "verdict-1", [0], d);

    expect(result).toEqual({ kind: "record-failed", sentence: "outcome was already answered" });
    expect(d.answer).toHaveBeenCalledOnce();
  });

  it("sends an empty selection for a confirmed typed reply", async () => {
    const order: string[] = [];

    await answerWalkthroughItem("sid-1", "rec-1", "verdict-1", [], deps({}, order));

    expect(order).toEqual(["answer:sid-1:verdict-1:", "record:rec-1:verdict-1:"]);
  });
});

describe("snoozeWalkthroughItem", () => {
  it("snoozes for the Gateway's length first, then notes it on the record", async () => {
    const order: string[] = [];

    const result = await snoozeWalkthroughItem("sid-1", "rec-1", 60, deps({}, order));

    expect(result).toEqual({ kind: "done", sentence: "" });
    expect(order).toEqual(["snooze:sid-1:60", "note:rec-1"]);
  });

  it("notes nothing when the snooze is refused", async () => {
    const d = deps({
      snooze: vi.fn(async () =>
        Promise.reject(new GatewayError(409, "This session has exited, so it cannot be snoozed.", {
          reason: "This session has exited, so it cannot be snoozed.",
        })),
      ),
    });

    const result = await snoozeWalkthroughItem("sid-1", "rec-1", 60, d);

    expect(result).toEqual({ kind: "refused", sentence: "This session has exited, so it cannot be snoozed." });
    expect(d.recordSnooze).not.toHaveBeenCalled();
  });
});
