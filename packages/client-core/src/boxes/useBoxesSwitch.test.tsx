import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { GatewayError } from "../api/client";
import type { AccountStatus } from "../account/accountClient";

// Whether Factory Boxes is on (contract section 5): the Gateway's `boxes` on GET /account/status, read once per page
// load. On only on true; a failed read is neither on nor off, is reported, and is asked again on the shell's rhythm.

const read = vi.hoisted(() => ({ queue: [] as Array<() => Promise<AccountStatus>>, calls: 0 }));
vi.mock("../account/accountClient", () => ({
  getAccountStatus: vi.fn(() => {
    read.calls += 1;
    const next = read.queue.shift();
    if (next === undefined) return new Promise<AccountStatus>(() => undefined);
    return next();
  }),
}));

vi.mock("../errors/reportClientError", async (importActual) => ({
  ...(await importActual<typeof import("../errors/reportClientError")>()),
  reportClientError: vi.fn(),
}));

import { reportClientError } from "../errors/reportClientError";
import { resetBoxesSwitchCache, useBoxesSwitch } from "./useBoxesSwitch";

function Probe() {
  const answer = useBoxesSwitch();
  return (
    <div data-testid="switch">
      {answer.state}
      {answer.state === "failed" ? `:${answer.message}` : ""}
    </div>
  );
}

const ON: AccountStatus = { signedIn: true, boxes: true };
const OFF: AccountStatus = { signedIn: true, boxes: false };

async function settle() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

beforeEach(() => {
  resetBoxesSwitchCache();
  read.queue = [];
  read.calls = 0;
  vi.mocked(reportClientError).mockClear();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("useBoxesSwitch", () => {
  it("useBoxesSwitch_BeforeTheGatewayAnswers_IsAsking", () => {
    render(<Probe />);

    expect(screen.getByTestId("switch").textContent).toBe("asking");
  });

  it("useBoxesSwitch_GatewaySaysTrue_IsOn", async () => {
    read.queue.push(() => Promise.resolve(ON));

    render(<Probe />);
    await settle();

    expect(screen.getByTestId("switch").textContent).toBe("on");
  });

  it("useBoxesSwitch_GatewaySaysFalse_IsOff", async () => {
    read.queue.push(() => Promise.resolve(OFF));

    render(<Probe />);
    await settle();

    expect(screen.getByTestId("switch").textContent).toBe("off");
    expect(reportClientError).not.toHaveBeenCalled();
  });

  it("useBoxesSwitch_ReadFails_IsFailedWithTheGatewaysSentenceAndReportsIt", async () => {
    read.queue.push(() => Promise.reject(new GatewayError(500, "fault", { reason: "The account store is not answering." })));

    render(<Probe />);
    await settle();

    expect(screen.getByTestId("switch").textContent).toContain("failed:The account store is not answering.");
    expect(reportClientError).toHaveBeenCalledTimes(1);
    expect(vi.mocked(reportClientError).mock.calls[0][0]).toMatchObject({ surface: "boxes-switch", user_visible: false });
  });

  it("useBoxesSwitch_ReadFails_AsksAgainOnTheShellsRhythmUntilTheGatewayAnswers", async () => {
    vi.useFakeTimers();
    read.queue.push(() => Promise.reject(new TypeError("Failed to fetch")));
    read.queue.push(() => Promise.resolve(ON));

    render(<Probe />);
    await settle();
    expect(read.calls).toBe(1);

    await act(async () => {
      vi.advanceTimersByTime(14_999);
    });
    expect(read.calls).toBe(1);
    await act(async () => {
      vi.advanceTimersByTime(1);
    });
    await settle();

    expect(read.calls).toBe(2);
    expect(screen.getByTestId("switch").textContent).toBe("on");
  });

  it("useBoxesSwitch_SettledAnswer_IsNotAskedAgainWhenMountedAgain", async () => {
    read.queue.push(() => Promise.resolve(ON));
    const first = render(<Probe />);
    await settle();
    first.unmount();

    render(<Probe />);
    await settle();

    expect(screen.getByTestId("switch").textContent).toBe("on");
    expect(read.calls).toBe(1);
  });

  it("useBoxesSwitch_Unmounted_StopsAskingAgain", async () => {
    vi.useFakeTimers();
    read.queue.push(() => Promise.reject(new GatewayError(500, "fault")));
    const view = render(<Probe />);
    await settle();

    view.unmount();
    await act(async () => {
      vi.advanceTimersByTime(120_000);
    });

    expect(read.calls).toBe(1);
  });
});
