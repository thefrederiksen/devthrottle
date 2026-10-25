// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";

// Alerts in the Cockpit (the Alerts mission, Phase 4): the browser tab's title carries the count of alerts that need an
// answer and loses it at zero; a notification's deep link (?alert={id}) opens the tray on that alert; closing the tray
// takes the link out of the address; and the tray is placed at the bell's corner, through the position variables only.

const { listAlerts } = vi.hoisted(() => ({ listAlerts: vi.fn() }));

vi.mock("@devthrottle/client-core/alerts/alertsClient", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/alerts/alertsClient");
  return { ...actual, listAlerts, markAlertsSeen: vi.fn(async () => {}) };
});

import { AlertsProvider } from "@devthrottle/client-core/alerts/AlertsProvider";
import { messageAlert, restartAlert } from "@devthrottle/client-core/alerts/alertsFixtures.testkit";
import { AlertsTitleCount, RailAlertsBell } from "./CockpitAlerts";
import { COCKPIT_TITLE, composeTitle, setPageTitle } from "./documentTitle";

function LocationProbe() {
  const location = useLocation();
  return <span data-testid="location">{location.pathname + location.search}</span>;
}

function renderRail(entry = "/sessions") {
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <AlertsProvider>
        <AlertsTitleCount />
        <RailAlertsBell />
        <LocationProbe />
      </AlertsProvider>
    </MemoryRouter>,
  );
}

describe("Alerts in the Cockpit", () => {
  beforeEach(() => setPageTitle(COCKPIT_TITLE));
  afterEach(() => {
    cleanup();
    listAlerts.mockReset();
  });

  it("composeTitle_PutsTheCountInFront_AndNothingAtZero", () => {
    expect(composeTitle("DevThrottle Cockpit", 2)).toBe("(2) DevThrottle Cockpit");
    expect(composeTitle("DevThrottle Cockpit", 0)).toBe("DevThrottle Cockpit");
  });

  it("TabTitle_CarriesTheGatewaysCount_AndKeepsThePagesOwnName", async () => {
    listAlerts.mockResolvedValue({ alerts: [restartAlert(), messageAlert()], needsAnswerCount: 2 });
    renderRail();
    await waitFor(() => expect(document.title).toBe("(2) DevThrottle Cockpit"));

    // A page that names itself keeps the count in front of its own name.
    setPageTitle("Mine - DevThrottle Cockpit");
    expect(document.title).toBe("(2) Mine - DevThrottle Cockpit");
  });

  it("TabTitle_AtZero_IsThePlainTitle", async () => {
    listAlerts.mockResolvedValue({ alerts: [], needsAnswerCount: 0 });
    renderRail();
    await waitFor(() => expect(listAlerts).toHaveBeenCalled());
    expect(document.title).toBe("DevThrottle Cockpit");
  });

  it("DeepLink_OpensTheTrayOnThatAlert_AndClosingTakesTheLinkAway", async () => {
    listAlerts.mockResolvedValue({ alerts: [restartAlert(), messageAlert({ id: "message-request:lr-1" })], needsAnswerCount: 2 });
    renderRail("/sessions?alert=message-request%3Alr-1");

    const detail = await screen.findByTestId("alert-detail");
    expect(detail.getAttribute("data-kind")).toBe("message-request");

    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
    await waitFor(() => expect(screen.getByTestId("location").textContent).toBe("/sessions"));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("OpeningTheTray_PlacesItAtTheBellsCorner_ThroughThePositionVariablesOnly", async () => {
    listAlerts.mockResolvedValue({ alerts: [restartAlert()], needsAnswerCount: 1 });
    renderRail();
    const bell = screen.getByTestId("alerts-bell");
    bell.getBoundingClientRect = () => ({ top: 16, bottom: 48, left: 12, right: 44, width: 32, height: 32, x: 12, y: 16, toJSON: () => ({}) });

    fireEvent.click(bell);

    const wrap = screen.getByTestId("rail-alerts");
    expect(wrap.style.getPropertyValue("--alerts-tray-top")).toBe("54px");
    expect(wrap.style.getPropertyValue("--alerts-tray-left")).toBe("12px");
    expect(screen.getByRole("dialog")).toBeTruthy();
  });

  // Found in the Phase 4 QA run: in a narrow window the bell sits further right than the tray is wide.
  it("OpeningTheTray_InANarrowWindow_MovesItLeftToStayInside", async () => {
    listAlerts.mockResolvedValue({ alerts: [restartAlert()], needsAnswerCount: 1 });
    const original = HTMLElement.prototype.getBoundingClientRect;
    HTMLElement.prototype.getBoundingClientRect = function (this: HTMLElement) {
      const rect = (left: number, width: number) => ({ top: 16, bottom: 48, left, right: left + width, width, height: 32, x: left, y: 16, toJSON: () => ({}) });
      if (this.classList.contains("alerts-tray")) return rect(900, 374);
      if (this.classList.contains("alerts-bell")) return rect(900, 32);
      return original.call(this);
    };
    try {
      renderRail();
      fireEvent.click(screen.getByTestId("alerts-bell"));

      const wrap = screen.getByTestId("rail-alerts");
      // A 1024px jsdom window: 1024 - 8 - 374 = 642, so the tray's right edge stops 8px inside the window.
      await waitFor(() => expect(wrap.style.getPropertyValue("--alerts-tray-left")).toBe(`${window.innerWidth - 8 - 374}px`));
    } finally {
      HTMLElement.prototype.getBoundingClientRect = original;
    }
  });
});
