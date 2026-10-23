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
  state: null as unknown as {
    /** Session "s"'s conversation; `other` is session "t"'s. */
    messages: HistoryMessageDto[];
    other: HistoryMessageDto[];
    sent: Sent[];
    /** When true, an older-page answer is held back until `release` is called - to land things while it is in flight. */
    holdOlder: boolean;
    release: (() => void) | null;
    /** When true, every answer for session "t" is held back until `releaseT` is called. */
    holdT: boolean;
    releaseT: (() => void) | null;
    /** When true, every poll of session "s" (not an older page) is held back until `releasePolls` answers them. */
    holdPolls: boolean;
    heldPolls: Array<() => void>;
  },
}));

function messagesFor(sid: string): HistoryMessageDto[] {
  return sid === "t" ? gw.state.other : gw.state.messages;
}

function fullDto(sid = "s"): SessionHistoryDto {
  return {
    sessionId: sid,
    directorId: "d",
    agent: "ClaudeCode",
    isSupported: true,
    isRawText: false,
    historyState: "Working",
    messages: messagesFor(sid),
    staleNotice: null,
    emptyText: null,
    status: "ok",
    error: null,
  };
}

function cursorFor(sid: string, count: number): string {
  return `${sid}|${count}|${JSON.stringify(messagesFor(sid).slice(0, count))}`;
}

function startOfLastTurns(sid: string, end: number, turns: number): number {
  let seen = 0;
  for (let i = end - 1; i >= 0; i--) {
    if (messagesFor(sid)[i].role !== "User") continue;
    seen++;
    if (seen === turns) return i;
  }
  return 0;
}

vi.mock("../api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../api/client")>()),
  getSessionHistory: vi.fn(
    async (sid: string, _signal: AbortSignal | undefined, cursor?: string, window?: { turns: number; before?: number }): Promise<SessionHistoryPage> => {
      gw.state.sent.push({ cursor: cursor ?? "", turns: window?.turns, before: window?.before });
      const full = fullDto(sid);
      const all = full.messages.length;
      let held = -1;
      if (cursor) {
        const count = Number(cursor.split("|")[1]);
        if (Number.isInteger(count) && count <= all && cursor === cursorFor(sid, count)) held = count;
      }
      let answer: SessionHistoryPage;
      if (window?.before !== undefined && held >= window.before) {
        const from = startOfLastTurns(sid, window.before, window.turns);
        answer = { ...full, messages: full.messages.slice(from, window.before), olderFrom: from };
      } else if (held > 0 && window?.before === undefined) {
        answer = { ...full, messages: full.messages.slice(held), tailFrom: held, cursor: cursorFor(sid, all) };
      } else {
        const from = window ? startOfLastTurns(sid, all, window.turns) : 0;
        answer = {
          ...full,
          messages: full.messages.slice(from),
          tailFrom: 0,
          cursor: cursorFor(sid, all),
          ...(window ? { windowFrom: from } : {}),
        };
      }
      // The answer is decided NOW, from the conversation as it is now, as the real Gateway's is; only its arrival waits.
      if (sid === "t" && gw.state.holdT)
        return new Promise((resolve) => {
          gw.state.releaseT = () => resolve(answer);
        });
      if (sid === "s" && window?.before === undefined && gw.state.holdPolls)
        return new Promise((resolve) => {
          gw.state.heldPolls.push(() => resolve(answer));
        });
      if (window?.before !== undefined && gw.state.holdOlder)
        return new Promise((resolve) => {
          gw.state.release = () => resolve(answer);
        });
      return answer;
    },
  ),
}));

import { CHAT_LOADING_TEXT, useSessionChat } from "./useSessionChat";

// Prompt N at index 2N, its reply at 2N + 1.
function conversation(turns: number, label = ""): HistoryMessageDto[] {
  return Array.from({ length: turns * 2 }, (_, i) => ({
    role: i % 2 === 0 ? "User" : "Assistant",
    parts: [{ kind: "Text", text: i % 2 === 0 ? `${label}prompt ${i / 2}` : `${label}reply ${(i - 1) / 2}` }],
  }));
}

