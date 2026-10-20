import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";
import { GatewayError } from "../api/client";
import type { MailDeskAnswer } from "./mailDeskClient";
import { oddChannels } from "./mailDeskFixture.testkit";

// Whether a shell offers "Mail Desk" (docs/qa/mail-desk/phase-7/CONTRACT.md section 1, corrected after the phase
// review's F1): shown ONLY on a successful read; hidden on 404 and on every failure, which is reported and asked again
// on the shell's rhythm.

const read = vi.hoisted(() => ({ queue: [] as Array<() => Promise<MailDeskAnswer>>, calls: 0 }));
vi.mock("./mailDeskClient", () => ({
  getMailDeskChannels: vi.fn(() => {
    read.calls += 1;
    const next = read.queue.shift();
    if (next === undefined) return new Promise<MailDeskAnswer>(() => undefined);
    return next();
  }),
}));

vi.mock("../errors/reportClientError", async (importActual) => ({
  ...(await importActual<typeof import("../errors/reportClientError")>()),
  reportClientError: vi.fn(),
}));

import { reportClientError } from "../errors/reportClientError";
import { resetMailDeskEntryCache, useMailDeskEntry } from "./useMailDeskEntry";

function Probe() {
  return <div data-testid="offered">{useMailDeskEntry() ? "yes" : "no"}</div>;
}

const ON: MailDeskAnswer = { kind: "channels", envelope: oddChannels() };
const OFF: MailDeskAnswer = { kind: "off" };

async function settle() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

beforeEach(() => {
  resetMailDeskEntryCache();
  read.queue = [];
  read.calls = 0;
  vi.mocked(reportClientError).mockClear();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("useMailDeskEntry", () => {
  it("useMailDeskEntry_SwitchOn_OffersTheEntry", async () => {
    read.queue.push(() => Promise.resolve(ON));

    render(<Probe />);
    await settle();

    expect(screen.getByTestId("offered").textContent).toBe("yes");
    expect(reportClientError).not.toHaveBeenCalled();
  });

  it("useMailDeskEntry_404_HidesTheEntry", async () => {
    read.queue.push(() => Promise.resolve(OFF));

    render(<Probe />);
    await settle();

    expect(screen.getByTestId("offered").textContent).toBe("no");
    expect(reportClientError).not.toHaveBeenCalled();
  });

  it("useMailDeskEntry_BeforeTheGatewayAnswers_DoesNotOfferTheEntry", () => {
    render(<Probe />);

    expect(screen.getByTestId("offered").textContent).toBe("no");
  });

  // Contract section 1, corrected after the phase review (F1): the entry is drawn ONLY on a successful read. Every
  // failure kind leaves it hidden, so an account that is off never sees it while the phone is offline or the Gateway is
  // restarting.
  it.each([
    ["offline", () => new TypeError("Failed to fetch"), {}],
    ["a timeout", () => new DOMException("The operation timed out.", "TimeoutError"), {}],
    ["a 5xx", () => new GatewayError(503, "the Gateway is restarting"), { http_status: 503 }],
    ["a broken answer", () => new GatewayError(502, "The Gateway sent a Mail Desk list this app cannot read"), { http_status: 502 }],
  ])("useMailDeskEntry_ReadFails_%s_KeepsTheEntryHiddenAndReportsIt", async (_kind, failure, facts) => {
    read.queue.push(() => Promise.reject(failure()));

    render(<Probe />);
    await settle();

    expect(screen.getByTestId("offered").textContent).toBe("no");
    expect(reportClientError).toHaveBeenCalledTimes(1);
    expect(vi.mocked(reportClientError).mock.calls[0][0]).toMatchObject({
      surface: "mail-desk-entry",
      user_visible: false,
      ...facts,
    });
  });

  it("useMailDeskEntry_ReadFails_AsksAgainOnTheShellsRhythmAndOffersTheEntryOnlyOnceAReadSucceeds", async () => {
    vi.useFakeTimers();
    read.queue.push(() => Promise.reject(new GatewayError(500, "the store is unavailable")));
    read.queue.push(() => Promise.reject(new TypeError("Failed to fetch")));
    read.queue.push(() => Promise.resolve(ON));

    render(<Probe />);
    await settle();
    expect(screen.getByTestId("offered").textContent).toBe("no");
    expect(read.calls).toBe(1);

    // retryDelayMs: 15 seconds after the first failure, then 30.
    await act(async () => {
      vi.advanceTimersByTime(14_999);
    });
    expect(read.calls).toBe(1);
    await act(async () => {
      vi.advanceTimersByTime(1);
    });
    await settle();
    expect(read.calls).toBe(2);
    expect(reportClientError).toHaveBeenCalledTimes(2);
    expect(screen.getByTestId("offered").textContent).toBe("no");

    await act(async () => {
      vi.advanceTimersByTime(30_000);
    });
    await settle();
    expect(read.calls).toBe(3);
    // The Gateway finally answered with its list: only now is the entry drawn.
    expect(screen.getByTestId("offered").textContent).toBe("yes");
  });

  it("useMailDeskEntry_ReadSucceededThisPageLoad_ALaterFailureDoesNotTakeTheEntryAway", async () => {
    read.queue.push(() => Promise.resolve(ON));
    const first = render(<Probe />);
    await settle();
    first.unmount();
    read.queue.push(() => Promise.reject(new TypeError("Failed to fetch")));

    render(<Probe />);
    await settle();

    expect(screen.getByTestId("offered").textContent).toBe("yes");
    expect(read.calls).toBe(1);
    expect(reportClientError).not.toHaveBeenCalled();
  });

  it("useMailDeskEntry_SettledAnswer_IsNotAskedAgainWhenTheShellMountsAgain", async () => {
    read.queue.push(() => Promise.resolve(ON));
    const first = render(<Probe />);
    await settle();
    first.unmount();

    render(<Probe />);
    await settle();

    expect(screen.getByTestId("offered").textContent).toBe("yes");
    expect(read.calls).toBe(1);
  });

  it("useMailDeskEntry_Unmounted_StopsAskingAgain", async () => {
    vi.useFakeTimers();
    read.queue.push(() => Promise.reject(new GatewayError(500, "the store is unavailable")));
    const view = render(<Probe />);
    await settle();

    view.unmount();
    await act(async () => {
      vi.advanceTimersByTime(120_000);
    });

    expect(read.calls).toBe(1);
  });
});
