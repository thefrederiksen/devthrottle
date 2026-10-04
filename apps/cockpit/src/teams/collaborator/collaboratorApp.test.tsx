// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup, screen, fireEvent, within, waitFor } from "@testing-library/react";
import { MemoryRouter, useLocation, useRoutes } from "react-router-dom";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";

// THE COLLABORATOR'S OWN APP (devthrottle_internal#2306), through the REAL shell and the REAL route table: the rail is
// drawn from the Gateway's page verdict, every other address is the one "not available" page, and the same account
// switched to a team where it is a Developer gets the whole Cockpit back. Only what cannot run in jsdom, or is not
// under test, is stood in for: the network polls the rail makes, and the pages of the whole app.

vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "a-device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));
vi.mock("@devthrottle/client-core/net/useKeepWarm", () => ({ useKeepWarm: () => {} }));
vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({ getSuggestionCount: vi.fn(async () => 0) }));
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({ resumePendingDictations: vi.fn(async () => {}) }));
vi.mock("@devthrottle/client-core/fleetmanager/pageClient", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  getFleetManagerPage: vi.fn(async () => ({ waitingCount: 0 })),
}));
vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  getFactoryAgentsSwitch: vi.fn(async () => ({ enabled: false })),
}));
vi.mock("../../network/CockpitStatusPill", () => ({ CockpitStatusPill: () => null }));

// The whole app's pages. A page mounted when it must not be shows up as its own text, which the tests look for.
vi.mock("../../fleetmanager/FleetManagerView", () => ({ FleetManagerView: () => <div>fleet manager page</div> }));
vi.mock("../../sessions/SessionsView", async () => {
  const { Outlet } = await import("react-router-dom");
  return { SessionsView: () => <div>sessions page<Outlet /></div>, SessionsEmpty: () => <div>pick a session</div> };
});
vi.mock("../../sessions/SessionDetail", () => ({ SessionDetail: () => <div>session detail page</div> }));
vi.mock("../../fleet/FleetMapView", () => ({ FleetMapView: () => <div>fleet map page</div> }));
vi.mock("../../fleet/DirectorsView", () => ({ DirectorsView: () => <div>directors page</div> }));
vi.mock("../../skills/SkillsView", () => ({ SkillsView: () => <div>skills page</div> }));
vi.mock("../../settings/SettingsView", () => ({ SettingsView: () => <div>settings page</div> }));
vi.mock("../../account/AccountView", () => ({ AccountView: () => <div>account page</div> }));

const myTeams = vi.hoisted(() => ({ teams: [] as unknown[], failure: null as Error | null }));
vi.mock("@devthrottle/client-core/teams/teamsClient", () => ({
  getMyTeams: vi.fn(async () => {
    if (myTeams.failure !== null) throw myTeams.failure;
    return { kind: "teams", teams: myTeams.teams };
  }),
}));

import { currentTeamStorageKey } from "@devthrottle/client-core/teams/CurrentTeam";
import { COCKPIT_ROUTES } from "../../routes";

const NOT_AVAILABLE = "This page is not available to Collaborators.";

const COLLABORATOR_TEAM: TeamSummary = {
  id: "team-dt",
  name: "DevThrottle",
  role: "Collaborator",
  memberCount: 5,
  people: "5 people",
  app: {
    full: false,
    pages: [
      { id: "questions", label: "Questions", path: "/questions" },
      { id: "requests", label: "Requests", path: "/requests" },
      { id: "reports", label: "Reports", path: "/reports" },
    ],
    landing: "/questions",
    elsewhere: NOT_AVAILABLE,
  },
};

const DEVELOPER_TEAM: TeamSummary = {
  id: "team-paul",
  name: "Paul's project",
  role: "Developer",
  memberCount: 2,
  people: "2 people",
  app: { full: true, pages: COLLABORATOR_TEAM.app.pages, landing: null, elsewhere: null },
};

function App() {
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
  return render(
    <MemoryRouter initialEntries={[path]}>
      <App />
    </MemoryRouter>,
  );
}

function rememberTeam(id: string) {
  window.localStorage.setItem(currentTeamStorageKey(), id);
}

function railLabels(): string[] {
  const list = document.querySelector(".nav-list:not(.nav-list-foot)");
  if (list === null) throw new Error("the shell rendered no main nav list");
  return Array.from(list.querySelectorAll(".nav-link-label")).map((el) => el.textContent ?? "");
}

async function whenRailIs(labels: string[]) {
  await waitFor(() => expect(railLabels()).toEqual(labels));
}

function pickTeam(optionText: string) {
  const select = within(screen.getByTestId("team-switcher")).getByRole("combobox") as HTMLSelectElement;
  const option = Array.from(select.options).find((o) => o.textContent === optionText);
  if (option === undefined) throw new Error(`the switcher offers no "${optionText}"`);
  fireEvent.change(select, { target: { value: option.value } });
}

