// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The left rail's ORDER is a product decision, not an accident of the array literal, so it is pinned
// here: the Fleet Manager first (the Fleet Manager mission, step 6 - it replaced the Assistant), then Sessions,
// then Fleet Map. Without this test the order is one careless re-sort away from changing silently - nothing else
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
  answer: { kind: "teams", teams: [] } as
    | { kind: "teams"; teams: Array<{ id: string; name: string; role: string; memberCount: number; people: string }> }
    | { kind: "not-offered"; reason: string }
    | Error,
}));
vi.mock("@devthrottle/client-core/teams/teamsClient", () => ({
  getMyTeams: vi.fn(async () => {
    if (myTeams.answer instanceof Error) throw myTeams.answer;
    return myTeams.answer;
  }),
}));

// The Mentor entry (devthrottle_internal#2305) follows the Gateway's answer to the Mentor read for the team on screen:
// a page offers it, a refusal or a missing team hides it. The rail never decides it from a role label.
const mentorRead = vi.hoisted(() => ({
  answer: { kind: "refused", reason: "A Collaborator has no Mentor page." } as { kind: string; reason?: string },
  calls: [] as string[],
}));
vi.mock("@devthrottle/client-core/teams/mentorClient", () => ({
  getMentorPage: vi.fn(async (teamId: string) => {
    mentorRead.calls.push(teamId);
    return mentorRead.answer;
  }),
}));

import { screen, waitFor } from "@testing-library/react";
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
    myTeams.answer = { kind: "teams", teams: [] };
    mentorRead.answer = { kind: "refused", reason: "A Collaborator has no Mentor page." };
    mentorRead.calls = [];
    window.localStorage.clear();
  });

  it("opens with the Fleet Manager, then Sessions, then Fleet Map", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    expect(railLabels().slice(0, 3)).toEqual(["Fleet Manager", "Sessions", "Fleet Map"]);
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
      "Fleet Manager",
      "Sessions",
      "Fleet Map",
      "History",
      "Directors",
      "Schedule",
      "Workflows",
      // Skills sits immediately after Workflows on purpose: two lists on one shelf (the central
      // skill library, devthrottle_internal issue 995). Nothing else moved.
      "Skills",
      "Dictionary",
      "Voice Recorder",
      "Transcription",
      "Network",
    ]);
  });

  // Website Business Factory: Factory Agents sits after Fleet Map and before History - only while the Gateway's
  // factoryAgents.enabled switch is on. Off, the rail is exactly what it was.
  it("shows Factory Agents after Fleet Map and before History when the Gateway says the area is on", async () => {
    factory.enabled = true;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await waitFor(() => expect(railLabels()).toContain("Factory Agents"));
    expect(railLabels().slice(0, 5)).toEqual(["Fleet Manager", "Sessions", "Fleet Map", "Factory Agents", "History"]);
    expect(screen.getByRole("link", { name: /Factory Agents/ }).getAttribute("href")).toBe("/factory-agents");
  });

  it("has no Factory Agents item when the Gateway says the area is off", async () => {
    factory.enabled = false;
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 20));
    expect(railLabels()).not.toContain("Factory Agents");
  });

  // Teams must change nothing for a person who never joins one: no switcher, and the rail exactly as it was.
  it("shows no team switcher to a person with no team", async () => {
    myTeams.answer = { kind: "teams", teams: [] };
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    await new Promise((r) => setTimeout(r, 20));
    expect(screen.queryByTestId("team-switcher")).toBeNull();
    expect(screen.queryByTestId("team-switcher-error")).toBeNull();
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
      teams: [{ id: "t1", name: "DevThrottle", role: "Owner", memberCount: 5, people: "5 people" }],
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
    function onTeam(role: string) {
      myTeams.answer = {
        kind: "teams",
        teams: [{ id: "team-test", name: "Teams test", role, memberCount: 2, people: "2 people" }],
      };
      window.localStorage.setItem(currentTeamStorageKey(), "team-test");
      render(
        <MemoryRouter initialEntries={["/sessions"]}>
          <AppShell />
        </MemoryRouter>,
      );
    }

    it.each(["Owner", "Manager", "Developer"])("is offered after Skills when the Gateway answers the %s with a page", async (role) => {
      mentorRead.answer = { kind: "page" };
      onTeam(role);

      await waitFor(() => expect(railLabels()).toContain("Mentor"));
      const labels = railLabels();
      expect(labels[labels.indexOf("Skills") + 1]).toBe("Mentor");
      expect(screen.getByRole("link", { name: /Mentor/ }).getAttribute("href")).toBe("/mentor");
      expect(mentorRead.calls).toEqual(["team-test"]);
    });

    it("is not offered to a Collaborator, whom the Gateway refuses", async () => {
      mentorRead.answer = { kind: "refused", reason: "A Collaborator has no Mentor page." };
      onTeam("Collaborator");

      await waitFor(() => expect(mentorRead.calls).toEqual(["team-test"]));
      await new Promise((r) => setTimeout(r, 20));
      expect(railLabels()).not.toContain("Mentor");
    });

    it("is not offered, and nothing is asked, for a person with no team", async () => {
      mentorRead.answer = { kind: "page" };
      myTeams.answer = { kind: "teams", teams: [] };
      render(
        <MemoryRouter initialEntries={["/sessions"]}>
          <AppShell />
        </MemoryRouter>,
      );

      await new Promise((r) => setTimeout(r, 20));
      expect(railLabels()).not.toContain("Mentor");
      expect(mentorRead.calls).toEqual([]);
    });

    it("is not offered on a Gateway that has not turned Teams on", async () => {
      mentorRead.answer = { kind: "page" };
      myTeams.answer = { kind: "not-offered", reason: "dark" };
      render(
        <MemoryRouter initialEntries={["/sessions"]}>
          <AppShell />
        </MemoryRouter>,
      );

      await new Promise((r) => setTimeout(r, 20));
      expect(railLabels()).not.toContain("Mentor");
      expect(mentorRead.calls).toEqual([]);
    });
  });
});