async function release(): Promise<void> {
  const r = gw.state.release;
  expect(r).not.toBeNull();
  gw.state.release = null;
  await act(async () => {
    r!();
  });
  await settle();
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
  gw.state = { messages: conversation(25), other: conversation(25, "t-"), sent: [], holdOlder: false, release: null, holdT: false, releaseT: null, holdPolls: false, heldPolls: [] };
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

// The races the review named. Revert-proof: drop the whole-reads check and the /clear case puts the OLD conversation's
// page in front of the new one (both windows start at message 30); drop the session check and the switch case draws
// session s's turns under session t.
describe("an older page in flight (issue #4372, review)", () => {
  it("a tail that lands while the page is in flight keeps both, with no gap and no duplicate", async () => {
    const { result } = renderHook(() => useSessionChat("s", { turns: 10 }));
    await settle();
    gw.state.holdOlder = true;

    act(() => result.current.loadEarlier());
    gw.state.messages = [...gw.state.messages, ...conversation(26).slice(50)];
    await poll();
    expect(result.current.bubbles.at(-1)?.bubble.body).toBe("reply 25");
    await release();

    expect(result.current.earlierPages).toBe(1);
    gw.state.holdOlder = false;
    act(() => result.current.loadEarlier());
    await settle();
    expect(result.current.hasEarlier).toBe(false);
    expect(result.current.bubbles).toEqual(renderChatHistory(fullDto(), loadChatFilter()).bubbles);
  });

  it("a /clear that lands while the page is in flight drops the page - never mixes the two conversations", async () => {
    const { result } = renderHook(() => useSessionChat("s", { turns: 10 }));
    await settle();
    gw.state.holdOlder = true;

    act(() => result.current.loadEarlier());
    gw.state.messages = conversation(25, "after-clear ");
    await poll();
    await release();

    const bodies = result.current.bubbles.map((b) => b.bubble.body);
    expect(bodies.every((b) => b.startsWith("after-clear "))).toBe(true);
    expect(result.current.earlierPages).toBe(0);
    expect(result.current.loadingEarlier).toBe(false);
  });

  it("a session switch while the page is in flight never draws the previous session", async () => {
    const { result, rerender } = renderHook(({ sid }) => useSessionChat(sid, { turns: 10 }), { initialProps: { sid: "s" } });
    await settle();
    gw.state.holdOlder = true;

    // The order the review found: the old session's page lands BEFORE the new session's first answer.
    gw.state.holdT = true;
    act(() => result.current.loadEarlier());
    rerender({ sid: "t" });
    expect(result.current.loadingEarlier).toBe(false);
    await release();
    expect(result.current.bubbles).toEqual([]);

    const t = gw.state.releaseT;
    expect(t).not.toBeNull();
    await act(async () => {
      t!();
    });
    await settle();
    const bodies = result.current.bubbles.map((b) => b.bubble.body);
    expect(bodies.length).toBeGreaterThan(0);
    expect(bodies.every((b) => b.startsWith("t-"))).toBe(true);
    expect(result.current.earlierPages).toBe(0);
  });

  it("a failed older-page fetch says so, and the next try clears it", async () => {
    const client = await import("../api/client");
    const { result } = renderHook(() => useSessionChat("s", { turns: 10 }));
    await settle();
    vi.mocked(client.getSessionHistory).mockRejectedValueOnce(new Error("the Gateway is unreachable"));

    act(() => result.current.loadEarlier());
    await settle();
    expect(result.current.earlierError).not.toBeNull();
    expect(result.current.loadingEarlier).toBe(false);

    act(() => result.current.loadEarlier());
    expect(result.current.earlierError).toBeNull();
    await settle();
    expect(result.current.earlierPages).toBe(1);
  });
});

// Issue #4387: the timer fires every 2.5 seconds whether or not the last poll answered. Revert-proof: drop the
// in-flight check and the first case sends four reads instead of one; key it on anything that outlives a session
// switch and the second case waits for the old session's poll before reading the new one.
describe("one poll at a time (issue #4387)", () => {
  it("a slow answer is not stacked with more reads, and polling goes on once it lands", async () => {
    renderHook(() => useSessionChat("s", { turns: 10 }));
    await settle();
    gw.state.holdPolls = true;
    const before = gw.state.sent.length;

    await poll();
    await poll();
    await poll();
    expect(gw.state.sent.length).toBe(before + 1);

    gw.state.holdPolls = false;
    await act(async () => {
      for (const answer of gw.state.heldPolls.splice(0)) answer();
    });
    await settle();
    await poll();
    expect(gw.state.sent.length).toBe(before + 2);
  });

  it("a session switch reads the new session at once, though the previous one's poll is still waiting", async () => {
    const { result, rerender } = renderHook(({ sid }) => useSessionChat(sid, { turns: 10 }), { initialProps: { sid: "s" } });
    await settle();
    gw.state.holdPolls = true;
    await poll();
    const before = gw.state.sent.length;

    rerender({ sid: "t" });
    await settle();

    expect(gw.state.sent.length).toBe(before + 1);
    expect(gw.state.sent.at(-1)?.cursor).toBe("");
    expect(result.current.bubbles.length).toBeGreaterThan(0);
    expect(result.current.bubbles.every((b) => b.bubble.body.startsWith("t-"))).toBe(true);
  });
});
