// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, renderHook } from "@testing-library/react";
import type { SessionHistoryDto } from "./types";

// Traffic optimization, phase 1: the Chat poll goes quiet while the page is hidden. It used to be a bare
// setInterval, so a hidden or forgotten tab re-downloaded the whole conversation every 2.5 seconds all night -
// on a long session that is megabytes per poll. What is pinned here:
//   - a hidden page makes ZERO requests, however long it stays hidden;
//   - becoming visible reads once AT ONCE, then polls on the interval;
//   - hiding mid-flight cancels the read in progress;
//   - the scroll-preserving signature guard still holds: an unchanged answer commits no new bubble list.
//
// Revert-proof: put the bare setInterval back in useSessionChat and the hidden-page test goes red (it counts
// four reads in ten seconds instead of none).

const reads = vi.hoisted(() => ({ calls: [] as AbortSignal[], answer: null as unknown }));

vi.mock("../api/client", () => ({
  getSessionHistory: vi.fn(async (_sid: string, signal: AbortSignal) => {
    reads.calls.push(signal);
    return reads.answer;
  }),
}));

import { useSessionChat } from "./useSessionChat";

function history(lastText: string): SessionHistoryDto {
  return {
    sessionId: "s",
    directorId: "d",
    agent: "ClaudeCode",
    isSupported: true,
    isRawText: false,
    historyState: "idle",
    messages: [
      { role: "User", parts: [{ kind: "Text", text: "do the thing" }] },
      { role: "Assistant", parts: [{ kind: "Text", text: lastText }] },
    ],
    status: "ok",
  };
}

let visibility: DocumentVisibilityState = "visible";

function setVisibility(next: DocumentVisibilityState): void {
  visibility = next;
  document.dispatchEvent(new Event("visibilitychange"));
}

// Let the awaited fetch promise and the state updates behind it settle.
async function settle(): Promise<void> {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

beforeEach(() => {
  vi.useFakeTimers();
  reads.calls = [];
  reads.answer = history("working");
  visibility = "visible";
  Object.defineProperty(document, "visibilityState", { configurable: true, get: () => visibility });
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("useSessionChat polling", () => {
  it("reads at once and then every 2.5 seconds while visible", async () => {
    renderHook(() => useSessionChat("s"));
    await settle();
    expect(reads.calls).toHaveLength(1);

    await act(async () => {
      vi.advanceTimersByTime(7500);
    });
    expect(reads.calls).toHaveLength(4);
  });

  it("makes zero requests while hidden, and one at once when it becomes visible", async () => {
    visibility = "hidden";
    renderHook(() => useSessionChat("s"));
    await act(async () => {
      vi.advanceTimersByTime(10_000);
    });
    expect(reads.calls).toHaveLength(0);

    await act(async () => {
      setVisibility("visible");
    });
    await settle();
    expect(reads.calls).toHaveLength(1);

    await act(async () => {
      vi.advanceTimersByTime(2500);
    });
    expect(reads.calls).toHaveLength(2);
  });

  it("stops and cancels the read in flight when the page is hidden", async () => {
    renderHook(() => useSessionChat("s"));
    await settle();
    expect(reads.calls).toHaveLength(1);

    await act(async () => {
      setVisibility("hidden");
    });
    expect(reads.calls[0].aborted).toBe(true);

    await act(async () => {
      vi.advanceTimersByTime(10_000);
    });
    expect(reads.calls).toHaveLength(1);
  });

  it("makes no requests with no session selected", async () => {
    renderHook(() => useSessionChat(undefined));
    await act(async () => {
      vi.advanceTimersByTime(10_000);
    });
    expect(reads.calls).toHaveLength(0);
  });

  it("keeps the same bubble list for an unchanged answer and commits a new one for a changed answer", async () => {
    const { result } = renderHook(() => useSessionChat("s"));
    await settle();
    const first = result.current.bubbles;
    expect(first).toHaveLength(2);

    // Unchanged answer (a browser 304 hands the page the same body): the guard keeps the SAME array, so the
    // view does not re-render and a reader scrolled up is not yanked.
    reads.answer = history("working");
    await act(async () => {
      vi.advanceTimersByTime(2500);
    });
    await settle();
    expect(result.current.bubbles).toBe(first);

    reads.answer = history("finished");
    await act(async () => {
      vi.advanceTimersByTime(2500);
    });
    await settle();
    expect(result.current.bubbles).not.toBe(first);
    expect(result.current.bubbles[1].bubble.body).toContain("finished");
  });
});
