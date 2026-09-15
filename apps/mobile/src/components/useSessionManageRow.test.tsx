// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { renderHook, cleanup, waitFor } from "@testing-library/react";

// THE ROW IS KEYED BY THE ROUTE (the Wingman-on-every-turn mission, slice E, inspection finding 2). The session screen
// answers a verdict off the row this hook hands over, so a row remembered across a route change is session A's answer
// buttons on session B's screen. Pinned HERE, at the hook, and separately from the Chat screen's test, because the
// shared panel refuses a row for another session too: a regression in the hook alone would hide behind the panel.

let rosterRead: () => Promise<unknown[]> = () => Promise.resolve([]);

vi.mock("@devthrottle/client-core/api/client", () => ({
  holdSession: () => Promise.resolve({ onHold: false, pending: false }),
  listSessions: () => rosterRead(),
  stopSession: () => Promise.resolve(null),
}));

import { useSessionManage } from "./useSessionManage";

const A = "6c1f0a44-0000-4000-8000-00000000000a";
const B = "6c1f0a44-0000-4000-8000-00000000000b";

function row(sessionId: string) {
  return { sessionId, activityState: "WaitingForInput", effectiveColor: "red", stateLabel: "Needs you", triageBucket: "needsYou" };
}

afterEach(() => {
  cleanup();
  rosterRead = () => Promise.resolve([]);
});

describe("useSessionManage's row", () => {
  it("hands over no row for B while B's read is pending, though A's row was read a moment ago", async () => {
    rosterRead = () => Promise.resolve([row(A), row(B)]);
    const { result, rerender } = renderHook(({ sid }) => useSessionManage(sid), { initialProps: { sid: A } });
    await waitFor(() => expect(result.current.session?.sessionId).toBe(A));

    rosterRead = () => new Promise(() => {});
    rerender({ sid: B });

    expect(result.current.session).toBeNull();
    expect(result.current.sessionProblem).toBeNull();
  });

  it("drops the row and gives the reason when a read fails", async () => {
    rosterRead = () => Promise.resolve([row(A)]);
    const { result } = renderHook(() => useSessionManage(A));
    await waitFor(() => expect(result.current.session?.sessionId).toBe(A));

    rosterRead = () => Promise.reject(new Error("The Gateway did not answer."));
    const { result: next } = renderHook(() => useSessionManage(A));

    await waitFor(() => expect(next.current.sessionProblem).toMatch(/Could not read the roster.*The Gateway did not answer\./));
    expect(next.current.session).toBeNull();
  });

  it("hands over no row and gives the reason when the roster does not hold the session", async () => {
    rosterRead = () => Promise.resolve([row(A)]);
    const { result } = renderHook(() => useSessionManage(B));

    await waitFor(() =>
      expect(result.current.sessionProblem).toBe("This session is not on the roster right now, so there is nothing here to answer."),
    );
    expect(result.current.session).toBeNull();
  });
});
