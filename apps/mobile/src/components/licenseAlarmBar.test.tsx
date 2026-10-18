// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";

// THE LICENSE ALARM ON THE PHONE (License Switching phase 6): the phone shell shows the Gateway's open alarms in the
// Gateway's own words, links to the Cockpit's Licenses page (the phone has none of its own), and shows nothing at all
// when no alarm is open.

const alarmRead = vi.hoisted(() => ({
  answer: { asOfUtc: "2026-10-17T08:00:00Z", count: 0, summary: null as string | null, linkText: null as string | null, alarms: [] as unknown[] },
}));
vi.mock("@devthrottle/client-core/licenses/licensesClient", () => ({
  getLicenseAlarms: vi.fn(async () => alarmRead.answer),
}));

import { LicenseAlarmBar } from "./LicenseAlarmBar";

const LINE = "machine-a holds no spare that could take it off alice@example.com. Sign machine-a in to a second account.";

describe("phone license alarm bar", () => {
  beforeEach(() => {
    cleanup();
    alarmRead.answer = { asOfUtc: "2026-10-17T08:00:00Z", count: 0, summary: null, linkText: null, alarms: [] };
  });

  it("draws no banner when no alarm is open", async () => {
    render(<LicenseAlarmBar />);
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByTestId("license-alarm-banner")).toBeNull();
  });

  it("draws the Gateway's line and a link to the Cockpit's Licenses page", async () => {
    // jsdom has no ResizeObserver; the bar measures itself with one to add its height to --topbars-h.
    vi.stubGlobal("ResizeObserver", class { observe() {} disconnect() {} });
    alarmRead.answer = {
      asOfUtc: "2026-10-17T08:00:00Z",
      count: 1,
      summary: null,
      linkText: "Open Licenses",
      alarms: [
        { alarmId: "a1", kind: "not-ready-at-80", machineName: "machine-a", accountEmail: "alice@example.com", text: LINE, openedAtUtc: "2026-10-17T07:58:00Z" },
      ],
    };
    render(<LicenseAlarmBar />);

    const banner = await screen.findByTestId("license-alarm-banner");
    expect(within(banner).getAllByTestId("license-alarm-line").map((li) => li.textContent)).toEqual([LINE]);
    const link = within(banner).getByTestId("license-alarm-link");
    expect(link.textContent).toBe("Open Licenses");
    expect(link.getAttribute("href")).toBe("/licenses");
    vi.unstubAllGlobals();
  });
});
