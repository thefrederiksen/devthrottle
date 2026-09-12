// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act, cleanup, waitFor } from "@testing-library/react";

// The phone's stop verb, at the hook. The app bar's own tests cover the sheet; what is pinned HERE is
// the part that is issue internal#1992: the phone collects NO reason from the owner - the requirement
// binds to agent keys, not to the owner's hand - so the hook itself sends the derived sentence the
// audit trail needs, and the Gateway's contract never sees an owner's stop without one.
//
// The single-in-flight guard and the carrying of the Gateway's own failure sentence to `error` are
// pinned here too, because they belong to the verb and not to any one sheet.

const stopSessionMock = vi.fn();
vi.mock("@devthrottle/client-core/api/client", () => ({
  holdSession: () => Promise.resolve({ onHold: false, pending: false }),
  listSessions: () => Promise.resolve([]),
  stopSession: (...args: unknown[]) => stopSessionMock(...args),
}));

import { useSessionManage } from "./useSessionManage";

// The one sentence the phone records with every stop it sends. A trail reader sees what happened and
// where from; the owner is not asked to justify his own tap.
const DERIVED_REASON = "Stopped by the owner from the mobile app";

const outcome = {
  verdict: "stopped",
  headline: "stopped 9c41e7a2 - process 51884 ended, row removed",
  details: [] as string[],
  sessionId: "9c41e7a2",
  shortId: "9c41e7a2",
  processId: 51884,
  processEnded: true,
  rowRemoved: true,
  worktreePath: null,
  worktreeHadUncommittedChanges: null,
  reason: DERIVED_REASON,
  stoppedBy: "device 4f10",
  killed: true,
  removed: true,
};

beforeEach(() => {
  stopSessionMock.mockReset();
});

afterEach(() => {
  cleanup();
});

describe("useSessionManage's stop verb", () => {
  it("sends the session id and the DERIVED reason to the ONE shared function", async () => {
    stopSessionMock.mockResolvedValue(outcome);
    const { result } = renderHook(() => useSessionManage("9c41e7a2"));

    // No reason is collected from the caller at all: the verb takes nothing and asks nothing.
    await act(async () => {
      await result.current.stopSession();
    });

    expect(stopSessionMock).toHaveBeenCalledWith("9c41e7a2", DERIVED_REASON);
  });

  it("resolves with the Gateway's answer, so a successful stop can be left silently", async () => {
    stopSessionMock.mockResolvedValue(outcome);
    const { result } = renderHook(() => useSessionManage("9c41e7a2"));

    let answered: { headline: string } | null = null;
    await act(async () => {
      answered = await result.current.stopSession();
    });

    expect(answered).not.toBeNull();
    expect(answered!.headline).toBe("stopped 9c41e7a2 - process 51884 ended, row removed");
  });

  it("sends ONE stop at a time - a second call while the first is outstanding never reaches the Gateway",
    async () => {
      let release: (value: unknown) => void = () => {};
      stopSessionMock.mockReturnValue(new Promise((resolve) => { release = resolve; }));
      const { result } = renderHook(() => useSessionManage("9c41e7a2"));

      let first: Promise<unknown> | undefined;
      act(() => {
        first = result.current.stopSession();
      });

      // Asserted WITHOUT awaiting the second call. A test that awaits it deadlocks under its own
      // mutation - with the guard gone, the second call waits on the same outstanding answer the
      // first is waiting on - and a test that hangs is a test that cannot go red.
      await act(async () => {
        await expect(result.current.stopSession()).rejects.toThrow(/already on its way/);
      });
      expect(stopSessionMock).toHaveBeenCalledTimes(1);

      await act(async () => {
        release(outcome);
        await first;
      });
      expect(stopSessionMock).toHaveBeenCalledTimes(1);
    });

  it("surfaces the Gateway's own sentence on the shared error and rethrows", async () => {
    stopSessionMock.mockRejectedValue(new Error("the Director on SORENLAPTOP could not be reached"));
    const { result } = renderHook(() => useSessionManage("9c41e7a2"));

    await act(async () => {
      await expect(result.current.stopSession()).rejects.toThrow();
    });

    await waitFor(() =>
      expect(result.current.error).toBe("the Director on SORENLAPTOP could not be reached"),
    );
  });
});
