// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { act, render, cleanup, screen, fireEvent, within, waitFor } from "@testing-library/react";
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
vi.mock("@devthrottle/client-core/auth/accountActions", () => ({
  signOutAccount: vi.fn(async () => ({ ok: true })),
  switchAccount: vi.fn(async () => {}),
}));
vi.mock("@devthrottle/client-core/fleetmanager/pageClient", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  getFleetManagerPage: vi.fn(async () => ({ waitingCount: 0 })),
}));
vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  getFactoryAgentsSwitch: vi.fn(async () => ({ enabled: false })),
}));
vi.mock("../../network/CockpitStatusPill", () => ({ CockpitStatusPill: () => null }));
// The Requests page's one read (devthrottle_internal#2308): recorded, so a test can show the slot asks for the team on
// screen.
const requestsRead = vi.hoisted(() => ({ teams: [] as string[] }));
vi.mock("@devthrottle/client-core/teams/requestsClient", () => ({
  listMyRequests: vi.fn(async (teamId: string) => {
    requestsRead.teams.push(teamId);
    return [];
  }),
}));
// The Reports page reads what was sent to this person from the Gateway (devthrottle_internal#2309); here, nothing was.
vi.mock("@devthrottle/client-core/teams/teamReportsClient", async (importOriginal) => ({
  ...(await importOriginal<object>()),
  getReportsSentToMe: vi.fn(async () => ({ count: 0, reports: [], emptyText: "No reports sent to you yet.", showYourReports: false })),
  getReportSentToMe: vi.fn(async () => null),
}));
// The Questions page reads what waits on this person from the Gateway (devthrottle_internal#2307); here, nothing does.
vi.mock("@devthrottle/client-core/teams/teamQuestionsClient", () => ({
  getMyQuestions: vi.fn(async () => ({
    count: 0, subtitle: "No questions waiting on you.", emptyText: "No questions waiting on you.", waiting: [], answered: [], answeredHeading: "Answered",
  })),
  answerQuestion: vi.fn(),
}));

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

// The count beside a team page (S8, devthrottle_internal#2307 review F8): every read is recorded by the path it asked,
// and answers the count the test sets.
const pageCounts = vi.hoisted(() => ({ count: 0, asked: [] as string[] }));

