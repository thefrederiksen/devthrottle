// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { renderHook, act, waitFor, cleanup } from "@testing-library/react";

// The hook's half of the phone's rename (mission "Mobile Rename"). The sheet's half is pinned in
// sessionAppBarRename.test.tsx; what is pinned HERE is the write path itself:
//
//   * a SUCCESS adopts the Gateway's APPLIED name on the row at once - the title renders the row, so
//     the rename must not wait for the next poll - and triggers an immediate roster re-sync;
//   * a FAILURE surfaces the Gateway's own sentence on `error` and keeps the row exactly as it was;
//   * a second rename while one is outstanding is refused, for the same reason as the stop's
//     in-flight guard: two answers must not land on top of each other.

let rosterRead: () => Promise<unknown[]> = () => Promise.resolve([]);
const renameWrite = vi.fn();

vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  // The error reporter (errors/reportClientError) needs the real error helpers; the rest is faked.
  ...(({ GatewayError, gatewayErrorMessage, authHeaders }) => ({ GatewayError, gatewayErrorMessage, authHeaders }))(
    await importOriginal<typeof import("@devthrottle/client-core/api/client")>(),
  ),
  holdSession: () => Promise.resolve({ onHold: false, pending: false }),
  listSessions: () => rosterRead(),
  stopSession: () => Promise.resolve(null),
}));

vi.mock("@devthrottle/client-core/fleet/fleetClient", () => ({
  renameSession: (sid: string, name: string) => renameWrite(sid, name),
}));

import { useSessionManage } from "./useSessionManage";

const A = "6c1f0a44-0000-4000-8000-00000000000a";

function row(name: string) {
  return { sessionId: A, name, activityState: "WaitingForInput", effectiveColor: "red", stateLabel: "Needs you", triageBucket: "needsYou" };
}

afterEach(() => {
  cleanup();
  rosterRead = () => Promise.resolve([]);
  renameWrite.mockReset();
});

describe("useSessionManage's rename", () => {
  it("adopts the Gateway's applied name on the row at once and re-syncs the roster", async () => {
    // The roster answers with the OLD name until the rename lands, then with the applied one - the
    // re-sync the rename triggers is what must bring the fresh row in.
    let applied = false;
    rosterRead = () => Promise.resolve([row(applied ? "Mobile Rename - Worker - menu" : "Bad spawn name")]);
    renameWrite.mockImplementationOnce(async (_sid: string, name: string) => {
      applied = true;
      return { sessionId: A, name };
    });
    const { result } = renderHook(() => useSessionManage(A));
    await waitFor(() => expect(result.current.session?.name).toBe("Bad spawn name"));

    let ok: boolean | undefined;
    await act(async () => { ok = await result.current.rename("Mobile Rename - Worker - menu"); });

    expect(ok).toBe(true);
    expect(result.current.error).toBeNull();
    // The title renders the row's name - so the rename lands on the same tick the Gateway answered,
    // not on the next poll.
    expect(result.current.session?.name).toBe("Mobile Rename - Worker - menu");
    expect(result.current.session?.sessionId).toBe(A);
  });

  it("keeps the row and surfaces the Gateway's sentence on failure", async () => {
    rosterRead = () => Promise.resolve([row("Old name")]);
    renameWrite.mockRejectedValueOnce(new Error("The Director on SORENLAPTOP could not be reached."));
    const { result } = renderHook(() => useSessionManage(A));
    await waitFor(() => expect(result.current.session?.name).toBe("Old name"));

    let ok: boolean | undefined;
    await act(async () => { ok = await result.current.rename("Never applied"); });

    expect(ok).toBe(false);
    expect(result.current.session?.name).toBe("Old name");
    expect(result.current.error).toContain("SORENLAPTOP");
  });

  it("refuses a second rename while the first is still outstanding", async () => {
    rosterRead = () => Promise.resolve([row("Old name")]);
    let release: () => void = () => {};
    renameWrite.mockImplementationOnce(() => new Promise((resolve) => { release = () => resolve({ sessionId: A, name: "First" }); }));
    const { result } = renderHook(() => useSessionManage(A));
    await waitFor(() => expect(result.current.session?.name).toBe("Old name"));

    let second: boolean | undefined;
    await act(async () => {
      void result.current.rename("First");
      second = await result.current.rename("Second");
      release();
    });

    expect(second).toBe(false);
    expect(renameWrite).toHaveBeenCalledTimes(1);
  });
});
