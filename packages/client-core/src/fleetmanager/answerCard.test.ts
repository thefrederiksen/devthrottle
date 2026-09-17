import { describe, it, expect, vi } from "vitest";
import { answerCard } from "./answerCard";

// A card button is ONE Gateway call (the steps 5 and 6 fixes): the Gateway records the answer and queues the words to
// the Fleet Manager. The page never prompts the Fleet Manager itself.

describe("answerCard", () => {
  it("makes exactly one call, the answer, with the words exactly", async () => {
    const deps = { answer: vi.fn(async () => undefined) };

    const result = await answerCard("rec-1", 'Always run "both".', deps);

    expect(result).toEqual({ kind: "recorded" });
    expect(deps.answer).toHaveBeenCalledTimes(1);
    expect(deps.answer).toHaveBeenCalledWith("rec-1", 'Always run "both".');
  });

  it("reports a refused answer in the Gateway's words", async () => {
    const deps = { answer: vi.fn(async () => Promise.reject(new Error("already answered"))) };

    expect(await answerCard("rec-1", "Yes.", deps)).toEqual({ kind: "refused", error: "already answered" });
  });

  it("refuses empty words before calling anything", async () => {
    const deps = { answer: vi.fn(async () => undefined) };

    await expect(answerCard("rec-1", "   ", deps)).rejects.toThrow();
    expect(deps.answer).not.toHaveBeenCalled();
  });

  it("uses the answer route and nothing else by default", async () => {
    const fetchMock = vi.fn(async () => new Response("{}", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    try {
      expect(await answerCard("rec 1", "Yes.")).toEqual({ kind: "recorded" });
    } finally {
      vi.unstubAllGlobals();
    }

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/gateway/fleet-manager/outcomes/rec%201/answer");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({ answer: "Yes." });
  });
});
