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

// The Requests list's one read (devthrottle_internal#2308), recorded so a test can show which team the address opened.
const teamRequests = vi.hoisted(() => ({ read: [] as string[] }));
vi.mock("@devthrottle/client-core/teams/requestsClient", () => ({
  listTeamRequests: vi.fn(async (teamId: string) => {
    teamRequests.read.push(teamId);
    return [];
  }),
}));

const team = vi.hoisted(() => ({ state: null as unknown as CurrentTeamState }));
vi.mock("@devthrottle/client-core/teams/CurrentTeam", () => ({ useCurrentTeam: () => team.state }));

import { COCKPIT_ROUTES } from "../routes";

const ACME = {
  id: "team-acme",
  name: "Acme",
  role: "Owner",
  memberCount: 2,
  people: "2 people",
  app: { full: true as const, pages: [], landing: null, elsewhere: null },
};

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
  team.state = {
    status: "ready",
    teams: [ACME],
    current: onTeam ? ACME : null,
    resolving: false,
    choosing: false,
    error: null,
    choose: () => null,
    openOwnAccountForThisLoad: () => {},
  };
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

  it("Route_TeamRequests_OpensTheOwnerAndManagersListForTheTeamInTheAddress", async () => {
    teamRequests.read = [];
    renderAt("/team/team-other/requests", true);

    expect(await screen.findByText("Nobody on the team has sent a request yet.")).toBeTruthy();
    // The address names the team, not the switcher: the team on screen is Acme, the list read is the other one.
    expect(teamRequests.read).toEqual(["team-other"]);
  });
});
