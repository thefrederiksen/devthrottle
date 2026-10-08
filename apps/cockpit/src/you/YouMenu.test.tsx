// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import type { MyTeamsAnswer, TeamSummary } from "@devthrottle/client-core/teams/teamsClient";

// YOU, AT THE BOTTOM OF THE RAIL (owner, 8 Oct 2026; devthrottle#3681, Mockup B). The card with your name and the menu
// behind it: your email; Working in (Personal and each team, Create a team); Accounts on this browser (each account,
// Add another account); Settings, Usage, Connect your phone, Help, About; and one sign-out that names the account. A
// menu button a keyboard and a screen reader can use: it opens on Enter, Space and the arrow keys, the arrows move
// through it, and Escape or a click outside closes it.

const actions = vi.hoisted(() => ({ switched: [] as string[], signedOut: [] as string[] }));
vi.mock("@devthrottle/client-core/auth/accountActions", () => ({
  switchAccount: vi.fn(async (id: string) => {
    actions.switched.push(id);
  }),
  signOutAccount: vi.fn(async (id: string) => {
    actions.signedOut.push(id);
    return { ok: true };
  }),
}));

import { CurrentTeamProvider } from "@devthrottle/client-core/teams/CurrentTeam";
import { GatewayError } from "@devthrottle/client-core/api/client";
import { YouMenu, initialsFor } from "./YouMenu";

const FULL = { full: true as const, pages: [], landing: null, elsewhere: null };
const TEAM: TeamSummary = { id: "team-1", name: "Soren Test Team", role: "Owner", memberCount: 1, people: "1 person", app: FULL };

function signIn(accounts: Array<{ id: string; email: string }>, active: string) {
  window.localStorage.setItem(
    "cc.accounts",
    JSON.stringify(accounts.map((a) => ({ id: a.id, label: a.email, email: a.email, deviceKey: `key-${a.id}`, installId: `i-${a.id}` }))),
  );
  window.localStorage.setItem("cc.activeAccount", active);
}

function Where() {
  const location = useLocation();
  return <div data-testid="where">{location.pathname + location.search + location.hash}</div>;
}

function mount(
  opts: { answer?: MyTeamsAnswer; fail?: Error; wholeApp?: boolean; collapsed?: boolean; suggestions?: number; onSwitched?: () => void } = {},
) {
  const answer = opts.answer ?? { kind: "teams", teams: [TEAM], start: { where: "own-account" } };
  const fail = opts.fail;
  return render(
    <MemoryRouter initialEntries={["/sessions"]}>
      <CurrentTeamProvider load={() => (fail !== undefined ? Promise.reject(fail) : Promise.resolve(answer))}>
        <Routes>
          <Route path="*" element={<Where />} />
        </Routes>
        <button type="button">outside</button>
        <YouMenu
          collapsed={opts.collapsed ?? false}
          wholeApp={opts.wholeApp ?? true}
          suggestions={opts.suggestions ?? 0}
          onSwitched={opts.onSwitched ?? (() => {})}
        />
      </CurrentTeamProvider>
    </MemoryRouter>,
  );
}

function card(): HTMLElement {
  return screen.getByTestId("you-card");
}

/** Every item of the menu, in order: the plain items and the radio choices alike. */
function menuItems(): HTMLElement[] {
  return Array.from(screen.getByTestId("you-menu").querySelectorAll<HTMLElement>("[role^='menuitem']"));
}

function menuLabels(): string[] {
  return menuItems().map((el) => el.textContent ?? "");
}

