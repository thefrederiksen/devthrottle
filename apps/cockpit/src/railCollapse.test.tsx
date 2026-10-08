// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup, screen, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// THE RAIL COLLAPSES TO ITS ICONS, AND LOSES NOTHING (issue #3074).
//
// A collapsed rail is the SAME navigation with the words hidden. That is the claim these tests hold to,
// because the easy way to write this feature is the wrong one: hide the labels with display:none and the
// rail shrinks beautifully while every row stops having an accessible name, so a screen reader announces
// twelve links called nothing. And a rail that drops its attention badge when it narrows is a way to stop
// being told something needs you.

vi.mock("@devthrottle/client-core/net/useKeepWarm", () => ({ useKeepWarm: () => {} }));
const dictionary = vi.hoisted(() => ({ suggestions: 0 }));
vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({
  getSuggestionCount: vi.fn(async () => dictionary.suggestions),
}));
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({ resumePendingDictations: vi.fn(async () => {}) }));
vi.mock("./network/CockpitStatusPill", () => ({ CockpitStatusPill: () => <div data-testid="status-pill" /> }));

const page = vi.hoisted(() => ({ waitingCount: 0 }));
vi.mock("@devthrottle/client-core/fleetmanager/pageClient", () => ({
  getFleetManagerPage: vi.fn(async () => ({ waitingCount: page.waitingCount })),
}));

vi.mock("@devthrottle/client-core/teams/teamsClient", () => ({
  getMyTeams: vi.fn(async () => ({ kind: "teams", teams: [], start: { where: "own-account" } })),
}));

import { AppShell } from "./AppShell";

function mount() {
  return render(
    <MemoryRouter initialEntries={["/sessions"]}>
      <AppShell />
    </MemoryRouter>,
  );
}

function railLinkNames(): string[] {
  return screen.getAllByRole("link").map((el) => el.textContent?.trim() ?? "");
}

describe("the Cockpit rail collapse", () => {
  beforeEach(() => {
    cleanup();
    window.localStorage.clear();
    page.waitingCount = 0;
    dictionary.suggestions = 0;
  });

  it("opens expanded, and the toggle says so", () => {
    mount();

    const toggle = screen.getByTestId("rail-toggle");
    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    expect(document.querySelector(".shell-rail-collapsed")).toBeNull();
    expect(screen.getByText("DevThrottle")).toBeTruthy();
  });

  it("keeps every destination, and every destination's name, when it collapses", () => {
    mount();
    const expanded = railLinkNames();
    expect(expanded).toContain("Sessions");

    fireEvent.click(screen.getByTestId("rail-toggle"));

    // The shell is narrow now...
    expect(document.querySelector(".shell-rail-collapsed")).toBeTruthy();
    expect(screen.getByTestId("rail-toggle").getAttribute("aria-expanded")).toBe("false");
    // ...and the list is the same list. The labels are still the links' accessible names - they are hidden
    // by CSS from the eye, not removed from the page - so this is the identical array, not a shorter one.
    expect(railLinkNames()).toEqual(expanded);
    // The one row that names the app, and the status pill, are the only things that go.
    expect(screen.queryByText("DevThrottle")).toBeNull();
    expect(screen.queryByTestId("status-pill")).toBeNull();
  });

  // Dictionary is a tab of Settings now (owner, 8 Oct 2026), so its attention signal rides on YOU: a dot on your
  // initials, which is all of you a collapsed rail shows, and the count on a row of the menu behind your name.
  it("still shows the attention count when collapsed", async () => {
    dictionary.suggestions = 7;
    mount();

    fireEvent.click(screen.getByTestId("rail-toggle"));

    const card = screen.getByTestId("you-card");
    await waitFor(() => expect(card.querySelector(".you-dot")).not.toBeNull());
    fireEvent.click(card);
    const row = screen.getByRole("menuitem", { name: /Dictionary suggestions/ });
    expect(row.querySelector(".you-menu-badge")?.textContent).toBe("7");
  });

  // You stay in reach when the rail collapses: the card keeps its accessible name, shows only your initials, and opens
  // the same menu, placed outside the narrow rail.
  it("keeps you at the bottom when collapsed, as your initials, and opens the same menu", () => {
    mount();
    fireEvent.click(screen.getByTestId("rail-toggle"));

    const card = screen.getByTestId("you-card");
    expect(card.getAttribute("aria-label")).toContain("Open your menu");
    expect(card.querySelector(".you-text")).toBeNull();
    fireEvent.click(card);
    expect(screen.getByRole("menu", { name: "Your menu" })).toBeTruthy();
    expect(screen.getByRole("menuitem", { name: "Settings" })).toBeTruthy();
  });

  it("remembers the choice for the next time this browser opens the Cockpit", () => {
    mount();
    fireEvent.click(screen.getByTestId("rail-toggle"));
    expect(window.localStorage.getItem("cockpit.railCollapsed")).toBe("true");

    cleanup();
    mount();

    expect(document.querySelector(".shell-rail-collapsed")).toBeTruthy();
    expect(screen.getByTestId("rail-toggle").getAttribute("aria-expanded")).toBe("false");
  });

  it("expands again, and remembers that too", () => {
    window.localStorage.setItem("cockpit.railCollapsed", "true");
    mount();

    fireEvent.click(screen.getByTestId("rail-toggle"));

    expect(document.querySelector(".shell-rail-collapsed")).toBeNull();
    expect(window.localStorage.getItem("cockpit.railCollapsed")).toBe("false");
  });
});
