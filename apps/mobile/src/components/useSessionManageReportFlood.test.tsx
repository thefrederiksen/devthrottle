// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { renderHook, cleanup, act } from "@testing-library/react";

// The roster poll's problem line is a BACKGROUND read (the Error Logging mission, issue #3675): an unchanged failure
// is reported once, not on every round. The 3c review (finding 1) proved a snooze-state read that failed every round
// reported every four seconds, because the round cleared the memory before classify() threw again. This is that
// probe, kept as a test.

let rosterRead: () => Promise<unknown[]> = () => Promise.resolve([]);

vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  // The error reporter needs the real error helpers; the rest is faked.
  ...(({ GatewayError, gatewayErrorMessage, authHeaders }) => ({ GatewayError, gatewayErrorMessage, authHeaders }))(
    await importOriginal<typeof import("@devthrottle/client-core/api/client")>(),
  ),
  holdSession: () => Promise.resolve({ onHold: false, pending: false }),
  listSessions: () => rosterRead(),
  stopSession: () => Promise.resolve(null),
}));

import { useSessionManage } from "./useSessionManage";
import { resetReportingForTests, setReportingComponent } from "@devthrottle/client-core/errors/reportClientError";

const A = "6c1f0a44-0000-4000-8000-00000000000a";
// A row the Gateway did not stamp with triageBucket: classify() throws on it every round.
const UNSTAMPED = { sessionId: A, activityState: "WaitingForInput", effectiveColor: "red", stateLabel: "Needs you" };
const STAMPED = { ...UNSTAMPED, triageBucket: "needsYou" };

const fetchMock = vi.fn(async () => new Response(null, { status: 202 }));
const reports = () =>
  fetchMock.mock.calls
    .filter(([u]) => String(u) === "/client-errors")
    .map(([, init]) => JSON.parse(String((init as RequestInit).body)) as Record<string, unknown>);

async function rounds(n: number) {
  for (let i = 0; i < n; i++) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(4100);
    });
  }
}

beforeEach(() => {
  resetReportingForTests();
  setReportingComponent("mobile");
  fetchMock.mockClear();
  vi.stubGlobal("fetch", fetchMock);
  vi.useFakeTimers();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  rosterRead = () => Promise.resolve([]);
});

describe("useSessionManage's background reports", () => {
  it("reports a snooze-state read that fails every round ONCE, and again after it reads cleanly", async () => {
    rosterRead = () => Promise.resolve([UNSTAMPED]);
    const { result } = renderHook(() => useSessionManage(A));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(50);
    });
    expect(result.current.sessionProblem).toMatch(/snooze state could not be read/);
    await rounds(3);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000);
    });

    expect(reports().map((r) => r.action)).toEqual(["read the session's snooze state"]);

    // It reads cleanly, then fails again the same way: a new failure, reported again.
    rosterRead = () => Promise.resolve([STAMPED]);
    await rounds(1);
    expect(result.current.sessionProblem).toBeNull();
    rosterRead = () => Promise.resolve([UNSTAMPED]);
    await rounds(1);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000);
    });

    expect(reports()).toHaveLength(2);
  });

  it("reports a session missing from the roster once over four rounds", async () => {
    rosterRead = () => Promise.resolve([]);
    const { result } = renderHook(() => useSessionManage(A));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(50);
    });
    expect(result.current.sessionProblem).toMatch(/not on the roster/);
    await rounds(3);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1000);
    });

    expect(reports()).toHaveLength(1);
  });
});