describe("The Collaborator's app", () => {
  beforeEach(() => {
    cleanup();
    window.localStorage.clear();
    myTeams.teams = [COLLABORATOR_TEAM, DEVELOPER_TEAM];
    myTeams.failure = null;
  });

  it("Navigation_Collaborator_HasExactlyTheThreePagesTheGatewayListsAndNothingElse", async () => {
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    await whenRailIs(["Questions", "Requests", "Reports"]);
    expect(document.querySelectorAll(".nav-link")).toHaveLength(3);
    expect(document.querySelector(".nav-list-foot")).toBeNull();
    expect(screen.getByTestId("team-switcher")).toBeTruthy();
    expect(screen.getByText("No questions waiting on you.")).toBeTruthy();
  });

  it("Navigation_IsDrawnFromTheGatewaysVerdict_NotFromTheRoleName", async () => {
    // The same role word with a verdict of two pages draws two: the rail reads the verdict, never the role.
    myTeams.teams = [
      { ...COLLABORATOR_TEAM, app: { ...COLLABORATOR_TEAM.app, pages: COLLABORATOR_TEAM.app.pages.filter((p) => p.id !== "requests") } },
    ];
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    await whenRailIs(["Questions", "Reports"]);
  });

  it("Landing_Collaborator_OpensOnQuestions", async () => {
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/");

    expect(await screen.findByTestId("team-page-questions")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/questions");
  });

  it.each([["/requests", "team-page-requests", "No requests from you yet."], ["/reports", "team-page-reports", "No reports sent to you yet."]])(
    "Page_Collaborator_%s_RendersItsHonestEmptyPage",
    async (path, testId, empty) => {
      rememberTeam(COLLABORATOR_TEAM.id);
      renderAt(path);

      const page = await screen.findByTestId(testId);
      expect(page.textContent).toContain(empty);
    },
  );

  it.each([
    ["/sessions", "sessions page"],
    ["/session/abc123", "session detail page"],
    ["/fleet-map", "fleet map page"],
    ["/directors", "directors page"],
    ["/skills", "skills page"],
    ["/fleet-manager", "fleet manager page"],
    ["/settings", "settings page"],
    ["/account", "account page"],
    ["/team/team-dt/invite", "Invite someone"],
    ["/no-such-page", "Page not found"],
  ])("TypedAddress_Collaborator_%s_ShowsOnlyTheNotAvailablePage", async (path, pageText) => {
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt(path);

    const page = await screen.findByTestId("team-page-not-available");
    expect(page.textContent).toBe(NOT_AVAILABLE);
    expect(screen.queryByText(pageText)).toBeNull();
    expect(screen.getByTestId("where").textContent).toBe(path);
  });

  it("SwitchTeam_SameAccountAsADeveloperElsewhere_GetsTheWholeApp", async () => {
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");
    await whenRailIs(["Questions", "Requests", "Reports"]);

    pickTeam("Paul's project - Developer");

    await waitFor(() => expect(railLabels().slice(0, 3)).toEqual(["Fleet Manager", "Sessions", "Fleet Map"]));
    expect(railLabels()).toContain("Skills");
    expect(document.querySelector(".nav-list-foot")).not.toBeNull();
    expect(await screen.findByText("fleet manager page")).toBeTruthy();
    expect(screen.queryByTestId("team-page-not-available")).toBeNull();
  });

  it("SwitchTeam_FromTheOwnAccountToACollaboratorTeam_OpensItsLandingPage", async () => {
    renderAt("/sessions");
    await screen.findByTestId("team-switcher");
    expect(railLabels()[0]).toBe("Fleet Manager");

    pickTeam("DevThrottle - Collaborator");

    await whenRailIs(["Questions", "Requests", "Reports"]);
    expect(screen.getByTestId("where").textContent).toBe("/questions");
    expect(screen.queryByText("sessions page")).toBeNull();
  });

  it("OwnAccount_TheTeamPageAddresses_AreThePlainPageNotFound", async () => {
    renderAt("/questions");

    await screen.findByTestId("team-switcher");
    expect(screen.getByText("Page not found")).toBeTruthy();
    expect(screen.queryByTestId("team-page-questions")).toBeNull();
  });

  it("NoTeam_TheRailAndTheAppAreUnchanged", async () => {
    myTeams.teams = [];
    renderAt("/sessions");

    expect(await screen.findByText("sessions page")).toBeTruthy();
    expect(railLabels().slice(0, 3)).toEqual(["Fleet Manager", "Sessions", "Fleet Map"]);
    expect(document.querySelector(".nav-list-foot")).not.toBeNull();
    expect(screen.queryByTestId("team-switcher")).toBeNull();
    renderAt("/requests");
    expect(screen.getAllByText("Page not found").length).toBeGreaterThan(0);
  });

  it("RememberedTeam_TeamsCannotBeRead_DrawsNoPageAndOffersTheOwnAccount", async () => {
    myTeams.failure = new Error("Gateway restarting");
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/sessions");

    expect(await screen.findByTestId("team-unreadable")).toBeTruthy();
    expect(screen.queryByText("sessions page")).toBeNull();
    expect(railLabels()).toEqual([]);

    fireEvent.click(screen.getByText("Open your own account instead"));

    expect(await screen.findByText("sessions page")).toBeTruthy();
    expect(railLabels()[0]).toBe("Fleet Manager");
  });
});
