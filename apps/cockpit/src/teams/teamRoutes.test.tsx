// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter, Outlet, useLocation, useRoutes } from "react-router-dom";
import type { CurrentTeamState } from "@devthrottle/client-core/teams/CurrentTeam";

// The REAL route table (COCKPIT_ROUTES) with a team on screen and without one (devthrottle_internal#2304, S5). The own
// pages stand in as text and the team page is the real one over a stubbed client, because which page each address
// opens is what is under test: put the own page back on /skills or /workflows, or let one workflow's detail act on
// the own library while the rail names a team, and this goes red.

vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "a-device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));
vi.mock("../AppShell", () => ({ AppShell: () => <Outlet /> }));
vi.mock("../workflows/WorkflowsView", () => ({ WorkflowsView: () => <div>own workflows page</div> }));
vi.mock("../workflows/WorkflowDetail", () => ({ WorkflowDetail: () => <div>own workflow detail</div> }));
vi.mock("../skills/SkillsView", () => ({ SkillsView: () => <div>own skills page</div> }));
vi.mock("@devthrottle/client-core/teams/teamLibraryClient", () => ({
  getTeamLibrary: vi.fn(async () => ({
    team: { id: "team-acme", name: "Acme", role: "Owner" }, canChange: true, changeRefusal: null, builtInNote: "", items: [],
  })),
  newAddProgress: () => ({ createdId: null, createdVersion: null }),
}));

const team = vi.hoisted(() => ({ state: null as unknown as CurrentTeamState }));
vi.mock("@devthrottle/client-core/teams/CurrentTeam", () => ({ useCurrentTeam: () => team.state }));

import { COCKPIT_ROUTES } from "../routes";

const ACME = { id: "team-acme", name: "Acme", role: "Owner", memberCount: 2, people: "2 people" };

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

function renderAt(path: string, onTeam: boolean) {
  team.state = { status: "ready", teams: [ACME], current: onTeam ? ACME : null, resolving: false, error: null, choose: () => {} };
  render(<MemoryRouter initialEntries={[path]}><Shell /></MemoryRouter>);
}

describe("team routes", () => {
  beforeEach(() => cleanup());

  it.each([["/skills"], ["/workflows"]])("Route_%s_WithATeamOnScreen_OpensTheTeamsPage", async (path) => {
    renderAt(path, true);
    expect(await screen.findByRole("heading", { name: "Skills and workflows" })).toBeTruthy();
    expect(screen.queryByText(/^own /)).toBeNull();
  });

  it.each([["/skills", "own skills page"], ["/workflows", "own workflows page"], ["/workflows/mission", "own workflow detail"]])(
    "Route_%s_WithTheOwnAccount_OpensTheOwnPage", async (path, own) => {
      renderAt(path, false);
      expect(await screen.findByText(own)).toBeTruthy();
    });

  it("Route_OneWorkflowsDetail_WithATeamOnScreen_GoesToTheTeamsPage_NeverTheOwnDetail", async () => {
    renderAt("/workflows/mission", true);
    expect(await screen.findByRole("heading", { name: "Skills and workflows" })).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/workflows");
    expect(screen.queryByText("own workflow detail")).toBeNull();
  });
});
