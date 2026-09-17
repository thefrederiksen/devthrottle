import { describe, it, expect, vi } from "vitest";
import { answerCard, retellFleetManager } from "./answerCard";

// A card button's two calls (the Fleet Manager mission, step 6): answer first, then tell the Fleet Manager, with
// the same words; each failure is reported, never swallowed.

describe("answerCard", () => {
  it("answers first, then prompts, with identical words", async () => {
    const order: string[] = [];
    const deps = {
      answer: vi.fn(async (_id: string, w: string) => void order.push(`answer:${w}`)),
      prompt: vi.fn(async (_sid: string, w: string) => void order.push(`prompt:${w}`)),
    };

    const result = await answerCard("rec-1", "fm-1", "Always run both.", deps);

    expect(result).toEqual({ kind: "sent" });
    expect(order).toEqual(["answer:Always run both.", "prompt:Always run both."]);
    expect(deps.answer).toHaveBeenCalledWith("rec-1", "Always run both.");
    expect(deps.prompt).toHaveBeenCalledWith("fm-1", "Always run both.");
  });

  it("does not prompt when the answer is refused", async () => {
    const deps = { answer: vi.fn(async () => Promise.reject(new Error("already answered"))), prompt: vi.fn(async () => undefined) };

    const result = await answerCard("rec-1", "fm-1", "Yes.", deps);

    expect(result).toEqual({ kind: "answer-failed", error: "already answered" });
    expect(deps.prompt).not.toHaveBeenCalled();
  });

  it("reports a recorded answer whose prompt failed", async () => {
    const deps = { answer: vi.fn(async () => undefined), prompt: vi.fn(async () => Promise.reject(new Error("offline"))) };

    expect(await answerCard("rec-1", "fm-1", "Yes.", deps)).toEqual({ kind: "prompt-failed", error: "offline" });
    expect(deps.answer).toHaveBeenCalledTimes(1);
  });

  it("reports a recorded answer when no Fleet Manager is marked", async () => {
    const deps = { answer: vi.fn(async () => undefined), prompt: vi.fn(async () => undefined) };

    const result = await answerCard("rec-1", null, "Yes.", deps);

    expect(result.kind).toBe("prompt-failed");
    expect(deps.prompt).not.toHaveBeenCalled();
  });

  it("refuses empty words before calling anything", async () => {
    const deps = { answer: vi.fn(async () => undefined), prompt: vi.fn(async () => undefined) };

    await expect(answerCard("rec-1", "fm-1", "   ", deps)).rejects.toThrow();
    expect(deps.answer).not.toHaveBeenCalled();
  });

  it("retells only the prompt", async () => {
    const deps = { answer: vi.fn(async () => undefined), prompt: vi.fn(async () => undefined) };

    expect(await retellFleetManager("fm-1", "Yes.", deps)).toEqual({ kind: "sent" });
    expect(deps.answer).not.toHaveBeenCalled();
    expect(deps.prompt).toHaveBeenCalledWith("fm-1", "Yes.");
  });
});
