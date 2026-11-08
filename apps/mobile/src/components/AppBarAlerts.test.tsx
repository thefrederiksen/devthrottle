// @vitest-environment jsdom
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";

// The bell on every phone screen that has the app bar (the Alerts mission, section 5.4). Two things are proved here:
// every app bar the phone source draws carries the ONE shared AppBarAlerts (so a new screen cannot quietly ship without
// it), and the shared bell does on any screen what it does on the roster - the Gateway's count, and a deep link that
// lands on the Alerts screen on that alert. The Alerts screen itself is the one app bar without the bell: it IS Alerts.

const { listAlerts, getLicenseAlarms } = vi.hoisted(() => ({ listAlerts: vi.fn(), getLicenseAlarms: vi.fn() }));

vi.mock("@devthrottle/client-core/alerts/alertsClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/alerts/alertsClient");
  return { ...actual, listAlerts, markAlertsSeen: vi.fn(async () => {}) };
});

vi.mock("@devthrottle/client-core/licenses/licensesClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/licenses/licensesClient");
  return { ...actual, getLicenseAlarms };
});

import { AlertsProvider } from "@devthrottle/client-core/alerts/AlertsProvider";
import { LicenseAlarmsProvider } from "@devthrottle/client-core/licenses/LicenseAlarmIndicator";
import { messageAlert, restartAlert } from "@devthrottle/client-core/alerts/alertsFixtures.testkit";
import { AppBarAlerts } from "./AppBarAlerts";
import { Alerts } from "../pages/Alerts";

const SRC = join(__dirname, "..");

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
    const path = join(dir, e.name);
    if (e.isDirectory()) return sourceFiles(path);
    return e.name.endsWith(".tsx") && !e.name.endsWith(".test.tsx") ? [path] : [];
  });
}

/** Every app bar in a source file, as its text from the opening tag to the closing one. */
export function appBarsIn(text: string): string[] {
  return [...text.matchAll(/<header className="app-bar[^"]*"[\s\S]*?<\/header>/g)].map((m) => m[0]);
}

function LocationProbe() {
  const location = useLocation();
  return <span data-testid="location">{location.pathname + location.search}</span>;
}

afterEach(() => {
  cleanup();
  listAlerts.mockReset();
  getLicenseAlarms.mockReset();
});

describe("the bell in every phone app bar", () => {
  it("EveryAppBar_InThePhoneSource_CarriesTheSharedBell", () => {
    const bars = sourceFiles(SRC).flatMap((file) => appBarsIn(readFileSync(file, "utf8")).map((bar) => ({ file: relative(SRC, file), bar })));
    // A presence, never only an absence: the sweep must have found the app bars it exists to check.
    expect(bars.length).toBeGreaterThanOrEqual(15);
    const without = bars.filter((b) => !b.bar.includes("<AppBarAlerts />")).map((b) => b.file.split("\\").join("/"));
    expect(without).toEqual(["pages/Alerts.tsx"]);
  });

  it("appBarsIn_FindsEachBar_AndSeesOneWithoutTheBell", () => {
    const text = `<header className="app-bar"><h1>A</h1><AppBarAlerts /></header>
      <header className="app-bar session-app-bar"><h1>B</h1></header>`;
    const bars = appBarsIn(text);
    expect(bars).toHaveLength(2);
    expect(bars.filter((b) => !b.includes("<AppBarAlerts />"))).toHaveLength(1);
  });

  it("OnAnotherScreen_ShowsTheCount_AndADeepLinkLandsOnTheAlertsScreen_BackKeepsTheRestOfTheAddress", async () => {
    listAlerts.mockResolvedValue({ alerts: [restartAlert(), messageAlert({ id: "message-request:lr-1" })], needsAnswerCount: 2 });
    render(
      <MemoryRouter initialEntries={["/settings?tab=ai&alert=message-request%3Alr-1"]}>
        <AlertsProvider>
          <Routes>
            <Route path="/settings" element={<AppBarAlerts />} />
            <Route path="/alerts" element={<Alerts />} />
          </Routes>
          <LocationProbe />
        </AlertsProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId("location").textContent).toBe("/alerts?alert=message-request%3Alr-1"));
    await waitFor(() => expect((document.activeElement as HTMLElement | null)?.getAttribute("data-alert-id")).toBe("message-request:lr-1"));
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Back" }));
    await waitFor(() => expect(screen.getByTestId("location").textContent).toBe("/settings?tab=ai"));
    expect((await screen.findByTestId("alerts-badge")).textContent).toBe("2");
  });
});

// The license triangle (the owner, 7 November 2026) rides in the same component as the bell, so every app bar that
// carries the bell carries it too - the sweep above proves that for the triangle as well. It replaced the bar across
// the top of every screen.
describe("the license triangle beside the bell", () => {
  const NONE = { asOfUtc: "2026-11-07T08:00:00Z", count: 0, summary: null, linkText: null, alarms: [], tone: null };
  const alarm = (alarmId: string) => ({
    alarmId, kind: "not-ready-at-80", machineName: "machine-a", accountEmail: null,
    text: "machine-a is not ready. Add a spare to machine-a.", openedAtUtc: "2026-11-07T07:58:00Z", tone: "warning" as const,
  });

  function renderBar() {
    listAlerts.mockResolvedValue({ alerts: [], needsAnswerCount: 0 });
    render(
      <MemoryRouter initialEntries={["/"]}>
        <AlertsProvider>
          <LicenseAlarmsProvider enabled>
            <header className="app-bar">
              <AppBarAlerts />
            </header>
          </LicenseAlarmsProvider>
        </AlertsProvider>
      </MemoryRouter>,
    );
  }

  it("WithAlarmsOpen_ShowsTheGatewaysCount_BeforeTheBell_AndOpensLicenses", async () => {
    getLicenseAlarms.mockResolvedValue({ ...NONE, count: 3, summary: "3 license problems", linkText: "Open Licenses", alarms: [alarm("a1"), alarm("a2"), alarm("a3")], tone: "critical" });
    renderBar();

    const triangle = await screen.findByTestId("license-alarm-indicator");
    expect(screen.getByTestId("license-alarm-count").textContent).toBe("3");
    expect(triangle.getAttribute("aria-label")).toBe("3 license problems - Open Licenses");
    expect(triangle.getAttribute("href")).toBe("/licenses");
    expect(triangle.getAttribute("data-tone")).toBe("critical");
    // Beside the bell, just before it, in the app bar.
    const bell = screen.getByTestId("alerts-bell");
    expect(triangle.compareDocumentPosition(bell) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(triangle.closest(".app-bar")).not.toBeNull();
  });

  it("WithNoAlarmOpen_DrawsNothing_ButTheBell", async () => {
    getLicenseAlarms.mockResolvedValue(NONE);
    renderBar();

    await waitFor(() => expect(getLicenseAlarms).toHaveBeenCalled());
    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByTestId("license-alarm-indicator")).toBeNull();
    expect(screen.getByTestId("alerts-bell")).not.toBeNull();
  });
});
