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
// The five reads the whole app's rail makes (review finding F2). Recorded, so a test can show a pages-only Cockpit
// makes none of them.
const reads = vi.hoisted(() => ({ keepWarm: [] as boolean[] }));
vi.mock("@devthrottle/client-core/net/useKeepWarm", () => ({
  useKeepWarm: (enabled = true) => {
    reads.keepWarm.push(enabled);
  },
}));
vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({ getSuggestionCount: vi.fn(async () => 0) }));
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({ resumePendingDictations: vi.fn(async () => {}) }));
vi.mock("@devthrottle/client-core/auth/accountActions", () => ({ signOutAccount: vi.fn(async () => ({ ok: true })) }));
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

const myTeams = vi.hoisted(() => ({
  teams: [] as unknown[],
  start: { where: "own-account" } as unknown,
  failure: null as Error | null,
}));
vi.mock("@devthrottle/client-core/teams/teamsClient", () => ({
  getMyTeams: vi.fn(async () => {
    if (myTeams.failure !== null) throw myTeams.failure;
    return { kind: "teams", teams: myTeams.teams, start: myTeams.start };
  }),
}));

import { currentTeamStorageKey } from "@devthrottle/client-core/teams/CurrentTeam";
import { getSuggestionCount } from "@devthrottle/client-core/dictation/dictionaryClient";
import { resumePendingDictations } from "@devthrottle/client-core/dictation/backgroundSend";
import { getFleetManagerPage } from "@devthrottle/client-core/fleetmanager/pageClient";
import { getFactoryAgentsSwitch } from "@devthrottle/client-core/factory/factoryAgentsClient";
import { signOutAccount } from "@devthrottle/client-core/auth/accountActions";
import { resetFactorySwitchCache } from "../../factory/useFactorySwitch";
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
    myTeams.start = { where: "own-account" };
    myTeams.failure = null;
    reads.keepWarm = [];
    vi.clearAllMocks();
    resetFactorySwitchCache();
    // The signed-in account, as a finished sign-in leaves it in this browser.
    window.localStorage.setItem(
      "cc.accounts",
      JSON.stringify([{ id: "acct-1", label: "mike@example.com", email: "mike@example.com", deviceKey: "k", installId: "i" }]),
    );
    window.localStorage.setItem("cc.activeAccount", "acct-1");
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
    // Exact page addresses only, as the Gateway's phone front door reads them (delta review D8).
    ["/questions/q-1", "No questions waiting on you."],
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

  // ---- Review findings F1-F4 -------------------------------------------------------------------------------------

  it("FirstArrival_OneTeamAsACollaborator_NoDirector_LandsOnTheThreePages", async () => {
    myTeams.teams = [COLLABORATOR_TEAM];
    myTeams.start = { where: "team", teamId: COLLABORATOR_TEAM.id };
    renderAt("/");

    await whenRailIs(["Questions", "Requests", "Reports"]);
    expect(await screen.findByTestId("team-page-questions")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/questions");
    expect(screen.queryByText("fleet manager page")).toBeNull();
  });

  it("FirstArrival_SeveralTeams_NoDirector_OffersTheChooser_AndOpensThePickedTeam", async () => {
    myTeams.start = { where: "choose" };
    renderAt("/");

    const chooser = await screen.findByTestId("team-chooser");
    expect(chooser.textContent).toContain("You are signed in as mike@example.com");
    expect(chooser.textContent).toContain("Collaborator - 5 people");
    expect(chooser.textContent).toContain("Developer - 2 people");
    expect(screen.queryByText("fleet manager page")).toBeNull();
    expect(railLabels()).toEqual([]);
    expect(screen.queryByTestId("team-switcher")).toBeNull();
    // The chooser takes the short rail: a bar at phone width, never collapsed.
    expect(document.querySelector(".shell-team-pages")).not.toBeNull();
    expect(screen.queryByTestId("rail-toggle")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Open DevThrottle" }));

    await whenRailIs(["Questions", "Requests", "Reports"]);
    expect(screen.getByTestId("where").textContent).toBe("/questions");
  });

  it("FirstArrival_ADirectorOnTheOwnAccount_StartsOnTheOwnAccount", async () => {
    myTeams.start = { where: "own-account" };
    renderAt("/");

    expect(await screen.findByText("fleet manager page")).toBeTruthy();
    expect(railLabels()[0]).toBe("Fleet Manager");
  });

  it("PagesOnly_TheWholeAppsFiveReads_AreNeverMade", async () => {
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");
    await whenRailIs(["Questions", "Requests", "Reports"]);
    await new Promise((r) => setTimeout(r, 30));

    expect(getSuggestionCount).not.toHaveBeenCalled();
    expect(resumePendingDictations).not.toHaveBeenCalled();
    expect(getFleetManagerPage).not.toHaveBeenCalled();
    expect(getFactoryAgentsSwitch).not.toHaveBeenCalled();
    expect(reads.keepWarm.at(-1)).toBe(false);
  });

  it("WholeApp_TheFiveReads_AreStillMade", async () => {
    renderAt("/sessions");
    expect(await screen.findByText("sessions page")).toBeTruthy();

    await waitFor(() => expect(getSuggestionCount).toHaveBeenCalled());
    expect(resumePendingDictations).toHaveBeenCalled();
    expect(getFleetManagerPage).toHaveBeenCalled();
    expect(getFactoryAgentsSwitch).toHaveBeenCalled();
    expect(reads.keepWarm.at(-1)).toBe(true);
  });

  it("PagesOnly_TheFootShowsWhoIsSignedInAndTheirRole_AndSignsOut", async () => {
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    const foot = await screen.findByTestId("team-pages-foot");
    expect(foot.textContent).toContain("mike@example.com");
    expect(foot.textContent).toContain("Collaborator");
    fireEvent.click(within(foot).getByRole("button", { name: "Sign out" }));
    // The confirmation's own button, which appears once the dialog opens.
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Sign out" })).toHaveLength(2));
    const buttons = screen.getAllByRole("button", { name: "Sign out" });
    fireEvent.click(buttons[buttons.length - 1]);

    await waitFor(() => expect(signOutAccount).toHaveBeenCalledWith("acct-1"));
  });

  it("PagesOnly_ARememberedCollapsedRail_IsIgnored_SoTheSwitcherAndNamesStay", async () => {
    window.localStorage.setItem("cockpit.railCollapsed", "true");
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    await whenRailIs(["Questions", "Requests", "Reports"]);
    expect(screen.getByTestId("team-switcher")).toBeTruthy();
    expect(document.querySelector(".shell-rail-collapsed")).toBeNull();
    expect(screen.queryByTestId("rail-toggle")).toBeNull();
  });

  // ---- Delta review D1, D6, D7 ------------------------------------------------------------------------------------

  it("FirstArrival_NothingRemembered_DrawsTheOwnAccountAtOnce_ThenTheGatewaysTeam", async () => {
    myTeams.teams = [COLLABORATOR_TEAM];
    myTeams.start = { where: "team", teamId: COLLABORATOR_TEAM.id };
    renderAt("/sessions");

    // No wait: the own account is on screen before the list of teams answers.
    expect(screen.queryByText("Loading your team...")).toBeNull();
    expect(railLabels()[0]).toBe("Fleet Manager");

    // Then the Gateway's start opens the team where it starts.
    await whenRailIs(["Questions", "Requests", "Reports"]);
    await waitFor(() => expect(screen.getByTestId("where").textContent).toBe("/questions"));
    expect(await screen.findByTestId("team-page-questions")).toBeTruthy();
  });

  it("Chooser_ArrivedAtOneOfTheTeamsPages_StaysOnIt", async () => {
    myTeams.start = { where: "choose" };
    renderAt("/reports");

    await screen.findByTestId("team-chooser");
    fireEvent.click(screen.getByRole("button", { name: "Open DevThrottle" }));

    await whenRailIs(["Questions", "Requests", "Reports"]);
    expect(screen.getByTestId("where").textContent).toBe("/reports");
    expect(await screen.findByTestId("team-page-reports")).toBeTruthy();
  });

  it("Chooser_ShowsWhoIsSignedIn_AndSignOut", async () => {
    myTeams.start = { where: "choose" };
    renderAt("/");

    await screen.findByTestId("team-chooser");
    const foot = screen.getByTestId("team-pages-foot");
    expect(foot.textContent).toContain("mike@example.com");
    expect(within(foot).getByRole("button", { name: "Sign out" })).toBeTruthy();
    expect(screen.queryByText("Cockpit (React)")).toBeNull();
  });

  it("TeamCouldNotBeOpened_ShowsWhoIsSignedIn_AndSignOut", async () => {
    myTeams.failure = new Error("Gateway restarting");
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    await screen.findByTestId("team-unreadable");
    const foot = screen.getByTestId("team-pages-foot");
    expect(foot.textContent).toContain("mike@example.com");
    expect(within(foot).getByRole("button", { name: "Sign out" })).toBeTruthy();
  });
});