const myTeams = vi.hoisted(() => ({
  teams: [] as unknown[],
  start: { where: "own-account" } as unknown,
  failure: null as Error | null,
  // While set, the read has not answered yet: the test releases it.
  held: null as Promise<void> | null,
}));
vi.mock("@devthrottle/client-core/teams/teamsClient", () => ({
  getMyTeams: vi.fn(async () => {
    if (myTeams.held !== null) await myTeams.held;
    if (myTeams.failure !== null) throw myTeams.failure;
    return { kind: "teams", teams: myTeams.teams, start: myTeams.start };
  }),
  getPageCount: vi.fn(async (path: string) => {
    pageCounts.asked.push(path);
    return pageCounts.count;
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
import { refreshTeamPageCounts } from "../useTeamPageCounts";

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
      // An odd count path, so a path the Cockpit built for itself would never match it.
      { id: "questions", label: "Questions", path: "/questions", countPath: "/teams/team-dt/questions?odd-count" },
      { id: "requests", label: "Requests", path: "/requests", countPath: null },
      { id: "reports", label: "Reports", path: "/reports", countPath: null },
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
  app: {
    full: true,
    pages: COLLABORATOR_TEAM.app.pages.map((p) => (p.id === "questions" ? { ...p, countPath: "/teams/team-paul/questions?odd-count" } : p)),
    landing: null,
    elsewhere: null,
  },
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
  const nav = document.querySelector(".nav");
  if (nav === null) throw new Error("the shell rendered no rail");
  return Array.from(nav.querySelectorAll(".nav-link-label")).map((el) => el.textContent ?? "");
}

/** Open the menu behind your name at the bottom of the rail (owner, 8 Oct 2026), and answer it. */
function openYourMenu(): HTMLElement {
  fireEvent.click(screen.getByTestId("you-card"));
  return screen.getByTestId("you-menu");
}

/** True when the menu behind your name offers the whole app's own pages (Settings and the rest). */
function menuOffersSettings(): boolean {
  const menu = openYourMenu();
  const offered = within(menu).queryByRole("menuitem", { name: "Settings" }) !== null;
  fireEvent.keyDown(menu, { key: "Escape" });
  return offered;
}

async function whenRailIs(labels: string[]) {
  await waitFor(() => expect(railLabels()).toEqual(labels));
}

/** Pick a team (by its name) in Working in, in the menu behind your name - where the team switch lives now. */
function pickTeam(teamName: string) {
  const working = within(openYourMenu()).getByRole("group", { name: "Working in" });
  const choice = within(working)
    .getAllByRole("menuitemradio")
    .find((c) => c.querySelector(".you-menu-main")?.textContent === teamName);
  if (choice === undefined) throw new Error(`Working in offers no "${teamName}"`);
  fireEvent.click(choice);
}

describe("The Collaborator's app", () => {
  beforeEach(() => {
    cleanup();
    window.localStorage.clear();
    myTeams.teams = [COLLABORATOR_TEAM, DEVELOPER_TEAM];
    myTeams.start = { where: "own-account" };
    myTeams.failure = null;
    myTeams.held = null;
    reads.keepWarm = [];
    pageCounts.count = 0;
    pageCounts.asked = [];
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
    // No Settings, Usage, Phone or About: a pages-only app has none of those pages. The team switch is still in reach.
    expect(menuOffersSettings()).toBe(false);
    expect(within(openYourMenu()).getByRole("group", { name: "Working in" })).toBeTruthy();
    expect((await screen.findAllByText("No questions waiting on you.")).length).toBeGreaterThan(0);
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

  it("Page_Collaborator_Requests_IsTheRequestsPageForTheTeamOnScreen", async () => {
    requestsRead.teams = [];
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/requests");

    const page = await screen.findByTestId("team-page-requests");
    expect(within(page).getByRole("button", { name: "Send request" })).toBeTruthy();
    expect((await within(page).findByText(/No requests from you yet\./)).textContent).toContain("No requests from you yet.");
    expect(requestsRead.teams).toEqual([COLLABORATOR_TEAM.id]);
  });

  it.each([["/reports", "team-page-reports", "No reports sent to you yet."]])(
    "Page_Collaborator_%s_RendersItsHonestEmptyPage",
    async (path, testId, empty) => {
      rememberTeam(COLLABORATOR_TEAM.id);
      renderAt(path);

      const page = await screen.findByTestId(testId);
      // The Reports page's empty words are the Gateway's, so they arrive with its answer (devthrottle_internal#2309).
      await waitFor(() => expect(page.textContent).toContain(empty));
    },
  );

  it("Page_Collaborator_AReportOpenedOnTheReportsPage_StaysATeamPage", async () => {
    // devthrottle_internal#2309: a report opens in place, named in the query, so the address is still the page's.
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/reports?report=r-1");

    expect(await screen.findByTestId("team-report-missing")).toBeTruthy();
    expect(screen.queryByText(NOT_AVAILABLE)).toBeNull();
    expect(screen.getByTestId("where").textContent).toBe("/reports");
  });

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

    pickTeam("Paul's project");

    await waitFor(() => expect(railLabels().slice(0, 3)).toEqual(["Sessions", "Fleet Map", "Fleet Manager"]));
    expect(railLabels()).toContain("Skills");
    expect(railLabels().slice(-3)).toEqual(["Questions", "Requests", "Reports"]);
    expect(menuOffersSettings()).toBe(true);
    expect(await screen.findByText("sessions page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/sessions");
    expect(screen.queryByTestId("team-page-not-available")).toBeNull();
  });

  it("WholeAppTeam_Developer_TheRailAlsoListsTheTeamsPages_InTheGatewaysOrder", async () => {
    rememberTeam(DEVELOPER_TEAM.id);
    renderAt("/sessions");

    await waitFor(() => expect(railLabels().slice(-3)).toEqual(["Questions", "Requests", "Reports"]));
    expect(railLabels().slice(0, 3)).toEqual(["Sessions", "Fleet Map", "Fleet Manager"]);
    fireEvent.click(screen.getByRole("link", { name: "Reports" }));
    expect(await screen.findByTestId("team-page-reports")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/reports");
  });

  it("WholeAppTeam_TheGatewayListsOnlyReports_TheRailAddsOnlyReports", async () => {
    myTeams.teams = [COLLABORATOR_TEAM, { ...DEVELOPER_TEAM, app: { ...DEVELOPER_TEAM.app, pages: [COLLABORATOR_TEAM.app.pages[2]] } }];
    rememberTeam(DEVELOPER_TEAM.id);
    renderAt("/sessions");

    await waitFor(() => expect(railLabels().at(-1)).toBe("Reports"));
    expect(railLabels()).not.toContain("Questions");
    expect(railLabels()).not.toContain("Requests");
  });

  // ---- the count beside a team page (S8, review F8) ------------------------------------------------------------------

  function railBadge(label: string): string | null {
    const link = screen.getByRole("link", { name: new RegExp(`^${label}`) });
    return link.querySelector(".nav-badge")?.textContent ?? null;
  }

  it("Count_TheGatewaysCount_ShowsBesideQuestions_ReadFromThePathTheGatewayNamed", async () => {
    pageCounts.count = 3;
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    await whenRailIs(["Questions", "Requests", "Reports"]);
    await waitFor(() => expect(railBadge("Questions")).toBe("3"));
    expect(railBadge("Requests")).toBeNull();
    expect(railBadge("Reports")).toBeNull();
    // Read only where the Gateway said, and never for a page with no count path.
    expect(new Set(pageCounts.asked)).toEqual(new Set(["/teams/team-dt/questions?odd-count"]));
  });

  it("Count_AWholeAppTeam_ShowsTheCountBesideItsQuestionsToo", async () => {
    pageCounts.count = 2;
    rememberTeam(DEVELOPER_TEAM.id);
    renderAt("/sessions");

    await waitFor(() => expect(railLabels().slice(-3)).toEqual(["Questions", "Requests", "Reports"]));
    await waitFor(() => expect(railBadge("Questions")).toBe("2"));
    expect(new Set(pageCounts.asked)).toEqual(new Set(["/teams/team-paul/questions?odd-count"]));
  });

  it("Count_AfterAnAnswer_IsReadAgainAtOnce_SoTheRailNeverDisagreesWithThePage", async () => {
    pageCounts.count = 1;
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");
    await waitFor(() => expect(railBadge("Questions")).toBe("1"));
    const readsBefore = pageCounts.asked.length;

    pageCounts.count = 0;
    act(() => refreshTeamPageCounts());

    await waitFor(() => expect(railBadge("Questions")).toBeNull());
    expect(pageCounts.asked.length).toBe(readsBefore + 1);
  });

  it("Count_ZeroWaiting_ShowsNoCount", async () => {
    pageCounts.count = 0;
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    await whenRailIs(["Questions", "Requests", "Reports"]);
    await waitFor(() => expect(pageCounts.asked.length).toBeGreaterThan(0));
    expect(railBadge("Questions")).toBeNull();
  });

  it("Count_NoTeam_ReadsNoCount_AndTheRailIsUnchanged", async () => {
    myTeams.teams = [];
    pageCounts.count = 5;
    renderAt("/sessions");

    expect(await screen.findByText("sessions page")).toBeTruthy();
    expect(pageCounts.asked).toEqual([]);
    expect(document.querySelector(".nav-link .nav-badge")).toBeNull();
  });

  it("OwnAccount_TheRailListsNoTeamPages", async () => {
    renderAt("/sessions");

    expect(await screen.findByText("sessions page")).toBeTruthy();
    await waitFor(() => expect(screen.getByTestId("you-card").textContent).toContain("Personal"));
    for (const label of ["Questions", "Requests", "Reports"]) expect(railLabels()).not.toContain(label);
  });

  it("SwitchTeam_FromTheOwnAccountToACollaboratorTeam_OpensItsLandingPage", async () => {
    renderAt("/sessions");
    await waitFor(() => expect(screen.getByTestId("you-card").textContent).toContain("Personal"));
    expect(railLabels()[0]).toBe("Sessions");

    pickTeam("DevThrottle");

    await whenRailIs(["Questions", "Requests", "Reports"]);
    expect(screen.getByTestId("where").textContent).toBe("/questions");
    expect(screen.queryByText("sessions page")).toBeNull();
  });

  it("OwnAccount_TheTeamPageAddresses_AreThePlainPageNotFound", async () => {
    renderAt("/questions");

    await waitFor(() => expect(screen.getByTestId("you-card").textContent).toContain("Personal"));
    expect(screen.getByText("Page not found")).toBeTruthy();
    expect(screen.queryByTestId("team-page-questions")).toBeNull();
  });

  it("NoTeam_TheRailAndTheAppAreUnchanged", async () => {
    myTeams.teams = [];
    renderAt("/sessions");

    expect(await screen.findByText("sessions page")).toBeTruthy();
    expect(railLabels().slice(0, 3)).toEqual(["Sessions", "Fleet Map", "Fleet Manager"]);
    for (const label of ["Questions", "Requests", "Reports"]) expect(railLabels()).not.toContain(label);
    expect(screen.queryByTestId("nav-team")).toBeNull();
    expect(menuOffersSettings()).toBe(true);
    // A team page address waits for the list of teams (round 3 review, R1), then is the ordinary "Page not found".
    renderAt("/requests");
    expect((await screen.findAllByText("Page not found")).length).toBeGreaterThan(0);
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
    expect(railLabels()[0]).toBe("Sessions");
    // For this load only (round 3 review, R3): the remembered team is kept, so the next load tries it again.
    expect(window.localStorage.getItem(currentTeamStorageKey())).toBe(COLLABORATOR_TEAM.id);
  });

  // ---- Review findings F1-F4 -------------------------------------------------------------------------------------

  it("FirstArrival_OneTeamAsACollaborator_NoDirector_LandsOnTheThreePages", async () => {
    myTeams.teams = [COLLABORATOR_TEAM];
    myTeams.start = { where: "team", teamId: COLLABORATOR_TEAM.id };
    renderAt("/");

    await whenRailIs(["Questions", "Requests", "Reports"]);
    expect(await screen.findByTestId("team-page-questions")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/questions");
    expect(screen.queryByText("sessions page")).toBeNull();
  });

  it("FirstArrival_SeveralTeams_NoDirector_OffersTheChooser_AndOpensThePickedTeam", async () => {
    myTeams.start = { where: "choose" };
    renderAt("/");

    const chooser = await screen.findByTestId("team-chooser");
    expect(chooser.textContent).toContain("You are signed in as mike@example.com");
    expect(chooser.textContent).toContain("Collaborator - 5 people");
    expect(chooser.textContent).toContain("Developer - 2 people");
    expect(screen.queryByText("sessions page")).toBeNull();
    expect(railLabels()).toEqual([]);
    // While the chooser is on screen it IS the choice: the menu behind your name offers no second one.
    expect(within(openYourMenu()).queryByRole("group", { name: "Working in" })).toBeNull();
    fireEvent.keyDown(screen.getByTestId("you-menu"), { key: "Escape" });
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

    expect(await screen.findByText("sessions page")).toBeTruthy();
    expect(screen.getByTestId("where").textContent).toBe("/sessions");
    expect(railLabels()[0]).toBe("Sessions");
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
    // The Fleet Manager's rail count is off for now (7 Oct 2026), so the rail no longer reads it.
    expect(getFleetManagerPage).not.toHaveBeenCalled();
    expect(getFactoryAgentsSwitch).toHaveBeenCalled();
    expect(reads.keepWarm.at(-1)).toBe(true);
  });

  // A Collaborator's app has no Account page, and on a shared computer a person must be able to see who is signed in and
  // leave (review finding F3): you, at the bottom - who, the team and role on screen - and the one sign-out.
  it("PagesOnly_YouShowWhoIsSignedInAndTheirRole_AndSignOut", async () => {
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    await whenRailIs(["Questions", "Requests", "Reports"]);
    const card = screen.getByTestId("you-card");
    expect(card.textContent).toContain("mike@example.com");
    expect(card.textContent).toContain("Collaborator");
    fireEvent.click(within(openYourMenu()).getByRole("menuitem", { name: "Sign out of mike@example.com" }));
    // The confirmation's own button, which appears once the dialog opens.
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Sign out" }));

    await waitFor(() => expect(signOutAccount).toHaveBeenCalledWith("acct-1"));
  });

  it("PagesOnly_ARememberedCollapsedRail_IsIgnored_SoTheSwitcherAndNamesStay", async () => {
    window.localStorage.setItem("cockpit.railCollapsed", "true");
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    await whenRailIs(["Questions", "Requests", "Reports"]);
    expect(screen.getByTestId("you-card").textContent).toContain("mike@example.com");
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
    expect(railLabels()[0]).toBe("Sessions");

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
    expect(screen.getByTestId("you-card").textContent).toContain("mike@example.com");
    expect(within(openYourMenu()).getByRole("menuitem", { name: "Sign out of mike@example.com" })).toBeTruthy();
    expect(screen.queryByText("Cockpit (React)")).toBeNull();
  });

  it("TeamCouldNotBeOpened_ShowsWhoIsSignedIn_AndSignOut", async () => {
    myTeams.failure = new Error("Gateway restarting");
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    await screen.findByTestId("team-unreadable");
    expect(screen.getByTestId("you-card").textContent).toContain("mike@example.com");
    const menu = openYourMenu();
    expect(within(menu).getByRole("menuitem", { name: "Sign out of mike@example.com" })).toBeTruthy();
    // The teams could not be read, and the person is told so where the team switch lives.
    expect(within(menu).getByText("Your teams could not be read just now.")).toBeTruthy();
  });

  // ---- Round 3 review, R1 and R7 ----------------------------------------------------------------------------------

  it("FirstArrival_AMailedLinkToReports_NeverShowsPageNotFound_AndStaysOnReports", async () => {
    myTeams.teams = [COLLABORATOR_TEAM];
    myTeams.start = { where: "team", teamId: COLLABORATOR_TEAM.id };
    let release: () => void = () => {};
    myTeams.held = new Promise<void>((r) => (release = r));
    renderAt("/reports");

    // The list of teams is still being read: the page waits instead of saying it does not exist.
    expect(screen.queryByText("Page not found")).toBeNull();
    expect(screen.getByText("Loading your team...")).toBeTruthy();

    await act(async () => release());

    // The Gateway's start opens the team, and the person stays on the page the link named - not moved to Questions.
    expect(await screen.findByTestId("team-page-reports")).toBeTruthy();
    await whenRailIs(["Questions", "Requests", "Reports"]);
    expect(screen.getByTestId("where").textContent).toBe("/reports");
    expect(screen.queryByText("Page not found")).toBeNull();
  });

  // Collapsed, you are your initials - and the sign-out is still one click behind them.
  it("TeamCouldNotBeOpened_InACollapsedRail_StillOffersSignOutBehindYourInitials", async () => {
    window.localStorage.setItem("cockpit.railCollapsed", "true");
    myTeams.failure = new Error("Gateway restarting");
    rememberTeam(COLLABORATOR_TEAM.id);
    renderAt("/questions");

    await screen.findByTestId("team-unreadable");
    expect(document.querySelector(".shell-rail-collapsed")).not.toBeNull();
    expect(screen.getByTestId("you-card").textContent).toBe("ME");
    expect(within(openYourMenu()).getByRole("menuitem", { name: "Sign out of mike@example.com" })).toBeTruthy();
  });
});
