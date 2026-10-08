// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";

// The left rail's ORDER is a product decision, not an accident of the array literal, so it is pinned
// here: Sessions first, then Fleet Map, then the Fleet Manager (owner, 7 Oct 2026 - what you use every day first,
// what you set up lower, in the order you build it). Without this test the order is one careless re-sort away from changing silently - nothing else
// in the app reads it.
//
// The rail holds only the work (owner, 8 Oct 2026): what is about YOU - Account, Phone, Your Throttle, Settings, About,
// Help - is in the menu behind your name at the bottom (you/YouMenu.test.tsx), Dictionary, Transcription and Network
// are tabs of Settings, and the team's pages sit in their own block only while a team is on screen.

vi.mock("@devthrottle/client-core/net/useKeepWarm", () => ({
  useKeepWarm: () => {},
}));

vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({
  getSuggestionCount: vi.fn(async () => 0),
}));

vi.mock("./network/CockpitStatusPill", () => ({
  CockpitStatusPill: () => null,
}));

const page = vi.hoisted(() => ({ waitingCount: 0 }));
vi.mock("@devthrottle/client-core/fleetmanager/pageClient", () => ({
  getFleetManagerPage: vi.fn(async () => ({ waitingCount: page.waitingCount })),
}));

// The Gateway says whether the Factory Agents area is on; the rail only follows it (rule 7).
const factory = vi.hoisted(() => ({ enabled: false }));
vi.mock("@devthrottle/client-core/factory/factoryAgentsClient", () => ({
  getFactoryAgentsSwitch: vi.fn(async () => ({ enabled: factory.enabled })),
}));

// The person's teams (devthrottle_internal#2312). The team switch in the menu behind your name follows the Gateway's
// answer, and so does the rail's team block.
const myTeams = vi.hoisted(() => ({
  answer: { kind: "teams", teams: [], start: { where: "own-account" } } as
    | { kind: "teams"; teams: TeamSummary[]; start: { where: "own-account" } }
    | { kind: "not-offered"; reason: string }
    | Error,
  // While set, the read has not answered yet: the test releases it.
  held: null as Promise<void> | null,
}));
vi.mock("@devthrottle/client-core/teams/teamsClient", () => ({
  getMyTeams: vi.fn(async () => {
    if (myTeams.held !== null) await myTeams.held;
    if (myTeams.answer instanceof Error) throw myTeams.answer;
    return myTeams.answer;
  }),
}));

// The Mentor entry (devthrottle_internal#2305) follows the Gateway's answer to the Mentor read for the team on screen:
// a page offers it, a refusal or a missing team hides it, and any failure SHOWS it and is reported (review F2). The
// rail never decides it from a role label.
const mentorRead = vi.hoisted(() => ({
  answers: new Map<string, unknown>(),
  calls: [] as string[],
}));
vi.mock("@devthrottle/client-core/teams/mentorClient", () => ({
  getMentorPage: vi.fn(async (teamId: string) => {
    mentorRead.calls.push(teamId);
    const staged = mentorRead.answers.get(teamId);
    if (staged === undefined) throw new Error(`no Mentor answer staged for ${teamId}`);
    // A list is a sequence of answers, one per read; the last one repeats.
    const answer = Array.isArray(staged) ? (staged.length > 1 ? staged.shift() : staged[0]) : staged;
    if (answer instanceof Error) throw answer;
    return answer;
  }),
  // The person's own page (owner, 8 Oct 2026), recorded as "(own account)". Unstaged it answers "not offered" - a
  // Gateway with no personal page - so a test about something else sees the rail it always saw.
  getPersonalMentorPage: vi.fn(async () => {
    mentorRead.calls.push(OWN);
    const staged = mentorRead.answers.get(OWN);
    if (staged === undefined) return { kind: "not-offered" };
    const answer = Array.isArray(staged) ? (staged.length > 1 ? staged.shift() : staged[0]) : staged;
    if (answer instanceof Error) throw answer;
    return answer;
  }),
}));
const OWN = "(own account)";

// The probe asks again after a failure on the shell's rhythm, CurrentTeam's retryDelayMs (review of the delta, D2).
// The tests set that rhythm: an hour by default, so a failure is asked once; milliseconds where the re-ask is the
// point. Only the probe's import is replaced - the provider calls its own function.
const rhythm = vi.hoisted(() => ({ delayMs: 3_600_000, asked: [] as number[] }));
vi.mock("@devthrottle/client-core/teams/CurrentTeam", async (importActual) => ({
  ...(await importActual<typeof import("@devthrottle/client-core/teams/CurrentTeam")>()),
  retryDelayMs: vi.fn((failuresInARow: number) => {
    rhythm.asked.push(failuresInARow);
    return rhythm.delayMs;
  }),
}));

