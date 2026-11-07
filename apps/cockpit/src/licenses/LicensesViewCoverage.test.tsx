// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, within } from "@testing-library/react";

// THE COVERAGE GRID IS DRAWN AS THE GATEWAY FOLDED IT (mission "Licenses - Always Ready, Loud When Not", step 1).
// Every word and every colour comes from the answer: a cell is yellow because the Gateway says "warning", red because it
// says "bad" - so a test that changes only the tone in the answer must change only the colour on the page.
const getLicenseOverview = vi.fn();
const getLicenseForecast = vi.fn();
const getLicenseLog = vi.fn();
const getLicenseApprovers = vi.fn();
vi.mock("@devthrottle/client-core/licenses/licensesClient", () => ({
  getLicenseAlarms: async () => ({ count: 0, summary: null, linkText: null, alarms: [], tone: null }),
  getLicenseOverview: (...args: unknown[]) => getLicenseOverview(...args),
  getLicenseForecast: (...args: unknown[]) => getLicenseForecast(...args),
  getLicenseLog: (...args: unknown[]) => getLicenseLog(...args),
  switchLicenseNow: vi.fn(),
  setLicenseRenewalDate: vi.fn(),
  getLicenseApprovers: (...args: unknown[]) => getLicenseApprovers(...args),
  // The fire drill card is not what this file is about: it reads the Gateway's "not set up" example.
  getLicenseFireDrill: async () => (await import("./fixtures")).FIRE_DRILL_NOT_SET_UP,
}));
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport: (_surface: string, _action: string, err: unknown) => (err instanceof Error ? err.message : String(err)),
  backgroundRecovered: () => undefined,
}));

import { LicensesView } from "./LicensesView";
import { APPROVERS_EMPTY, FORECAST, LOG_PAGE, OVERVIEW, copyOf } from "./fixtures";

beforeEach(() => {
  getLicenseForecast.mockResolvedValue(copyOf(FORECAST));
  getLicenseLog.mockResolvedValue(copyOf(LOG_PAGE));
  getLicenseApprovers.mockResolvedValue(copyOf(APPROVERS_EMPTY));
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("the coverage grid", () => {
  it("draws one column per paid account and one row per machine, every word as the Gateway gave it", async () => {
    getLicenseOverview.mockResolvedValue(copyOf(OVERVIEW));
    render(<LicensesView />);

    const grid = await screen.findByTestId("lic-coverage-grid");
    expect(within(grid).getAllByTestId("lic-coverage-account").map((th) => th.textContent)).toEqual(
      OVERVIEW.coverage.accounts.map((a) => a.accountEmail + a.planText),
    );
    const rows = within(grid).getAllByTestId("lic-coverage-row");
    expect(rows).toHaveLength(OVERVIEW.coverage.rows.length);
    OVERVIEW.coverage.rows.forEach((row, index) => {
      expect(within(rows[index]).getByText(row.machine.label)).toBeTruthy();
      expect(within(rows[index]).getByTestId("lic-coverage-row-text").textContent).toBe(row.text);
      const cells = within(rows[index]).getAllByTestId("lic-coverage-cell");
      expect(cells.map((c) => c.textContent)).toEqual(row.cells.map((c) => c.text));
      expect(cells.map((c) => c.querySelector(".lic-pill")?.className)).toEqual(row.cells.map((c) => `lic-pill lic-tone-${c.tone}`));
    });
    expect(screen.getByTestId("lic-coverage-summary").textContent).toBe(OVERVIEW.coverage.text);
  });

  it("paints a cell red only when the Gateway says bad", async () => {
    const overview = copyOf(OVERVIEW);
    const gap = overview.coverage.rows[0].cells.find((c) => c.isGap);
    if (gap === undefined) throw new Error("the example overview has no gap");
    gap.tone = "bad";
    getLicenseOverview.mockResolvedValue(overview);
    render(<LicensesView />);

    const grid = await screen.findByTestId("lic-coverage-grid");
    const pills = within(grid).getAllByTestId("lic-coverage-cell").map((c) => c.querySelector(".lic-pill")?.className);
    expect(pills.filter((c) => c === "lic-pill lic-tone-bad")).toHaveLength(1);
  });
});
