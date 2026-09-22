// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, renderHook } from "@testing-library/react";
import type { HistoryMessageDto, SessionHistoryDto } from "./types";
import type { SessionHistoryPage } from "./conversationTail";
import { loadChatFilter, renderChatHistory } from "./chatView";

// Traffic optimization, phase 2: the Chat hook reads the conversation as a TAIL after the first read, and what it
// renders is exactly what a full read would render. A fake Gateway below applies the Gateway's rule - a tail only
// for a cursor naming exactly the start of the current conversation, otherwise the whole thing - and the hook's
// bubbles are compared against the bubbles of a full read of the same conversation after every poll.
//
// Revert-proof: make the hook drop the cursor (always ask for the whole conversation) and the "asks with its
// cursor" assertions go red; stitch a tail on without checking it fits and the misbehaving-Gateway test goes red.

interface FakeGateway {
  sessions: Map<string, { generation: string; messages: HistoryMessageDto[]; staleNotice: string | null }>;
  cursorsSent: string[];
  /** When set, the next answer is this, whatever was asked - to prove the hook refuses a tail that does not fit. */
  override: SessionHistoryPage | null;
}

const gw = vi.hoisted(() => ({ state: null as unknown as FakeGateway }));

function fullDto(sid: string): SessionHistoryDto {
  const s = gw.state.sessions.get(sid)!;
  return {
    sessionId: sid,
    directorId: "d",
    agent: "ClaudeCode",
    isSupported: true,
    isRawText: false,
    historyState: "Working",
    messages: s.messages,
    staleNotice: s.staleNotice,
    emptyText: s.messages.length === 0 ? "Waiting for the conversation to start..." : null,
    status: "ok",
    error: null,
  };
}

// The fake cursor names the generation, the count, and the exact JSON of the prefix - the same three things the
// real one hashes.
function cursorFor(sid: string, count: number): string {
  const s = gw.state.sessions.get(sid)!;
  return `${s.generation}|${count}|${JSON.stringify(s.messages.slice(0, count))}`;
}

vi.mock("../api/client", () => ({
  getSessionHistory: vi.fn(async (sid: string, _signal: AbortSignal, cursor?: string): Promise<SessionHistoryPage> => {
    gw.state.cursorsSent.push(cursor ?? "(none)");
    if (gw.state.override) {
      const o = gw.state.override;
      gw.state.override = null;
      return o;
    }
    const full = fullDto(sid);
    const all = full.messages.length;
    let from = 0;
    if (cursor) {
      const count = Number(cursor.split("|")[1]);
      if (Number.isInteger(count) && count <= all && cursor === cursorFor(sid, count)) from = count;
    }
    return { ...full, messages: full.messages.slice(from), tailFrom: from, cursor: cursorFor(sid, all) };
  }),
}));

import { useSessionChat } from "./useSessionChat";

function msg(i: number, text = `turn ${i}`): HistoryMessageDto {
  return { role: i % 2 === 0 ? "User" : "Assistant", parts: [{ kind: "Text", text }] };
}

function seed(sid: string, count: number, generation = "g1", staleNotice: string | null = null): void {
  gw.state.sessions.set(sid, { generation, messages: Array.from({ length: count }, (_, i) => msg(i)), staleNotice });
}

async function poll(): Promise<void> {
  await act(async () => {
    vi.advanceTimersByTime(2500);
  });
  await act(async () => {
    for (let i = 0; i < 6; i++) await Promise.resolve();
  });
}

async function settle(): Promise<void> {
  await act(async () => {
    for (let i = 0; i < 6; i++) await Promise.resolve();
  });
}

function expectSameAsFullRead(bubbles: unknown, sid: string): void {
  const expected = renderChatHistory(fullDto(sid), loadChatFilter()).bubbles;
  expect(bubbles).toEqual(expected);
}

beforeEach(() => {
  vi.useFakeTimers();
  gw.state = { sessions: new Map(), cursorsSent: [], override: null };
  Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "visible" });
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("useSessionChat with the conversation tail", () => {
  it("asks with its cursor after the first read, and renders exactly what a full read renders", async () => {
    seed("s", 3);
    const { result } = renderHook(() => useSessionChat("s"));
    await settle();
    expect(gw.state.cursorsSent).toEqual([""]);
    expectSameAsFullRead(result.current.bubbles, "s");

    for (const grow of [1, 0, 4, 2]) {
      const s = gw.state.sessions.get("s")!;
      s.messages = [...s.messages, ...Array.from({ length: grow }, (_, i) => msg(s.messages.length + i))];
      await poll();
      expectSameAsFullRead(result.current.bubbles, "s");
    }
    // Every read after the first carried a cursor the Gateway honoured.
    expect(gw.state.cursorsSent.slice(1).every((c) => c.length > 0)).toBe(true);
    expect(result.current.bubbles).toHaveLength(10);
  });

  it("follows a generation switch with a full answer, never mixing the two conversations", async () => {
    seed("s", 4, "g1");
    const { result } = renderHook(() => useSessionChat("s"));
    await settle();

    gw.state.sessions.set("s", { generation: "g2", messages: [msg(0, "after /clear")], staleNotice: null });
    await poll();

    expectSameAsFullRead(result.current.bubbles, "s");
    expect(result.current.bubbles).toHaveLength(1);
  });

  it("carries the new answer's stale notice with a tail", async () => {
    seed("s", 2);
    const { result } = renderHook(() => useSessionChat("s"));
    await settle();
    expect(result.current.staleNotice).toBeNull();

    gw.state.sessions.get("s")!.staleNotice = "This session's computer has not checked in recently.";
    await poll();

    expect(result.current.staleNotice).toBe("This session's computer has not checked in recently.");
  });

  it("refuses a tail that does not fit what it holds, and reads the whole conversation instead", async () => {
    seed("s", 3);
    const { result } = renderHook(() => useSessionChat("s"));
    await settle();

    // A misbehaving answer: claims the client holds 2 (it holds 3) - stitching it on would duplicate a turn.
    gw.state.sessions.get("s")!.messages.push(msg(3));
    gw.state.override = { ...fullDto("s"), messages: fullDto("s").messages.slice(2), tailFrom: 2, cursor: "bogus" };
    await poll();

    expect(gw.state.cursorsSent.at(-1)).toBe("");
    expectSameAsFullRead(result.current.bubbles, "s");
    expect(result.current.bubbles).toHaveLength(4);
  });

  it("starts from a full read when the session on screen changes", async () => {
    seed("a", 3);
    seed("b", 2);
    const { result, rerender } = renderHook(({ sid }) => useSessionChat(sid), { initialProps: { sid: "a" } });
    await settle();

    rerender({ sid: "b" });
    await settle();

    expect(gw.state.cursorsSent.at(-1)).toBe("");
    expectSameAsFullRead(result.current.bubbles, "b");
  });
});
