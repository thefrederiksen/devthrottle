// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";

// Alerts on the phone (the Alerts mission, Phase 4): the bell sits in the roster's app bar, left of the filter button,
// with the Gateway's count; a push notification's deep link (?alert={id}) opens the tray on that alert; and closing the
// tray takes the link out of the address, so the tray cannot reopen on a link already used.

const { listAlerts } = vi.hoisted(() => ({ listAlerts: vi.fn() }));

vi.mock("@devthrottle/client-core/alerts/alertsClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/alerts/alertsClient");
  return { ...actual, listAlerts, markAlertsSeen: vi.fn(async () => {}) };
});

import { AlertsProvider } from "@devthrottle/client-core/alerts/AlertsProvider";
import { secretAlert, restartAlert } from "@devthrottle/client-core/alerts/alertsFixtures.testkit";
import { Home } from "./Home";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  listAlerts.mockReset();
});

function emptyRoster() {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (): Promise<Response> => ({
      ok: true,
      status: 200,
      headers: new Headers({ "X-Gateway-Time": new Date().toISOString() }),
      json: async () => ({ sessions: [], machineErrors: [], directors: [], unreachableBanner: null }),
    }) as Response),
  );
}

function LocationProbe() {
  const location = useLocation();
  return <span data-testid="location">{location.pathname + location.search}</span>;
}

function renderHome(entry: string) {
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <AlertsProvider>
        <Home />
        <LocationProbe />
      </AlertsProvider>
    </MemoryRouter>,
  );
}

describe("Alerts on the phone's roster", () => {
  it("Home_AppBar_HasTheBell_LeftOfTheFilterButton_WithTheGatewaysCount", async () => {
    emptyRoster();
    listAlerts.mockResolvedValue({ alerts: [restartAlert(), secretAlert()], needsAnswerCount: 2 });
    renderHome("/");

    await waitFor(() => expect(screen.getByTestId("alerts-badge").textContent).toBe("2"));
    const bar = document.querySelector(".app-bar");
    const bell = screen.getByTestId("alerts-bell");
    const filter = screen.getByRole("button", { name: "Filter sessions" });
    expect(bar?.contains(bell)).toBe(true);
    // The bell comes right before the filter button in the bar.
    expect(bell.closest(".alerts")?.nextElementSibling).toBe(filter);
    expect(bell.getAttribute("aria-label")).toBe("Alerts, 2 need you");
  });

  it("Home_DeepLink_OpensTheTrayOnThatAlert_AndClosingTakesTheLinkAway", async () => {
    emptyRoster();
    listAlerts.mockResolvedValue({ alerts: [restartAlert(), secretAlert({ id: "secret-transfer:tr-1" })], needsAnswerCount: 2 });
    renderHome("/?alert=secret-transfer%3Atr-1");

    const detail = await screen.findByTestId("alert-detail");
    expect(detail.getAttribute("data-kind")).toBe("secret-transfer");

    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });

    await waitFor(() => expect(screen.getByTestId("location").textContent).toBe("/"));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("Home_TheSessionList_DrawsNoRequestCard_EvenWithAlertsWaiting", async () => {
    emptyRoster();
    listAlerts.mockResolvedValue({ alerts: [restartAlert(), secretAlert()], needsAnswerCount: 2 });
    renderHome("/");

    await waitFor(() => expect(screen.getByTestId("alerts-badge").textContent).toBe("2"));
    expect(document.querySelector(".restart-request, .secret-transfer, .link-request, [data-testid=alert-detail]")).toBeNull();
  });
});
