// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The phone's About page shows the account's real Director and session counts (Web UI Polish phase 3). They come
// from GET /fleet/counts, the signed-in read; the public /healthz has no account, so on the hosted Gateway it leaves
// its counts out and this page used to say "Not reported" for both.

const { getGatewayHealth, getFleetCounts } = vi.hoisted(() => ({ getGatewayHealth: vi.fn(), getFleetCounts: vi.fn() }));

vi.mock("@devthrottle/client-core/api/client", () => ({ getGatewayHealth, getFleetCounts }));
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  backgroundRecovered: vi.fn(),
  describeAndReport: (_surface: string, action: string) => `Could not ${action}.`,
}));
vi.mock("../components/AppBarAlerts", () => ({ AppBarAlerts: () => null }));

import { About, formatCount } from "./About";

function row(label: string): string {
  const name = screen.getByText(label);
  return name.parentElement!.querySelector(".about-value")!.textContent ?? "";
}

beforeEach(() => {
  vi.stubGlobal("__MOBILE_COMMIT__", "abc1234");
  vi.stubGlobal("__MOBILE_BUILD_TIME__", "2026-11-08T12:00:00Z");
  // jsdom has no matchMedia; About asks it whether the app runs installed.
  vi.stubGlobal("matchMedia", () => ({ matches: false }));
  // The hosted /healthz: liveness and version, and no counts.
  getGatewayHealth.mockResolvedValue({ status: "ok", version: "2.40.0+abcdef0123", serverTime: "2026-11-08T12:00:00Z" });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  getGatewayHealth.mockReset();
  getFleetCounts.mockReset();
});

describe("About", () => {
  it("About_SignedIn_ShowsTheAccountsDirectorAndSessionCounts", async () => {
    getFleetCounts.mockResolvedValue({ directors: 3, sessions: 17 });

    render(<MemoryRouter><About /></MemoryRouter>);

    await screen.findByText("17");
    expect(row("Directors")).toBe("3");
    expect(row("Sessions")).toBe("17");
    expect(screen.queryByText("Not reported")).toBeNull();
  });

  it("About_ZeroDirectors_ShowsZeroNotAPlaceholder", async () => {
    getFleetCounts.mockResolvedValue({ directors: 0, sessions: 0 });

    render(<MemoryRouter><About /></MemoryRouter>);

    await screen.findAllByText("0");
    expect(row("Directors")).toBe("0");
    expect(row("Sessions")).toBe("0");
  });

  it("About_CountsReadFails_SaysSoAndShowsTheReason", async () => {
    getFleetCounts.mockRejectedValue(new Error("403"));

    render(<MemoryRouter><About /></MemoryRouter>);

    await screen.findByText("Could not read how many Directors and sessions you have.");
    expect(row("Directors")).toBe("Could not read");
    expect(row("Sessions")).toBe("Could not read");
  });
});

describe("formatCount", () => {
  it("formatCount_NumberArrived_ReturnsIt", () => {
    expect(formatCount(4, null)).toBe("4");
  });

  it("formatCount_NothingYet_SaysLoading", () => {
    expect(formatCount(undefined, null)).toBe("Loading...");
  });

  it("formatCount_ReadFailed_SaysCouldNotRead", () => {
    expect(formatCount(undefined, "Could not read.")).toBe("Could not read");
  });
});