const reported = vi.hoisted(() => ({ calls: [] as unknown[][] }));
vi.mock("@devthrottle/client-core/errors/reportClientError", async (importActual) => ({
  ...(await importActual<typeof import("@devthrottle/client-core/errors/reportClientError")>()),
  reportClientError: vi.fn((...args: unknown[]) => {
    reported.calls.push(args);
  }),
}));

import { act, screen, waitFor, within, fireEvent } from "@testing-library/react";
import { GatewayError } from "@devthrottle/client-core/api/client";
import { getMyTeams } from "@devthrottle/client-core/teams/teamsClient";
import { getMentorPage } from "@devthrottle/client-core/teams/mentorClient";
import { AppShell } from "./AppShell";
import { resetFactorySwitchCache } from "./factory/useFactorySwitch";
import { getFactoryAgentsSwitch } from "@devthrottle/client-core/factory/factoryAgentsClient";
import { currentTeamStorageKey } from "@devthrottle/client-core/teams/CurrentTeam";

function railLabels(): string[] {
  const nav = document.querySelector(".nav");
  if (nav === null) throw new Error("the shell rendered no rail");
  return Array.from(nav.querySelectorAll(".nav-link-label")).map((el) => el.textContent ?? "");
}

/** Open the menu behind your name and answer it. */
function openYourMenu(): HTMLElement {
  fireEvent.click(screen.getByTestId("you-card"));
  return screen.getByTestId("you-menu");
}

