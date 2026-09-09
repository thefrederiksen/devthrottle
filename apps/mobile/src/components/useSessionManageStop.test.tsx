// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act, cleanup, waitFor } from "@testing-library/react";

// The phone's stop verb, at the hook. The app bar's own tests cover the sheet; what is pinned HERE is the
// part of the old behaviour that was the defect: removeSession called the Gateway and then navigated to
// the roster itself, the instant the call returned, so the answer was destroyed before anyone could read
// it. The replacement hands the answer back and goes nowhere.

const stopSessionMock = vi.fn();
vi.mock("@devthrottle/client-core/api/client", () => ({
  holdSession: () => Promise.resolve({ onHold: false, pending: false }),
  listSessions: () => Promise.resolve([]),
  stopSession: (...args: unknown[]) => stopSessionMock(...args),
}));

import { useSessionManage } from "./useSessionManage";

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
  reason: "spawned into the wrong mode",
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
  it("sends the session id and the trimmed reason to the ONE shared function", async () => {
    stopSessionMock.mockResolvedValue(outcome);
    const { result } = renderHook(() => useSessionManage("9c41e7a2"));

    await act(async () => {
      await result.current.stopSession("  spawned into the wrong mode  ");
    });

    expect(stopSessionMock).toHaveBeenCalledWith("9c41e7a2", "spawned into the wrong mode");
  });

  it("resolves with the Gateway's answer instead of navigating away with it unread", async () => {
    stopSessionMock.mockResolvedValue(outcome);
    const { result } = renderHook(() => useSessionManage("9c41e7a2"));

    let answered: { headline: string } | null = null;
    await act(async () => {
      answered = await result.current.stopSession("spawned into the wrong mode");
    });

    expect(answered).not.toBeNull();
    expect(answered!.headline).toBe("stopped 9c41e7a2 - process 51884 ended, row removed");
  });

  it("never sends a reason the Gateway could only refuse", async () => {
    const { result } = renderHook(() => useSessionManage("9c41e7a2"));

    await act(async () => {
      await expect(result.current.stopSession("   ")).rejects.toThrow(/reason/);
    });

    expect(stopSessionMock).not.toHaveBeenCalled();
  });

  it("surfaces the Gateway's own sentence on the shared error and rethrows", async () => {
    stopSessionMock.mockRejectedValue(new Error("the Director on SORENLAPTOP could not be reached"));
    const { result } = renderHook(() => useSessionManage("9c41e7a2"));

    await act(async () => {
      await expect(result.current.stopSession("doing the wrong work")).rejects.toThrow();
    });

    await waitFor(() =>
      expect(result.current.error).toBe("the Director on SORENLAPTOP could not be reached"),
    );
  });
});
