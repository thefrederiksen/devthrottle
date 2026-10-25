// @vitest-environment jsdom
import { readdirSync, readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";

// The bell on every phone screen that has the app bar (the Alerts mission, section 5.4). Two things are proved here:
// every app bar the phone source draws carries the ONE shared AppBarAlerts (so a new screen cannot quietly ship without
// it), and the shared bell does on any screen what it does on the roster - the Gateway's count, and a deep link that
// opens the tray and leaves the address when the tray closes.

const { listAlerts } = vi.hoisted(() => ({ listAlerts: vi.fn() }));

vi.mock("@devthrottle/client-core/alerts/alertsClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/alerts/alertsClient");
  return { ...actual, listAlerts, markAlertsSeen: vi.fn(async () => {}) };
});

import { AlertsProvider } from "@devthrottle/client-core/alerts/AlertsProvider";
import { messageAlert, restartAlert } from "@devthrottle/client-core/alerts/alertsFixtures.testkit";
import { AppBarAlerts } from "./AppBarAlerts";

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
});

describe("the bell in every phone app bar", () => {
  it("EveryAppBar_InThePhoneSource_CarriesTheSharedBell", () => {
    const bars = sourceFiles(SRC).flatMap((file) => appBarsIn(readFileSync(file, "utf8")).map((bar) => ({ file: relative(SRC, file), bar })));
    // A presence, never only an absence: the sweep must have found the app bars it exists to check.
    expect(bars.length).toBeGreaterThanOrEqual(15);
    expect(bars.filter((b) => !b.bar.includes("<AppBarAlerts />")).map((b) => b.file)).toEqual([]);
  });

  it("appBarsIn_FindsEachBar_AndSeesOneWithoutTheBell", () => {
    const text = `<header className="app-bar"><h1>A</h1><AppBarAlerts /></header>
      <header className="app-bar session-app-bar"><h1>B</h1></header>`;
    const bars = appBarsIn(text);
    expect(bars).toHaveLength(2);
    expect(bars.filter((b) => !b.includes("<AppBarAlerts />"))).toHaveLength(1);
  });

  it("OnAnotherScreen_ShowsTheCount_AndADeepLinkOpensTheTray_ClosingTakesTheLinkAway", async () => {
    listAlerts.mockResolvedValue({ alerts: [restartAlert(), messageAlert({ id: "message-request:lr-1" })], needsAnswerCount: 2 });
    render(
      <MemoryRouter initialEntries={["/settings?tab=ai&alert=message-request%3Alr-1"]}>
        <AlertsProvider>
          <AppBarAlerts />
          <LocationProbe />
        </AlertsProvider>
      </MemoryRouter>,
    );

    expect((await screen.findByTestId("alerts-badge")).textContent).toBe("2");
    const detail = await screen.findByTestId("alert-detail");
    expect(detail.getAttribute("data-kind")).toBe("message-request");

    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
    await waitFor(() => expect(screen.getByTestId("location").textContent).toBe("/settings?tab=ai"));
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});
