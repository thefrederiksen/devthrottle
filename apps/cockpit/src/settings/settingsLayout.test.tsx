// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, cleanup, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { Outlet, RouterProvider, createMemoryRouter } from "react-router-dom";
import type { MyTeamsAnswer, TeamSummary } from "@devthrottle/client-core/teams/teamsClient";
import type { TeamPage } from "@devthrottle/client-core/teams/teamPageClient";

// ONE SETTINGS PAGE, TABS DOWN THE LEFT (owner, 8 Oct 2026; devthrottle#3681, Mockups C and D), through the REAL route
// table the app mounts: every old address leads to its tab and keeps what it carried, the tab rides in the address so a
// link followed while Settings is open moves it, and the team's tabs appear only with a team on screen - Team plan only
// when the Gateway's Team page answer carries a bill.

vi.mock("@devthrottle/client-core/auth/deviceKey", () => ({
  hasDeviceKey: () => true,
  getDeviceKey: () => "device-key",
  setDeviceKey: vi.fn(),
  clearDeviceKey: vi.fn(),
}));

// The current team, from the Gateway answer each test stages. The shell is not the subject; it keeps only the provider.
const teams = vi.hoisted(() => ({ answer: null as unknown, fail: null as string | null }));
vi.mock("../AppShell", async () => {
  const { CurrentTeamProvider } = await import("@devthrottle/client-core/teams/CurrentTeam");
  const { GatewayError } = await import("@devthrottle/client-core/api/client");
  return {
    AppShell: () => (
      <CurrentTeamProvider
        load={() =>
          teams.fail !== null
            ? Promise.reject(new GatewayError(503, "GET /teams failed: 503", { reason: teams.fail }))
            : Promise.resolve(teams.answer as MyTeamsAnswer)
        }
      >
        <Outlet />
      </CurrentTeamProvider>
    ),
  };
});

const teamPage = vi.hoisted(() => ({
  bill: null as unknown,
  asked: [] as string[],
  fail: null as Error | null,
  answer: null as Promise<void> | null,
}));
vi.mock("@devthrottle/client-core/teams/teamPageClient", () => ({
  getTeamPage: vi.fn(async (teamId: string) => {
    teamPage.asked.push(teamId);
    if (teamPage.answer !== null) await teamPage.answer;
    if (teamPage.fail !== null) throw teamPage.fail;
    return { bill: teamPage.bill } as Partial<TeamPage>;
  }),
}));

// The Gateway's count of dictionary suggestions waiting, which the Dictionary tab carries (owner, 8 Oct 2026).
const dictionaryCount = vi.hoisted(() => ({ value: 0 }));
vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({
  getSuggestionCount: vi.fn(async () => dictionaryCount.value),
}));

// What each tab draws has its own tests; here each one is a marker, so the test is about which tab is on screen.
vi.mock("../account/AccountTab", () => ({ AccountTab: () => <div>account tab</div> }));
vi.mock("../throttle/YourThrottleView", () => ({ YourThrottleView: () => <div>your throttle</div> }));
vi.mock("../dictionary/DictionaryView", () => ({ DictionaryView: () => <div>dictionary</div> }));
vi.mock("../phone/PhoneView", () => ({ PhoneView: () => <div>connect your phone</div> }));
vi.mock("../network/NetworkDiagnosticsView", () => ({ NetworkDiagnosticsView: () => <div>network diagnostics</div> }));
vi.mock("../transcription/TranscriptionHealthView", () => ({ TranscriptionHealthView: () => <div>transcription health</div> }));
vi.mock("./InjectedTextTab", () => ({ InjectedTextTab: () => <div>injected text</div> }));
vi.mock("../team/TeamPageView", () => ({
  TeamPageView: ({ teamId, section }: { teamId: string; section: string }) => <div>{`team ${section} for ${teamId}`}</div>,
}));
vi.mock("../team/InviteView", () => ({ InviteView: ({ teamId }: { teamId: string }) => <div>{`invite to ${teamId}`}</div> }));
vi.mock("../team/TeamGovernanceView", () => ({ TeamGovernanceView: ({ teamId }: { teamId: string }) => <div>{`team governance for ${teamId}`}</div> }));
vi.mock("@devthrottle/client-core/settings/SettingsTabs", async (importActual) => ({
  ...(await importActual<typeof import("@devthrottle/client-core/settings/SettingsTabs")>()),
  SettingsTabPanel: ({ tab }: { tab: string }) => <div>{`shared ${tab}`}</div>,
}));