describe("Cockpit left rail", () => {
  beforeEach(() => {
    // This project runs vitest without globals, so testing-library's automatic cleanup is not
    // registered - without this, each render leaks into the next test's document.
    cleanup();
    factory.enabled = false;
    resetFactorySwitchCache();
    myTeams.answer = { kind: "teams", teams: [], start: { where: "own-account" } };
    myTeams.held = null;
    vi.clearAllMocks();
    mentorRead.answers.clear();
    mentorRead.calls = [];
    reported.calls = [];
    rhythm.delayMs = 3_600_000;
    rhythm.asked = [];
    window.localStorage.clear();
  });

  it("opens with Sessions, then Fleet Map, then the Fleet Manager", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    expect(railLabels().slice(0, 3)).toEqual(["Sessions", "Fleet Map", "Fleet Manager"]);
  });

  // The owner had the Fleet Manager's red count taken off the rail (7 Oct 2026) until it means what he wants it
  // to mean. Even with work waiting, the row shows no badge.
  it("shows no badge on the Fleet Manager, even when the Gateway counts something waiting", async () => {
    page.waitingCount = 7;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    const entry = screen.getByRole("link", { name: /Fleet Manager/ });
    expect(entry.getAttribute("href")).toBe("/fleet-manager");
    await new Promise((r) => setTimeout(r, 20));
    expect(entry.querySelector(".nav-badge")).toBeNull();
  });

  // The Assistant was removed from the product (the Fleet Manager mission, step 9). No rail entry - main list
  // or foot - may name it or link to its old address.
  it("has no Assistant entry anywhere in the rail", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    const links = [...Array.from(document.querySelectorAll(".nav-list a")), ...Array.from(openYourMenu().querySelectorAll("[role='menuitem']"))];
    expect(links.length).toBeGreaterThan(12);
    expect(links.map((a) => a.textContent ?? "").filter((t) => /assistant/i.test(t))).toEqual([]);
    expect(links.map((a) => a.getAttribute("href")).filter((h) => h === "/assistant")).toEqual([]);
  });

  // THREE LABELLED SECTIONS (owner, 8 Oct 2026, Mockup 1 of the menu mockups): Work, then Set up, then you at the
  // bottom - every row visible, the labels readable.
  it("lays the menu out in labelled sections: Work, then Set up, then you at the bottom", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    const work = screen.getByRole("group", { name: "Work" });
    // The group's name carries whose fleet it is, as the heading shows it.
    const setUp = screen.getByRole("group", { name: "Set up, your fleet" });
    // Reports is in Work for everyone (owner, 8 Oct 2026): on the own account it is the person's own reports.
    expect(Array.from(work.querySelectorAll(".nav-link-label")).map((el) => el.textContent)).toEqual([
      "Sessions", "Fleet Map", "Fleet Manager", "Factories", "History", "Voice Recorder", "Reports",
    ]);
    expect(Array.from(setUp.querySelectorAll(".nav-link-label")).map((el) => el.textContent)).toEqual([
      "Directors", "Skills", "Workflows", "Schedule", "Network",
    ]);
    expect(railLabels()).toEqual([
      "Sessions", "Fleet Map", "Fleet Manager", "Factories", "History", "Voice Recorder", "Reports",
      "Directors", "Skills", "Workflows", "Schedule", "Network",
    ]);
    expect(screen.getByTestId("nav-work-heading").textContent).toBe("Work");
    // Set up names whose fleet it changes: yours, with Personal on screen.
    expect(screen.getByTestId("nav-setup-heading").textContent).toBe("Set upyour fleet");
    expect(screen.getByRole("link", { name: /Network/ }).getAttribute("href")).toBe("/network");
    // No team block with Personal on screen.
    expect(screen.queryByTestId("nav-team")).toBeNull();
  });

  // Owner, 8 Oct 2026: what is about you left the rail for the menu behind your name, and Dictionary and Transcription
  // became tabs of Settings. None of them is a rail row any more. (Network stays, under Set up.)
  it("keeps Account, Phone, Your Throttle, Settings, About, Help, Dictionary and Transcription out of the rail", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    for (const gone of ["Account", "Phone", "Your Throttle", "Settings", "About", "Help", "Dictionary", "Transcription"]) {
      expect(railLabels()).not.toContain(gone);
    }
    // You are at the bottom of the rail, after the navigation.
    const card = screen.getByTestId("you-card");
    expect(document.querySelector(".nav")!.compareDocumentPosition(card) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  // FACTORIES IS ALWAYS SHOWN (owner, 8 Oct 2026): fourth in Work, promoted with the Fleet Manager, whatever the
  // Gateway's factory switch says - the rail no longer asks it. Off, the Factories page says how to start.
  it("always shows Factories, fourth in Work and promoted, without asking the Gateway's factory switch", async () => {
    factory.enabled = false;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    expect(railLabels()[3]).toBe("Factories");
    expect(screen.getByRole("link", { name: /Factories/ }).getAttribute("href")).toBe("/factories");
    const promoted = Array.from(document.querySelectorAll(".nav-link-promoted .nav-link-label")).map((el) => el.textContent);
    expect(promoted).toEqual(["Fleet Manager", "Factories"]);
    await new Promise((r) => setTimeout(r, 20));
    expect(getFactoryAgentsSwitch).not.toHaveBeenCalled();
  });

  // A person with no team on a Gateway with Teams on: the rail has no team block, and the menu behind their name offers
  // Personal - where they are - and Create a team (Mockup B).
  it("shows a person with no team no team block, and Personal and Create a team in their menu", async () => {
    myTeams.answer = { kind: "teams", teams: [], start: { where: "own-account" } };
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId("you-card").textContent).toContain("Personal"));
    expect(screen.queryByTestId("nav-team")).toBeNull();
    const working = within(openYourMenu()).getByRole("group", { name: "Working in" });
    const choices = within(working).getAllByRole("menuitemradio");
    expect(choices).toHaveLength(1);
    expect(choices[0].textContent).toContain("Personal");
    expect(within(working).getByRole("menuitem", { name: "+ Create a team" })).toBeTruthy();
  });

  // Delta review D1: the state production is in - Teams dark, or no team, and nothing remembered in this browser - draws
  // today's Cockpit AT ONCE, before the list of teams answers, and never says "Loading your team".
  it.each([
    ["Teams dark", { kind: "not-offered", reason: "dark" }],
    ["no team", { kind: "teams", teams: [], start: { where: "own-account" } }],
  ] as Array<[string, typeof myTeams.answer]>)("draws today's Cockpit at once with nothing remembered (%s), before the teams answer", async (_name, answer) => {
    myTeams.answer = answer;
    let release: () => void = () => {};
    myTeams.held = new Promise<void>((r) => (release = r));
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    // Not answered yet, and the whole rail is already there.
    expect(railLabels().slice(0, 3)).toEqual(["Sessions", "Fleet Map", "Fleet Manager"]);
    expect(screen.getByTestId("you-card")).toBeTruthy();
    expect(screen.queryByText("Loading your team...")).toBeNull();

    await act(async () => release());
    await new Promise((r) => setTimeout(r, 20));
    expect(railLabels().slice(0, 3)).toEqual(["Sessions", "Fleet Map", "Fleet Manager"]);
    expect(screen.queryByText("Loading your team...")).toBeNull();
    expect(screen.queryByTestId("nav-team")).toBeNull();
  });

  // A Gateway with Teams dark shows nothing about teams at all: no Working in, no Personal, no team block.
  it("shows nothing about teams on a Gateway that has not turned Teams on", async () => {
    myTeams.answer = { kind: "not-offered", reason: "dark" };
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 20));
    expect(screen.getByTestId("you-card").textContent).not.toContain("Personal");
    const menu = openYourMenu();
    expect(within(menu).queryByRole("group", { name: "Working in" })).toBeNull();
    expect(within(menu).queryByText("Your teams could not be read just now.")).toBeNull();
    expect(screen.queryByTestId("nav-team")).toBeNull();
  });

  // Review finding F1: a person with no team sees no change even when the read of their teams fails.
  it("shows nothing about teams to a person with no team when the read of their teams fails", async () => {
    myTeams.answer = new Error("Gateway restarting");
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 20));
    const menu = openYourMenu();
    expect(within(menu).queryByRole("group", { name: "Working in" })).toBeNull();
    expect(within(menu).queryByText("Your teams could not be read just now.")).toBeNull();
    expect(screen.queryByTestId("nav-team")).toBeNull();
  });

  it("moves the team switch into the menu behind your name, with Personal where Your own account was", async () => {
    myTeams.answer = {
      kind: "teams",
      teams: [
        {
          id: "t1",
          name: "DevThrottle",
          role: "Owner",
          memberCount: 5,
          people: "5 people",
          app: { full: true, pages: [], landing: null, elsewhere: null },
        },
      ],
      start: { where: "own-account" },
    };
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId("you-card").textContent).toContain("Personal"));
    // No box at the top of the rail any more.
    expect(screen.queryByTestId("team-switcher")).toBeNull();
    const working = within(openYourMenu()).getByRole("group", { name: "Working in" });
    const choices = within(working).getAllByRole("menuitemradio");
    expect(choices.map((c) => c.getAttribute("aria-checked"))).toEqual(["true", "false"]);
    expect(choices[0].textContent).toContain("Personal");
    expect(choices[1].textContent).toContain("DevThrottle");
    expect(choices[1].textContent).toContain("Owner - 5 people");

    // Picking the team puts it on screen: the card says so, and the rail grows the team's block under its name.
    fireEvent.click(choices[1]);
    await waitFor(() => expect(screen.getByTestId("you-card").textContent).toContain("DevThrottle - Owner"));
    const block = screen.getByTestId("nav-team");
    expect(block.textContent).toContain("DevThrottle");
    expect(within(block).getByRole("link", { name: /Team/ }).getAttribute("href")).toBe("/settings?tab=members");
  });

  describe("the Mentor entry", () => {
    const TEAM = { id: "team-test", name: "Teams test", role: "Developer", memberCount: 2, people: "2 people", app: { full: true, pages: [], landing: null, elsewhere: null } } as TeamSummary;
    const OTHER = { id: "team-other", name: "Other team", role: "Manager", memberCount: 4, people: "4 people", app: { full: true, pages: [], landing: null, elsewhere: null } } as TeamSummary;

    function renderOn(teams: (typeof TEAM)[], chosen: string | null) {
      myTeams.answer = { kind: "teams", teams, start: { where: "own-account" } };
      if (chosen !== null) window.localStorage.setItem(currentTeamStorageKey(), chosen);
      render(
        <MemoryRouter initialEntries={["/sessions"]}>
          <AppShell />
        </MemoryRouter>,
      );
    }

    // The absence tests below wait until the shell has READ the teams and every Mentor read has settled, so "no entry"
    // is never just "not drawn yet" (review F6).
    async function settled() {
      await Promise.all(vi.mocked(getMyTeams).mock.results.map((r) => r.value).map((p) => Promise.resolve(p).catch(() => undefined)));
      await Promise.all(vi.mocked(getMentorPage).mock.results.map((r) => Promise.resolve(r.value).catch(() => undefined)));
      await new Promise((r) => setTimeout(r, 0));
    }

    // Mockup 1 with a team selected: the team's block, headed by its name, sits right under Work and above Set up,
    // and Set up says it changes the team's fleet.
    it("puts the team's block between Work and Set up, under the team's name", async () => {
      mentorRead.answers.set(TEAM.id, { kind: "page" });
      renderOn([TEAM], TEAM.id);

      const block = await screen.findByTestId("nav-team");
      expect(screen.getByTestId("nav-team-heading").textContent).toBe(TEAM.name);
      const work = screen.getByTestId("nav-work");
      const setUp = screen.getByTestId("nav-setup");
      expect(work.compareDocumentPosition(block) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(block.compareDocumentPosition(setUp) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(screen.getByTestId("nav-setup-heading").textContent).toBe("Set upthe team's fleet");
    });

    it("is offered in Work, after Voice Recorder, when the Gateway answers the team's Mentor read with a page", async () => {
      mentorRead.answers.set(TEAM.id, { kind: "page" });
      renderOn([TEAM], TEAM.id);

      await waitFor(() => expect(railLabels()).toContain("Mentor"));
      const work = screen.getByTestId("nav-work");
      const workLabels = Array.from(work.querySelectorAll(".nav-link-label")).map((el) => el.textContent);
      expect(workLabels[workLabels.indexOf("Voice Recorder") + 1]).toBe("Mentor");
      expect(within(screen.getByTestId("nav-team")).queryByRole("link", { name: /Mentor/ })).toBeNull();
      expect(screen.getByRole("link", { name: /Mentor/ }).getAttribute("href")).toBe("/mentor");
      expect(mentorRead.calls).toEqual([TEAM.id]);
    });

    // Screen 2: with a team on screen, Reports is in Work - the team's page, where the Gateway's verdict lists it - and
    // the team's block keeps only Team, Questions and Requests.
    it("puts the team's Reports in Work and keeps Team, Questions and Requests in the team's block", async () => {
      const pages = [
        { id: "questions", label: "Questions", path: "/questions", countPath: null },
        { id: "requests", label: "Requests", path: "/requests", countPath: null },
        { id: "reports", label: "Reports", path: "/reports", countPath: null },
      ];
      const withPages = { ...TEAM, app: { ...TEAM.app, pages } } as TeamSummary;
      mentorRead.answers.set(TEAM.id, { kind: "page" });
      renderOn([withPages], TEAM.id);

      await waitFor(() => expect(railLabels()).toContain("Mentor"));
      const work = Array.from(screen.getByTestId("nav-work").querySelectorAll(".nav-link-label")).map((el) => el.textContent);
      expect(work.slice(-3)).toEqual(["Voice Recorder", "Mentor", "Reports"]);
      const block = Array.from(screen.getByTestId("nav-team").querySelectorAll(".nav-link-label")).map((el) => el.textContent);
      expect(block).toEqual(["Team", "Questions", "Requests"]);
    });

    it("leaves Reports out of Work when the team's verdict does not list it", async () => {
      mentorRead.answers.set(TEAM.id, { kind: "refused" });
      renderOn([TEAM], TEAM.id);

      await screen.findByTestId("nav-team");
      await settled();
      const work = Array.from(screen.getByTestId("nav-work").querySelectorAll(".nav-link-label")).map((el) => el.textContent);
      expect(work).not.toContain("Reports");
    });

    it.each([
      ["refused (a Collaborator)", { kind: "refused" }],
      ["not offered (no such team)", { kind: "not-offered" }],
    ])("is hidden when the Gateway's answer is %s", async (_name, answer) => {
      mentorRead.answers.set(TEAM.id, answer);
      renderOn([TEAM], TEAM.id);

      await waitFor(() => expect(mentorRead.calls).toEqual([TEAM.id]));
      await settled();
      expect(railLabels()).not.toContain("Mentor");
      expect(reported.calls).toEqual([]);
    });

    it.each([
      ["a network failure", new TypeError("Failed to fetch")],
      ["a Gateway fault", new GatewayError(500, "fault")],
      ["an answer that breaks the contract", new GatewayError(502, "The Gateway sent a Mentor page the Cockpit cannot read")],
    ])("is SHOWN, and the failure reported, on %s", async (_name, failure) => {
      mentorRead.answers.set(TEAM.id, failure);
      renderOn([TEAM], TEAM.id);

      await waitFor(() => expect(railLabels()).toContain("Mentor"));
      expect(reported.calls).toHaveLength(1);
      expect(reported.calls[0][0]).toBe("cockpit-mentor-entry");
      expect(reported.calls[0][3]).toBe(failure);
    });

    it("asks again on the shell's rhythm after a failure, and hides the entry when the Gateway then refuses", async () => {
      // One failed read at load must not leave a Collaborator - or someone just removed - with the entry for good.
      rhythm.delayMs = 5;
      mentorRead.answers.set(TEAM.id, [new GatewayError(500, "fault"), { kind: "refused" }]);
      renderOn([TEAM], TEAM.id);

      await waitFor(() => expect(railLabels()).toContain("Mentor"));
      await waitFor(() => expect(railLabels()).not.toContain("Mentor"));
      expect(mentorRead.calls).toEqual([TEAM.id, TEAM.id]);
      expect(rhythm.asked).toEqual([1]);
      expect(reported.calls).toHaveLength(1);
    });

    it("keeps asking, one failure later each time, until the Gateway answers - then stops", async () => {
      rhythm.delayMs = 5;
      mentorRead.answers.set(TEAM.id, [new TypeError("Failed to fetch"), new GatewayError(503, "restarting"), { kind: "page" }]);
      renderOn([TEAM], TEAM.id);

      await waitFor(() => expect(mentorRead.calls).toHaveLength(3));
      await new Promise((r) => setTimeout(r, 50));
      expect(mentorRead.calls).toHaveLength(3);
      expect(rhythm.asked).toEqual([1, 2]);
      expect(railLabels()).toContain("Mentor");
    });

    it("reports a failure under the route the person is on", async () => {
      mentorRead.answers.set(TEAM.id, new GatewayError(500, "fault"));
      renderOn([TEAM], TEAM.id);

      await waitFor(() => expect(reported.calls).toHaveLength(1));
      expect(reported.calls[0][1]).toBe(window.location.pathname);
      expect(reported.calls[0][1]).not.toBe("rail");
    });

    it("follows the team now on screen, not an answer for the team it left", async () => {
      // The first team's read is still in flight when the person switches team; its late refusal must not hide the
      // entry the second team's answer offered.
      let settleFirst: (answer: unknown) => void = () => {};
      mentorRead.answers.set(TEAM.id, new Promise((resolve) => (settleFirst = resolve)));
      mentorRead.answers.set(OTHER.id, { kind: "page" });
      renderOn([TEAM, OTHER], TEAM.id);

      await waitFor(() => expect(mentorRead.calls).toEqual([TEAM.id]));
      fireEvent.click(within(openYourMenu()).getByRole("menuitemradio", { name: /Other team/ }));
      await waitFor(() => expect(railLabels()).toContain("Mentor"));

      settleFirst({ kind: "refused" });
      await settled();
      expect(railLabels()).toContain("Mentor");
      expect(mentorRead.calls).toEqual([TEAM.id, OTHER.id]);
    });

    // Screen 1: on the person's own account the Mentor is theirs, offered when the Gateway answers the personal read
    // with a page, and placed in Work after Voice Recorder, before Reports.
    it("is offered in Work on the person's own account when the Gateway answers the personal read with a page", async () => {
      mentorRead.answers.set(OWN, { kind: "page" });
      renderOn([], null);

      await waitFor(() => expect(railLabels()).toContain("Mentor"));
      const work = Array.from(screen.getByTestId("nav-work").querySelectorAll(".nav-link-label")).map((el) => el.textContent);
      expect(work.slice(-3)).toEqual(["Voice Recorder", "Mentor", "Reports"]);
      expect(mentorRead.calls).toEqual([OWN]);
      expect(screen.queryByTestId("nav-team")).toBeNull();
    });

    it("is not offered on the own account when the Gateway has no personal page", async () => {
      mentorRead.answers.set(OWN, { kind: "not-offered" });
      renderOn([], null);

      await waitFor(() => expect(mentorRead.calls).toEqual([OWN]));
      await settled();
      expect(railLabels()).not.toContain("Mentor");
      expect(reported.calls).toEqual([]);
    });

    it("is not offered on a Gateway that has not turned Teams on", async () => {
      myTeams.answer = { kind: "not-offered", reason: "dark" };
      mentorRead.answers.set(OWN, { kind: "not-offered" });
      render(
        <MemoryRouter initialEntries={["/sessions"]}>
          <AppShell />
        </MemoryRouter>,
      );

      await settled();
      await waitFor(() => expect(mentorRead.calls).toEqual([OWN]));
      expect(vi.mocked(getMyTeams)).toHaveBeenCalled();
      expect(railLabels()).not.toContain("Mentor");
    });
  });
});
