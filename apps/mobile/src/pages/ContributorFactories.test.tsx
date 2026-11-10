// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// A CONTRIBUTOR'S FACTORIES ON THE PHONE ("Watch your factory", piece 6): the same cards and the same watch view the
// Cockpit shows, from client-core. The words below are deliberately odd, so a word the phone wrote for itself would fail.

const state = vi.hoisted(() => ({ teams: [] as unknown[], factories: {} as Record<string, unknown>, watch: null as unknown, seen: [] as string[] }));
vi.mock("@devthrottle/client-core/teams/teamsClient", async (importActual) => ({
  ...(await importActual<object>()),
  getMyTeams: vi.fn(async () => ({ kind: "teams", teams: state.teams, start: { where: "own-account" } })),
}));
vi.mock("@devthrottle/client-core/teams/contributorClient", async (importActual) => ({
  ...(await importActual<object>()),
  getContributorFactories: vi.fn(async (teamId: string) => state.factories[teamId]),
  getContributorWatch: vi.fn(async () => state.watch),
  markWatchExplainerSeen: vi.fn(async (teamId: string, factory: string) => {
    state.seen.push(`${teamId}/${factory}`);
  }),
}));
vi.mock("../components/AppBarAlerts", () => ({ AppBarAlerts: () => null }));

import { forgetMyTeamsRead } from "@devthrottle/client-core/teams/useMyTeamsWithPage";
import { ContributorFactories } from "./ContributorFactories";

const team = {
  id: "acme", name: "Acme", role: "Contributor", memberCount: 2, people: "2 people",
  app: { full: false, pages: [{ id: "factories", label: "Factories~odd", path: "/contributor/factories", countPath: null }], landing: "/contributor/factories", elsewhere: "x" },
};

const view = {
  factoryId: "", title: "Website~odd", layout: "workshop", floorWidth: 4, floorHeight: 3,
  stations: [{ id: "seat-1", label: "Boss", sub: "Plans", isBoss: true, gx: 1, gy: 1, width: 2, height: 1, line: null }],
  lines: [], characters: [{ seat: "seat-1", name: "Boss", colour: "#f0a03c", station: "seat-1", homeGx: 2, homeGy: 2 }],
  steps: [],
  window: { key: "last-24h", label: "Last 24 hours", fromUtc: "2026-11-08T00:00:00Z", toUtc: "2026-11-09T00:00:00Z", fromLocal: "", toLocal: "", choices: [] },
  emptyText: "Nothing~odd", truncatedText: null, notes: [], replayText: null,
};

function at(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/contributor/factories" element={<ContributorFactories />} />
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  state.teams = [team];
  state.seen = [];
  state.factories = {
    acme: {
      teamId: "acme", teamLabel: "Acme~odd", count: 1, intro: "Intro~odd", emptyText: "Empty~odd",
      factories: [{ teamId: "acme", teamLabel: "Acme~odd", factory: "web site", name: "Website~odd", nowText: "Now~odd", needsYouLabel: null, watchLabel: "Watch~odd", order: "1" }],
    },
  };
  vi.stubGlobal("requestAnimationFrame", () => 0);
  vi.stubGlobal("cancelAnimationFrame", () => undefined);
});
afterEach(() => {
  cleanup();
  forgetMyTeamsRead();
  vi.unstubAllGlobals();
});

describe("ContributorFactories (phone)", () => {
  it("ContributorFactories_ShowsTheGatewaysCards_EachWithAWatchButton_AndNoLinkToAPageThePhoneHasNot", async () => {
    at("/contributor/factories");

    const card = await screen.findByTestId("contributor-factory-card");
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Factories~odd");
    expect(screen.getByText("Intro~odd")).toBeTruthy();
    const links = within(card).getAllByRole("link");
    expect(links.map((l) => [l.textContent, l.getAttribute("href")])).toEqual([
      ["Watch~odd", "/contributor/factories?team=acme&factory=web%20site&view=watch"],
    ]);
  });

  it("ContributorFactories_TheWatchAddress_ShowsTheExplainerCardWithExactlyTheViewsWords_ThenThePlayer", async () => {
    state.watch = { ...view, explainer: { paragraphs: ["One~odd.", "Two~odd."], buttonText: "Go~odd" } };

    const { container } = at("/contributor/factories?team=acme&factory=web%20site&view=watch");

    const paragraphs = await screen.findAllByTestId("watch-explainer-paragraph");
    expect(paragraphs.map((p) => p.textContent)).toEqual(["One~odd.", "Two~odd."]);
    expect(screen.getByTestId("watch-explainer-go").textContent).toBe("Go~odd");
    fireEvent.click(screen.getByTestId("watch-explainer-go"));
    expect(container.querySelector(".fwp")).not.toBeNull();
    await waitFor(() => expect(state.seen).toEqual(["acme/web site"]));
    expect(screen.getByRole("link", { name: "Back" }).getAttribute("href")).toBe("/contributor/factories");
  });

  it("ContributorFactories_AlreadySeen_OpensStraightOnThePlayer", async () => {
    state.watch = { ...view, explainer: null };

    const { container } = at("/contributor/factories?team=acme&factory=web%20site&view=watch");

    await waitFor(() => expect(container.querySelector(".fwp")).not.toBeNull());
    expect(screen.queryByTestId("watch-explainer")).toBeNull();
  });

  it("ContributorFactories_TheWatchScreens_ShowTheFactorysNameOnce_InTheAppBar", async () => {
    state.watch = { ...view, explainer: { paragraphs: ["One~odd."], buttonText: "Go~odd" } };

    const { container } = at("/contributor/factories?team=acme&factory=web%20site&view=watch");

    // The card: the name is the app bar's heading and nowhere else as a heading.
    await screen.findByTestId("watch-explainer");
    await waitFor(() => expect(screen.getAllByRole("heading").map((h) => h.textContent)).toEqual([view.title]));
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe(view.title);

    // The player after the button: still once.
    fireEvent.click(screen.getByTestId("watch-explainer-go"));
    expect(container.querySelector(".fwp")).not.toBeNull();
    expect(screen.getAllByRole("heading").map((h) => h.textContent)).toEqual([view.title]);
  });
});