import { COCKPIT_ROUTES } from "../routes";
import { GatewayError } from "@devthrottle/client-core/api/client";

const FULL = { full: true as const, pages: [], landing: null, elsewhere: null };
const TEAM: TeamSummary = { id: "team-1", name: "Soren Test Team", role: "Owner", memberCount: 1, people: "1 person", app: FULL };

function mountAt(entry: string) {
  const router = createMemoryRouter(COCKPIT_ROUTES, { initialEntries: [entry] });
  const view = render(<RouterProvider router={router} />);
  return { router, view };
}

function where(router: ReturnType<typeof createMemoryRouter>): string {
  return router.state.location.pathname + router.state.location.search;
}

function tabs(): string[] {
  return screen.getAllByRole("tab").map((t) => t.textContent ?? "");
}

beforeEach(() => {
  cleanup();
  window.localStorage.clear();
  teams.answer = { kind: "teams", teams: [], start: { where: "own-account" } };
  teams.fail = null;
  dictionaryCount.value = 0;
  teamPage.bill = null;
  teamPage.asked = [];
  teamPage.fail = null;
  teamPage.answer = null;
});

afterEach(() => cleanup());

describe("every old address leads to its Settings tab", () => {
  it.each([
    ["/account", "/settings?tab=account", "account tab"],
    ["/phone", "/settings?tab=devices", "connect your phone"],
    ["/your-throttle", "/settings?tab=usage", "your throttle"],
    ["/repos", "/settings?tab=usage", "your throttle"],
    ["/dictionary", "/settings?tab=dictionary", "dictionary"],
    ["/transcription", "/settings?tab=transcription", "transcription health"],
    ["/injected-text", "/settings?tab=injectedtext", "injected text"],
  ])("%s", async (old, address, shows) => {
    const { router } = mountAt(old);
    expect(await screen.findByText(shows)).toBeTruthy();
    expect(where(router)).toBe(address);
  });

  // A Mentor report links to a week of Your Throttle; the week must survive the move.
  it("keeps what the old address carried - Your Throttle's week", async () => {
    const { router } = mountAt("/your-throttle?week=2026-W40");
    expect(await screen.findByText("your throttle")).toBeTruthy();
    expect(where(router)).toBe("/settings?tab=usage&week=2026-W40");
  });

  it("puts the team an old Team page address names on screen, then opens its Members tab", async () => {
    teams.answer = { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
    const { router } = mountAt(`/team/${TEAM.id}/members`);
    expect(await screen.findByText(`team members for ${TEAM.id}`)).toBeTruthy();
    expect(where(router)).toBe("/settings?tab=members");
  });

  it("opens Invite someone from the old invite address", async () => {
    teams.answer = { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
    const { router } = mountAt(`/team/${TEAM.id}/invite`);
    expect(await screen.findByText(`invite to ${TEAM.id}`)).toBeTruthy();
    expect(where(router)).toBe("/settings?tab=members&view=invite");
  });

  it("says so, rather than opening another team, when the address names a team that is not yours", async () => {
    teams.answer = { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
    mountAt("/team/someone-elses/members");
    expect((await screen.findByTestId("team-address-not-yours")).textContent).toContain("This team is not one of yours");
  });

  // Only the Gateway's list can say a team is not yours. While it cannot be read, the page says that - and the reason.
  it("says your teams could not be read, and nothing about membership, while the teams read is failing", async () => {
    teams.fail = "The Gateway is restarting.";
    mountAt(`/team/${TEAM.id}/members`);
    const pane = await screen.findByTestId("team-address-unreadable");
    expect(pane.textContent).toContain("Your teams could not be read just now: ");
    expect(pane.textContent).toContain("The Gateway is restarting.");
    expect(screen.queryByTestId("team-address-not-yours")).toBeNull();
    expect(screen.queryByText(/not one of yours/)).toBeNull();
  });
});

describe("Settings, with its tabs down the left", () => {
  it("opens on Account, under You, with no team tabs while Personal is on screen", async () => {
    mountAt("/settings");
    expect(await screen.findByText("account tab")).toBeTruthy();
    expect(screen.getByRole("tab", { name: "Account" }).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByTestId("settings-group-you").textContent).toBe("You");
    expect(screen.queryByTestId("settings-group-team")).toBeNull();
    expect(tabs()).not.toContain("Members");
  });

  it("writes the chosen tab to the address, and follows the address when a link moves it", async () => {
    const { router } = mountAt("/settings");
    await screen.findByText("account tab");

    fireEvent.click(screen.getByRole("tab", { name: "Dictionary" }));
    expect(await screen.findByText("dictionary")).toBeTruthy();
    expect(where(router)).toBe("/settings?tab=dictionary");

    // The menu behind your name links straight to a tab while Settings is open.
    await router.navigate("/settings?tab=usage");
    expect(await screen.findByText("your throttle")).toBeTruthy();
    expect(screen.getByRole("tab", { name: "Plan and usage" }).getAttribute("aria-selected")).toBe("true");
  });

  // On the desktop the Transcription tab goes deeper than the phone's: the shared checks, then the health report.
  it("carries the Gateway's count of dictionary suggestions on the Dictionary tab", async () => {
    dictionaryCount.value = 5;
    mountAt("/settings");
    expect(await screen.findByText("account tab")).toBeTruthy();
    expect((await screen.findByTestId("settings-tab-badge-dictionary")).textContent).toBe("5");
  });

  it("shows the shared Transcription tab with the health report beneath it", async () => {
    mountAt("/settings?tab=transcription");
    const panel = await screen.findByTestId("settings-panel-transcription");
    expect(panel.textContent).toBe("shared transcriptiontranscription health");
  });

  it("adds the team's tabs under the team's name with a team on screen, and Team plan only when the Gateway shows the bill", async () => {
    teams.answer = { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
    window.localStorage.setItem("devthrottle.currentTeam", TEAM.id);
    teamPage.bill = null;
    mountAt("/settings?tab=members");

    expect(await screen.findByText(`team members for ${TEAM.id}`)).toBeTruthy();
    expect(screen.getByTestId("settings-group-team").textContent).toBe("Soren Test Team");
    await waitFor(() => expect(teamPage.asked).toEqual([TEAM.id]));
    expect(tabs().slice(-2)).toEqual(["Members", "Governance"]);
  });

  it("offers Team plan when the Gateway's Team page answer carries a bill", async () => {
    teams.answer = { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
    window.localStorage.setItem("devthrottle.currentTeam", TEAM.id);
    teamPage.bill = { state: "active" };
    mountAt("/settings?tab=teamplan");

    expect(await screen.findByText(`team plan for ${TEAM.id}`)).toBeTruthy();
    expect(tabs().slice(-3)).toEqual(["Members", "Team plan", "Governance"]);
    expect(within(screen.getByRole("tablist")).getByRole("tab", { name: "Team plan" }).getAttribute("aria-selected")).toBe("true");
  });

  // The team's Governance tab is offered with the team on screen whatever the bill says; a link to it opens it.
  it("opens the Governance tab from a link, under the team's name, with or without the team plan", async () => {
    teams.answer = { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
    window.localStorage.setItem("devthrottle.currentTeam", TEAM.id);
    teamPage.bill = null;
    mountAt("/settings?tab=governance");

    expect(await screen.findByText(`team governance for ${TEAM.id}`)).toBeTruthy();
    expect(within(screen.getByRole("tablist")).getByRole("tab", { name: "Governance" }).getAttribute("aria-selected")).toBe("true");
  });

  // A failed read is not the Gateway's verdict: the tab stays, so the tab itself can show the failure and its Retry.
  it("keeps the Team plan tab when the Team page read fails, rather than hiding it without a word", async () => {
    teams.answer = { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
    window.localStorage.setItem("devthrottle.currentTeam", TEAM.id);
    teamPage.fail = new Error("network down");
    mountAt("/settings?tab=members");

    expect(await screen.findByText(`team members for ${TEAM.id}`)).toBeTruthy();
    await waitFor(() => expect(tabs().slice(-3)).toEqual(["Members", "Team plan", "Governance"]));
  });

  // While the answer is unknown the tab shows the failure and a Retry of that read - never the plan section, which
  // would be empty for a role that has no plan. The Retry's answer decides: here, no bill, so the tab goes.
  it("shows only the failure in Team plan while the answer is unknown, and lets the Retry's answer decide", async () => {
    teams.answer = { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
    window.localStorage.setItem("devthrottle.currentTeam", TEAM.id);
    teamPage.fail = new GatewayError(503, "GET /teams/team-1/page failed: 503", { reason: "The Gateway is restarting." });
    mountAt("/settings?tab=teamplan");

    const banner = await screen.findByRole("alert");
    expect(banner.textContent).toContain("The Gateway is restarting.");
    expect(screen.queryByText(`team plan for ${TEAM.id}`)).toBeNull();

    teamPage.fail = null;
    teamPage.bill = null;
    let answer!: () => void;
    teamPage.answer = new Promise<void>((r) => {
      answer = r;
    });
    fireEvent.click(within(banner).getByRole("button", { name: "Try again" }));

    // While the Retry reads, the tab stays in the strip, selected, beside a loading line - no flicker.
    expect(await screen.findByText("Reading the team's plan again...")).toBeTruthy();
    expect(screen.getByRole("tab", { name: "Team plan" }).getAttribute("aria-selected")).toBe("true");

    answer();
    expect(await screen.findByText("account tab")).toBeTruthy();
    expect(tabs()).not.toContain("Team plan");
    expect(teamPage.asked).toEqual([TEAM.id, TEAM.id]);
  });

  it("takes the Team plan tab away only on the Gateway's refusal", async () => {
    teams.answer = { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
    window.localStorage.setItem("devthrottle.currentTeam", TEAM.id);
    teamPage.fail = new GatewayError(403, "refused");
    mountAt("/settings?tab=members");

    expect(await screen.findByText(`team members for ${TEAM.id}`)).toBeTruthy();
    await waitFor(() => expect(teamPage.asked).toEqual([TEAM.id]));
    await new Promise((r) => setTimeout(r, 10));
    expect(tabs()).not.toContain("Team plan");
  });

  // A link straight to Team plan waits for the Gateway's answer; it never opens Account first and then jumps.
  it("holds a link straight to Team plan on a loading line until the Gateway answers, never opening Account first", async () => {
    teams.answer = { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
    window.localStorage.setItem("devthrottle.currentTeam", TEAM.id);
    teamPage.bill = { state: "active" };
    let answer!: () => void;
    teamPage.answer = new Promise<void>((r) => {
      answer = r;
    });
    mountAt("/settings?tab=teamplan");

    await waitFor(() => expect(teamPage.asked).toEqual([TEAM.id]));
    expect(screen.getByText("Opening the team...")).toBeTruthy();
    expect(screen.queryByText("account tab")).toBeNull();

    answer();
    expect(await screen.findByText(`team plan for ${TEAM.id}`)).toBeTruthy();
    expect(screen.queryByText("account tab")).toBeNull();
  });

  it("sends a team tab's address to Account when Personal is on screen", async () => {
    mountAt("/settings?tab=members");
    expect(await screen.findByText("account tab")).toBeTruthy();
  });
});
