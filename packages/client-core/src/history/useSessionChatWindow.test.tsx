// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, renderHook } from "@testing-library/react";
import type { HistoryMessageDto, SessionHistoryDto } from "./types";
import type { SessionHistoryPage } from "./conversationTail";
import { loadChatFilter, renderChatHistory } from "./chatView";

// Issue #4372: a Chat asked for a window opens on the last few turns, keeps taking tails on top of them, and puts
// the turns before them in front when asked - ending, after enough of that, exactly where a full read would be. The
// fake Gateway below applies the real Gateway's rules: a window cut at a prompt, a tail only for a cursor naming the
// start of the conversation, and an older page only on such a cursor (a fresh window otherwise).
//
// Revert-proof: drop `turns` from the first read and the "asks for ten turns" assertion goes red; put an older page
// behind the window instead of in front and the full-read comparison goes red.

interface Sent {
  cursor: string;
  turns?: number;
  before?: number;
}

const gw = vi.hoisted(() => ({
  state: null as unknown as { messages: HistoryMessageDto[]; sent: Sent[] },
}));

function fullDto(): SessionHistoryDto {
  return {
    sessionId: "s",
    directorId: "d",
    agent: "ClaudeCode",
    isSupported: true,
    isRawText: false,
    historyState: "Working",
    messages: gw.state.messages,
    staleNotice: null,
    emptyText: null,
    status: "ok",
    error: null,
  };
}

function cursorFor(count: number): string {
  return `${count}|${JSON.stringify(gw.state.messages.slice(0, count))}`;
}

function startOfLastTurns(end: number, turns: number): number {
  let seen = 0;
  for (let i = end - 1; i >= 0; i--) {
    if (gw.state.messages[i].role !== "User") continue;
    seen++;
    if (seen === turns) return i;
  }
  return 0;
}

vi.mock("../api/client", () => ({
  getSessionHistory: vi.fn(
    async (_sid: string, _signal: AbortSignal | undefined, cursor?: string, window?: { turns: number; before?: number }): Promise<SessionHistoryPage> => {
      gw.state.sent.push({ cursor: cursor ?? "", turns: window?.turns, before: window?.before });
      const full = fullDto();
      const all = full.messages.length;
      let held = -1;
      if (cursor) {
        const count = Number(cursor.split("|")[0]);
        if (Number.isInteger(count) && count <= all && cursor === cursorFor(count)) held = count;
      }
      if (window?.before !== undefined && held >= window.before) {
        const from = startOfLastTurns(window.before, window.turns);
        return { ...full, messages: full.messages.slice(from, window.before), olderFrom: from };
      }
      if (held > 0 && window?.before === undefined)
        return { ...full, messages: full.messages.slice(held), tailFrom: held, cursor: cursorFor(all) };
      const from = window ? startOfLastTurns(all, window.turns) : 0;
      return {
        ...full,
        messages: full.messages.slice(from),
        tailFrom: 0,
        cursor: cursorFor(all),
        ...(window ? { windowFrom: from } : {}),
      };
    },
  ),
}));

import { CHAT_LOADING_TEXT, useSessionChat } from "./useSessionChat";

// Prompt N at index 2N, its reply at 2N + 1.
function conversation(turns: number): HistoryMessageDto[] {
  return Array.from({ length: turns * 2 }, (_, i) => ({
    role: i % 2 === 0 ? "User" : "Assistant",
    parts: [{ kind: "Text", text: i % 2 === 0 ? `prompt ${i / 2}` : `reply ${(i - 1) / 2}` }],
  }));
}

async function settle(): Promise<void> {
  await act(async () => {
    for (let i = 0; i < 8; i++) await Promise.resolve();
  });
}

async function poll(): Promise<void> {
  await act(async () => {
    vi.advanceTimersByTime(2500);
  });
  await settle();
}

beforeEach(() => {
  vi.useFakeTimers();
  gw.state = { messages: conversation(25), sent: [] };
  Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "visible" });
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("useSessionChat with a window (issue #4372)", () => {
  it("opens on the last ten turns, and says there is more above", async () => {
    const { result } = renderHook(() => useSessionChat("s", { turns: 10 }));
    await settle();

    expect(gw.state.sent[0]).toEqual({ cursor: "", turns: 10, before: undefined });
    const prompts = result.current.bubbles.filter((b) => b.bubble.kind === "user").map((b) => b.bubble.body);
    expect(prompts).toHaveLength(10);
    expect(prompts[0]).toBe("prompt 15");
    expect(result.current.hasEarlier).toBe(true);
  });

  it("takes tails on top of the window, then loads earlier turns in front until it holds what a full read holds", async () => {
    const { result } = renderHook(() => useSessionChat("s", { turns: 10 }));
    await settle();

    gw.state.messages = [...gw.state.messages, ...conversation(27).slice(50)];
    await poll();
    expect(result.current.bubbles.at(-1)?.bubble.body).toBe("reply 26");

    for (let i = 0; i < 2; i++) {
      act(() => result.current.loadEarlier());
      await settle();
    }

    expect(gw.state.sent.filter((s) => s.before !== undefined).map((s) => s.before)).toEqual([30, 10]);
    expect(result.current.hasEarlier).toBe(false);
    expect(result.current.bubbles).toEqual(renderChatHistory(fullDto(), loadChatFilter()).bubbles);
  });

  it("without a window reads the whole conversation, as the Fleet Manager page still does", async () => {
    const { result } = renderHook(() => useSessionChat("s"));
    await settle();

    expect(gw.state.sent[0]).toEqual({ cursor: "", turns: undefined, before: undefined });
    expect(result.current.hasEarlier).toBe(false);
    expect(result.current.bubbles).toEqual(renderChatHistory(fullDto(), loadChatFilter()).bubbles);
  });
});

describe("what the Chat says before it has an answer (owner, 2026-10-23)", () => {
  it("says it is loading - never that the conversation has not started - until the Gateway answers", async () => {
    const { result } = renderHook(() => useSessionChat("s", { turns: 10 }));
    expect(result.current.emptyText).toBe(CHAT_LOADING_TEXT);
    expect(result.current.bubbles).toEqual([]);
    await settle();
    expect(result.current.bubbles.length).toBeGreaterThan(0);
  });

  it("drops the previous session's conversation the moment another session is shown", async () => {
    const { result, rerender } = renderHook(({ sid }) => useSessionChat(sid, { turns: 10 }), { initialProps: { sid: "s" } });
    await settle();
    expect(result.current.bubbles.length).toBeGreaterThan(0);

    rerender({ sid: "t" });

    expect(result.current.bubbles).toEqual([]);
    expect(result.current.emptyText).toBe(CHAT_LOADING_TEXT);
    expect(result.current.hasEarlier).toBe(false);
  });
});