describe("you, at the bottom of the rail", () => {
  beforeEach(() => {
    cleanup();
    window.localStorage.clear();
    actions.switched = [];
    actions.signedOut = [];
    vi.clearAllMocks();
    signIn([{ id: "a1", email: "soren@centerconsulting.com" }, { id: "a2", email: "soren@duksrevo.com" }], "a1");
  });

  it("shows your initials, your email and where you are working", async () => {
    mount();
    await waitFor(() => expect(card().textContent).toContain("Personal"));
    expect(card().textContent).toContain("soren@centerconsulting.com");
    expect(card().getAttribute("aria-haspopup")).toBe("menu");
    expect(card().getAttribute("aria-expanded")).toBe("false");
  });

  it("opens the menu in the mockup's order, with one sign-out that names the account", async () => {
    mount();
    await waitFor(() => expect(card().textContent).toContain("Personal"));
    fireEvent.click(card());

    expect(card().getAttribute("aria-expanded")).toBe("true");
    const menu = screen.getByRole("menu", { name: "Your menu" });
    expect(menu.textContent?.startsWith("soren@centerconsulting.com")).toBe(true);
    expect(menuLabels()).toEqual([
      "PPersonal*",
      "STSoren Test TeamOwner - 1 person",
      "+ Create a team",
      "SCsoren@centerconsulting.com*",
      "SDsoren@duksrevo.com",
      "+ Add another account",
      "Settings",
      "Usage (Your Throttle)",
      "Connect your phone",
      "Help",
      "About DevThrottle",
      "Sign out of soren@centerconsulting.com",
    ]);
    expect(within(menu).getAllByRole("menuitem", { name: /^Sign out/ })).toHaveLength(1);
    expect(within(menu).getByRole("menuitem", { name: "Help" }).getAttribute("href")).toBe("https://devthrottle.com/docs");
  });

  it("puts the keyboard in the menu, moves with the arrows, and Escape closes it and gives focus back", async () => {
    mount();
    await waitFor(() => expect(card().textContent).toContain("Personal"));
    card().focus();
    fireEvent.keyDown(card(), { key: "ArrowDown" });

    const menu = screen.getByTestId("you-menu");
    const items = menuItems();
    await waitFor(() => expect(document.activeElement).toBe(items[0]));
    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(document.activeElement).toBe(items[1]);
    fireEvent.keyDown(menu, { key: "End" });
    expect(document.activeElement).toBe(items[items.length - 1]);
    fireEvent.keyDown(menu, { key: "ArrowDown" });
    expect(document.activeElement).toBe(items[0]);
    fireEvent.keyDown(menu, { key: "ArrowUp" });
    expect(document.activeElement).toBe(items[items.length - 1]);

    fireEvent.keyDown(menu, { key: "Escape" });
    expect(screen.queryByTestId("you-menu")).toBeNull();
    expect(document.activeElement).toBe(card());
  });

  // The menu button pattern: ArrowUp on the button opens the menu at its LAST item.
  it("opens at the last item on ArrowUp", async () => {
    mount();
    await waitFor(() => expect(card().textContent).toContain("Personal"));
    card().focus();
    fireEvent.keyDown(card(), { key: "ArrowUp" });

    const items = menuItems();
    await waitFor(() => expect(document.activeElement).toBe(items[items.length - 1]));
    expect(items[items.length - 1].textContent).toBe("Sign out of soren@centerconsulting.com");
  });

  it("follows Help on Space, as every other row answers Space", async () => {
    mount();
    fireEvent.click(card());
    const help = screen.getByRole("menuitem", { name: "Help" });
    const followed = vi.fn((e: Event) => e.preventDefault());
    help.addEventListener("click", followed);

    fireEvent.keyDown(help, { key: " " });
    expect(followed).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId("you-menu")).toBeNull();
  });

  // With a team chosen and the teams unreadable, the menu says so - as a disabled item a screen reader walking the menu
  // reaches, with the reason on screen rather than only in a hover title.
  it("says why your teams could not be read, as an item the keyboard and a screen reader reach", async () => {
    window.localStorage.setItem("devthrottle.currentTeam.a1", TEAM.id);
    mount({ fail: new GatewayError(503, "GET /teams failed: 503", { reason: "The Gateway is restarting." }) });
    await waitFor(() => expect(card().getAttribute("aria-label")).not.toBeNull());
    card().focus();
    fireEvent.keyDown(card(), { key: "ArrowDown" });

    const note = await screen.findByTestId("you-menu-teams-error");
    expect(note.getAttribute("role")).toBe("menuitem");
    expect(note.getAttribute("aria-disabled")).toBe("true");
    expect(note.textContent).toContain("Your teams could not be read just now: The Gateway is restarting.");
    await waitFor(() => expect(document.activeElement).toBe(note));
    expect(screen.queryByRole("group", { name: "Working in" })).toBeNull();
  });

  it("closes on a click outside it", async () => {
    mount();
    fireEvent.click(card());
    expect(screen.getByTestId("you-menu")).toBeTruthy();
    fireEvent.mouseDown(screen.getByRole("button", { name: "outside" }));
    expect(screen.queryByTestId("you-menu")).toBeNull();
  });

  it("picks a team in Working in, and tells the shell which team was on screen before", async () => {
    const onSwitched = vi.fn();
    mount({ onSwitched });
    await waitFor(() => expect(card().textContent).toContain("Personal"));
    fireEvent.click(card());
    fireEvent.click(screen.getByRole("menuitemradio", { name: /Soren Test Team/ }));

    expect(screen.queryByTestId("you-menu")).toBeNull();
    expect(onSwitched).toHaveBeenCalledWith(TEAM, null);
    await waitFor(() => expect(card().textContent).toContain("Soren Test Team - Owner"));
  });

  it("switches the whole account from Accounts on this browser, and never re-opens the one on screen", async () => {
    mount();
    fireEvent.click(card());
    fireEvent.click(screen.getByRole("menuitemradio", { name: /soren@centerconsulting.com/ }));
    expect(actions.switched).toEqual([]);

    fireEvent.click(card());
    fireEvent.click(screen.getByRole("menuitemradio", { name: /soren@duksrevo.com/ }));
    await waitFor(() => expect(actions.switched).toEqual(["a2"]));
  });

  it("adds another account through this shell's own sign-in", async () => {
    mount();
    fireEvent.click(card());
    fireEvent.click(screen.getByRole("menuitem", { name: "+ Add another account" }));
    expect(screen.getByTestId("where").textContent).toBe("/signin");
  });

  it.each([
    ["Settings", "/settings"],
    ["Usage (Your Throttle)", "/settings?tab=usage"],
    ["Connect your phone", "/settings?tab=devices"],
    ["About DevThrottle", "/about"],
    ["+ Create a team", "/settings?tab=account#create-a-team"],
  ])("%s leads to %s", async (label, address) => {
    mount();
    await waitFor(() => expect(card().textContent).toContain("Personal"));
    fireEvent.click(card());
    fireEvent.click(screen.getByRole("menuitem", { name: label }));
    expect(screen.getByTestId("where").textContent).toBe(address);
  });

  it("signs out of the account it names, after asking", async () => {
    mount();
    fireEvent.click(card());
    fireEvent.click(screen.getByRole("menuitem", { name: "Sign out of soren@centerconsulting.com" }));

    const dialog = await screen.findByRole("alertdialog");
    expect(dialog.textContent).toContain("Sign out of soren@centerconsulting.com?");
    expect(dialog.textContent).toContain("Your other accounts on this browser stay signed in");
    expect(actions.signedOut).toEqual([]);
    fireEvent.click(within(dialog).getByRole("button", { name: "Sign out" }));
    await waitFor(() => expect(actions.signedOut).toEqual(["a1"]));
  });

  it("offers only what a pages-only app has: no Settings, Usage, Phone, About or Create a team", async () => {
    mount({ wholeApp: false });
    await waitFor(() => expect(card().textContent).toContain("Personal"));
    fireEvent.click(card());
    const labels = menuLabels();
    for (const gone of ["Settings", "Usage (Your Throttle)", "Connect your phone", "About DevThrottle", "+ Create a team"]) {
      expect(labels).not.toContain(gone);
    }
    expect(labels).toContain("Help");
    expect(labels).toContain("Sign out of soren@centerconsulting.com");
  });

  it("collapsed, shows only your initials - with the same accessible name and the same menu", async () => {
    mount({ collapsed: true });
    await waitFor(() => expect(card().getAttribute("aria-label")).toContain("working in Personal"));
    expect(card().textContent).toBe("SC");
    fireEvent.click(card());
    expect(screen.getByRole("menuitem", { name: "Settings" })).toBeTruthy();
  });

  // Owner, 8 Oct 2026: the count of dictionary suggestions waiting rides on the Settings row (and the Dictionary tab
  // inside Settings) - no row of its own.
  it("carries the dictionary suggestions as a dot on you and a count on the Settings row", async () => {
    mount({ suggestions: 3 });
    expect(card().querySelector(".you-dot")).not.toBeNull();
    fireEvent.click(card());
    expect(screen.queryByRole("menuitem", { name: /Dictionary/ })).toBeNull();
    const settings = screen.getByRole("menuitem", { name: /^Settings/ });
    expect(settings.querySelector(".you-menu-badge")?.textContent).toBe("3");
    fireEvent.click(settings);
    expect(screen.getByTestId("where").textContent).toBe("/settings");
  });

  it("shows no count on the Settings row when nothing is waiting", async () => {
    mount({ suggestions: 0 });
    expect(card().querySelector(".you-dot")).toBeNull();
    fireEvent.click(card());
    expect(screen.getByRole("menuitem", { name: "Settings" }).querySelector(".you-menu-badge")).toBeNull();
  });

  it("names the account's initials from the email", () => {
    expect(initialsFor("soren.frederiksen@mindzie.com")).toBe("SF");
    expect(initialsFor("soren@duksrevo.com")).toBe("SD");
    expect(initialsFor("Account 2")).toBe("A2");
    expect(initialsFor("Soren.Test.Team")).toBe("ST");
  });
});
