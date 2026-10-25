// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";

// Alerts on the phone (the Alerts mission, Phases 4 and 5): the bell sits in the roster's app bar, left of the filter
// button, with the Gateway's count; pressing it opens the Alerts screen; a push notification's deep link
// (?alert={id}) lands on the Alerts screen with that alert, in place of the roster's address, and Back returns to the
// roster without the link, so it cannot be used twice.

const { listAlerts } = vi.hoisted(() => ({ listAlerts: vi.fn() }));

vi.mock("@devthrottle/client-core/alerts/alertsClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/alerts/alertsClient");
  return { ...actual, listAlerts, markAlertsSeen: vi.fn(async () => {}) };
});

import { AlertsProvider } from "@devthrottle/client-core/alerts/AlertsProvider";
import { secretAlert, restartAlert } from "@devthrottle/client-core/alerts/alertsFixtures.testkit";
import { Home } from "./Home";
import { Alerts } from "./Alerts";

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
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/alerts" element={<Alerts />} />
        </Routes>
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

  it("Home_DeepLink_LandsOnTheAlertsScreenOnThatAlert_AndBackReturnsWithoutTheLink", async () => {
    emptyRoster();
    listAlerts.mockResolvedValue({ alerts: [restartAlert(), secretAlert({ id: "secret-transfer:tr-1" })], needsAnswerCount: 2 });
    renderHome("/?alert=secret-transfer%3Atr-1");

    await waitFor(() => expect(screen.getByTestId("location").textContent).toBe("/alerts?alert=secret-transfer%3Atr-1"));
    expect(screen.getByRole("heading", { level: 1, name: "Alerts" })).toBeTruthy();
    await waitFor(() => expect((document.activeElement as HTMLElement | null)?.getAttribute("data-alert-id")).toBe("secret-transfer:tr-1"));
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Back" }));
    await waitFor(() => expect(screen.getByTestId("location").textContent).toBe("/"));
    expect(await screen.findByRole("button", { name: "Filter sessions" })).toBeTruthy();
  });

  it("Home_Bell_OpensTheAlertsScreen_AndBackPutsFocusOnTheBellAgain", async () => {
    emptyRoster();
    listAlerts.mockResolvedValue({ alerts: [restartAlert()], needsAnswerCount: 1 });
    renderHome("/");

    fireEvent.click(await screen.findByTestId("alerts-bell"));
    await waitFor(() => expect(screen.getByTestId("location").textContent).toBe("/alerts"));
    expect(document.activeElement).toBe(screen.getByRole("heading", { level: 1, name: "Alerts" }));
    expect(await screen.findByTestId("alert-card")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Back" }));
    await waitFor(() => expect(screen.getByTestId("location").textContent).toBe("/"));
    await waitFor(() => expect(document.activeElement).toBe(screen.getByTestId("alerts-bell")));
  });

  it("Home_TheSessionList_DrawsNoRequestCard_EvenWithAlertsWaiting", async () => {
    emptyRoster();
    listAlerts.mockResolvedValue({ alerts: [restartAlert(), secretAlert()], needsAnswerCount: 2 });
    renderHome("/");

    await waitFor(() => expect(screen.getByTestId("alerts-badge").textContent).toBe("2"));
    expect(document.querySelector(".restart-request, .secret-transfer, .link-request, [data-testid=alert-detail]")).toBeNull();
  });
});
