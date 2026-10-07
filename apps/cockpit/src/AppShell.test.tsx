// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { TeamSummary } from "@devthrottle/client-core/teams/teamsClient";

// The left rail's ORDER is a product decision, not an accident of the array literal, so it is pinned
// here: Sessions first, then Fleet Map, then the Fleet Manager (owner, 7 Oct 2026 - what you use every day first,
// what you set up lower, in the order you build it). Without this test the order is one careless re-sort away from changing silently - nothing else
// in the app reads it.

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

// The person's teams (devthrottle_internal#2312). The switcher at the top of the rail follows the Gateway's answer.
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
}));

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
import { currentTeamStorageKey } from "@devthrottle/client-core/teams/CurrentTeam";

function railLabels(): string[] {
  const list = document.querySelector(".nav-list:not(.nav-list-foot)");
  if (list === null) throw new Error("the shell rendered no main nav list");
  return Array.from(list.querySelectorAll(".nav-link-label")).map((el) => el.textContent ?? "");
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

  it("badges the Fleet Manager with the Gateway's count of what is waiting", async () => {
    page.waitingCount = 7;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    const entry = screen.getByRole("link", { name: /Fleet Manager/ });
    expect(entry.getAttribute("href")).toBe("/fleet-manager");
    await waitFor(() => expect(entry.querySelector(".nav-badge")?.textContent).toBe("7"));
    expect(entry.querySelector(".nav-badge")?.getAttribute("title")).toBe("7 waiting on you");
  });

  it("shows no badge when nothing is waiting", async () => {
    page.waitingCount = 0;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    const entry = screen.getByRole("link", { name: /Fleet Manager/ });
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

    const links = Array.from(document.querySelectorAll(".nav-list a"));
    expect(links.length).toBeGreaterThan(10);
    expect(links.map((a) => a.textContent ?? "").filter((t) => /assistant/i.test(t))).toEqual([]);
    expect(links.map((a) => a.getAttribute("href")).filter((h) => h === "/assistant")).toEqual([]);
  });

  it("leaves the rest of the rail where it was", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    expect(railLabels()).toEqual([
      "Sessions",
      "Fleet Map",
      "Fleet Manager",
      "History",
      "Directors",
      // What you set up, in the order you build it: skills, workflows, the schedule that runs them.
      "Skills",
      "Workflows",
      "Schedule",
      "Dictionary",
      "Voice Recorder",
      "Transcription",
      "Network",
    ]);
  });

  // Factories (once "Factory Agents") sits after Schedule and before Dictionary - only while the Gateway's
  // factoryAgents.enabled switch is on. Off, the rail is exactly what it was.
  it("shows Factories after Schedule and before Dictionary when the Gateway says the area is on", async () => {
    factory.enabled = true;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await waitFor(() => expect(railLabels()).toContain("Factories"));
    expect(railLabels().slice(0, 10)).toEqual([
      "Sessions", "Fleet Map", "Fleet Manager", "History", "Directors", "Skills", "Workflows", "Schedule", "Factories", "Dictionary",
    ]);
    expect(screen.getByRole("link", { name: /Factories/ }).getAttribute("href")).toBe("/factories");
  });

  it("has no Factories item when the Gateway says the area is off", async () => {
    factory.enabled = false;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 20));
    expect(railLabels()).not.toContain("Factories");
  });

  // Teams must change nothing for a person who never joins one: no switcher, and the rail exactly as it was.
  it("shows no team switcher to a person with no team", async () => {
    myTeams.answer = { kind: "teams", teams: [], start: { where: "own-account" } };
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByTestId("team-switcher")).toBeNull();
    expect(screen.queryByTestId("team-switcher-error")).toBeNull();
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
    expect(document.querySelector(".nav-list-foot")).not.toBeNull();
    expect(screen.queryByText("Loading your team...")).toBeNull();

    await act(async () => release());
    await new Promise((r) => setTimeout(r, 20));
    expect(railLabels().slice(0, 3)).toEqual(["Sessions", "Fleet Map", "Fleet Manager"]);
    expect(screen.queryByText("Loading your team...")).toBeNull();
    expect(screen.queryByTestId("team-switcher")).toBeNull();
  });

  it("shows no team switcher on a Gateway that has not turned Teams on", async () => {
    myTeams.answer = { kind: "not-offered", reason: "dark" };
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByTestId("team-switcher")).toBeNull();
    expect(screen.queryByTestId("team-switcher-error")).toBeNull();
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
    expect(screen.queryByTestId("team-switcher")).toBeNull();
    expect(screen.queryByTestId("team-switcher-error")).toBeNull();
  });

  it("puts the team switcher at the top of the rail, above the navigation, for a person in a team", async () => {
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

    const switcher = await screen.findByTestId("team-switcher");
    const nav = document.querySelector(".nav");
    expect(nav).not.toBeNull();
    expect(switcher.compareDocumentPosition(nav!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(switcher.textContent).toContain("DevThrottle - Owner");
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

    it("is offered after Skills when the Gateway answers the Mentor read with a page", async () => {
      mentorRead.answers.set(TEAM.id, { kind: "page" });
      renderOn([TEAM], TEAM.id);

      await waitFor(() => expect(railLabels()).toContain("Mentor"));
      const labels = railLabels();
      expect(labels[labels.indexOf("Skills") + 1]).toBe("Mentor");
      expect(screen.getByRole("link", { name: /Mentor/ }).getAttribute("href")).toBe("/mentor");
      expect(mentorRead.calls).toEqual([TEAM.id]);
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

      const select = within(await screen.findByTestId("team-switcher")).getByRole("combobox");
      await waitFor(() => expect(mentorRead.calls).toEqual([TEAM.id]));
      fireEvent.change(select, { target: { value: OTHER.id } });
      await waitFor(() => expect(railLabels()).toContain("Mentor"));

      settleFirst({ kind: "refused" });
      await settled();
      expect(railLabels()).toContain("Mentor");
      expect(mentorRead.calls).toEqual([TEAM.id, OTHER.id]);
    });

    it("is not offered, and nothing is asked, for a person on their own account with no team", async () => {
      renderOn([], null);

      await settled();
      expect(vi.mocked(getMyTeams)).toHaveBeenCalled();
      expect(railLabels()).not.toContain("Mentor");
      expect(mentorRead.calls).toEqual([]);
    });

    it("is not offered on a Gateway that has not turned Teams on", async () => {
      myTeams.answer = { kind: "not-offered", reason: "dark" };
      render(
        <MemoryRouter initialEntries={["/sessions"]}>
          <AppShell />
        </MemoryRouter>,
      );

      await settled();
      expect(vi.mocked(getMyTeams)).toHaveBeenCalled();
      expect(railLabels()).not.toContain("Mentor");
      expect(mentorRead.calls).toEqual([]);
    });
  });
});
