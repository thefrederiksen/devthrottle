// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter, Outlet, useLocation, useRoutes } from "react-router-dom";

// The Cockpit opens on Sessions (owner, 7 Oct 2026), and the Assistant's old address still lands on the Fleet
// Manager. The REAL route table the app mounts is driven here (COCKPIT_ROUTES), so the sign-in gate and the shell
// layout are the app's own; only the pages themselves are stood in for, because just where each address goes is
// under test. (A memory DATA router is not used: its navigation builds a fetch Request that jsdom's abort signal
// cannot satisfy.)

// This browser is enrolled, so the gate lets every address below through to the page it names.
vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "a-device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));
// The shell frame (rail, header, live connection) cannot run in jsdom and is not the subject; it is the layout the
// pages route into, so it stands in as that outlet.
vi.mock("./AppShell", () => ({ AppShell: () => <Outlet /> }));
vi.mock("./fleetmanager/FleetManagerView", () => ({ FleetManagerView: () => <div>fleet manager page</div> }));
vi.mock("./sessions/SessionsView", () => ({ SessionsView: () => <Outlet />, SessionsEmpty: () => <div>sessions page</div> }));
vi.mock("./fleet/FleetMapView", () => ({ FleetMapView: () => <div>fleet map page</div> }));
vi.mock("./fleetmanager/WalkthroughView", () => ({ WalkthroughView: () => <div>walkthrough page</div> }));
vi.mock("./licenses/LicensesView", () => ({ LicensesView: () => <div>licenses page</div> }));
vi.mock("./maildesk/MailDeskView", () => ({ MailDeskView: () => <div>mail desk page</div> }));
vi.mock("./boxes/BoxesView", () => ({ BoxesView: () => <div>boxes page</div> }));

import { COCKPIT_ROUTES } from "./routes";

function Shell() {
  const location = useLocation();
  const element = useRoutes(COCKPIT_ROUTES);
  return (
    <>
      <div data-testid="where">{location.pathname}</div>
      {element}
    </>
  );
}

function renderAt(path: string) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Shell />
    </MemoryRouter>,
  );
}

describe("Cockpit routes", () => {
  beforeEach(() => cleanup());

  it("opens on Sessions", async () => {
    renderAt("/");

    expect(await screen.findByText("sessions page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/sessions");
  });

  it("still serves the Fleet Manager at its own address", async () => {
    renderAt("/fleet-manager");

    expect(await screen.findByText("fleet manager page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/fleet-manager");
  });

  it("sends the Assistant's old address to the Fleet Manager", async () => {
    renderAt("/assistant");

    expect(await screen.findByText("fleet manager page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/fleet-manager");
  });

  it("serves the Licenses page at its own address", async () => {
    renderAt("/licenses");

    expect(await screen.findByText("licenses page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/licenses");
  });

  // The Mail Desk (Mail Desk mission, phase 7): the same address as on the phone, so one link opens it on either.
  it("serves the Mail Desk at its own address", async () => {
    renderAt("/mail-desk");

    expect(await screen.findByText("mail desk page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/mail-desk");
  });

  // Boxes (Factory Boxes mission, phase 1): the address exists; whether it shows the page or "Page not found" is the
  // page's own gate on the Gateway's switch (boxes/BoxesView.test.tsx).
  it("serves Boxes at its own address", async () => {
    renderAt("/boxes");

    expect(await screen.findByText("boxes page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/boxes");
  });

  it("still serves the Fleet Map at its own address", async () => {
    renderAt("/fleet-map");

    expect(await screen.findByText("fleet map page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/fleet-map");
  });

  it("serves the walkthrough at its own address, without redirecting it", async () => {
    renderAt("/fleet-manager/walkthrough");

    expect(await screen.findByText("walkthrough page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/fleet-manager/walkthrough");
  });
});
