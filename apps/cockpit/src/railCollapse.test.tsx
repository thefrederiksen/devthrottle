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
vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({ getSuggestionCount: vi.fn(async () => 0) }));
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({ resumePendingDictations: vi.fn(async () => {}) }));
vi.mock("./network/CockpitStatusPill", () => ({ CockpitStatusPill: () => <div data-testid="status-pill" /> }));

const page = vi.hoisted(() => ({ waitingCount: 0 }));
vi.mock("@devthrottle/client-core/fleetmanager/pageClient", () => ({
  getFleetManagerPage: vi.fn(async () => ({ waitingCount: page.waitingCount })),
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

  it("still shows the attention count when collapsed", async () => {
    page.waitingCount = 7;
    mount();

    fireEvent.click(screen.getByTestId("rail-toggle"));

    const entry = screen.getByRole("link", { name: /Fleet Manager/ });
    await waitFor(() => expect(entry.querySelector(".nav-badge")?.textContent).toBe("7"));
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
