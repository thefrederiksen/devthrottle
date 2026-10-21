// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";

// The Licenses page's two tabs: Accounts (the default, everything it always held) and What is using it. Switching
// shows one and not the other; the Accounts tab's own error banner is not shown on the other tab.
const getLicenseOverview = vi.fn();
vi.mock("@devthrottle/client-core/licenses/licensesClient", () => ({
  getLicenseAlarms: () => Promise.resolve({ count: 0, summary: null, linkText: null, alarms: [] }),
  getLicenseOverview: (...args: unknown[]) => getLicenseOverview(...args),
  getLicenseForecast: () => new Promise(() => undefined),
  getLicenseLog: () => new Promise(() => undefined),
  getLicenseApprovers: () => new Promise(() => undefined),
}));
vi.mock("@devthrottle/client-core/licenses/licenseUsageClient", () => ({
  getLicenseUsage: () =>
    Promise.resolve({
      asOfUtc: "2026-10-20T12:30:00Z",
      accountEmail: null,
      accountInUse: false,
      accounts: [],
      calibration: { isMeasured: false, text: "" },
      unitText: [],
      emptyText: "No machine has reported a Claude account yet, so there is nothing to measure.",
      sessions: [],
      topLevel: [],
      missions: [],
    }),
  getLicensePlan: () => new Promise(() => undefined),
  setLicenseMonthlyPrice: vi.fn(),
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_surface: string, _action: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
  backgroundRecovered: () => undefined,
}));

import { LicensesView } from "./LicensesView";

beforeEach(() => {
  getLicenseOverview.mockRejectedValue(new Error("The accounts could not be read."));
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("LicensesView tabs", () => {
  it("opens on Accounts, and What is using it shows the usage tab in its place", async () => {
    render(<LicensesView />);
    expect(await screen.findByText("The accounts could not be read.")).toBeTruthy();
    expect(screen.getByRole("tab", { name: "Accounts" }).getAttribute("aria-selected")).toBe("true");

    fireEvent.click(screen.getByRole("tab", { name: "What is using it" }));

    expect(screen.getByRole("tab", { name: "What is using it" }).getAttribute("aria-selected")).toBe("true");
    expect(await screen.findByText("No machine has reported a Claude account yet, so there is nothing to measure.")).toBeTruthy();
    expect(screen.queryByText("The accounts could not be read.")).toBeNull();
  });
});
