// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter, Outlet, useLocation, useRoutes } from "react-router-dom";

// The Cockpit opens on the Fleet Manager (the Fleet Manager mission, step 6), and the Assistant's old address lands
// there too. The REAL route table the app mounts is driven here (COCKPIT_ROUTES), so the sign-in gate and the shell
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
vi.mock("./fleet/FleetMapView", () => ({ FleetMapView: () => <div>fleet map page</div> }));
vi.mock("./fleetmanager/WalkthroughView", () => ({ WalkthroughView: () => <div>walkthrough page</div> }));

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

  it("opens on the Fleet Manager", async () => {
    renderAt("/");

    expect(await screen.findByText("fleet manager page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/fleet-manager");
  });

  it("sends the Assistant's old address to the Fleet Manager", async () => {
    renderAt("/assistant");

    expect(await screen.findByText("fleet manager page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/fleet-manager");
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
