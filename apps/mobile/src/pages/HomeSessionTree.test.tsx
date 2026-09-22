// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { REACHABILITY_ONLINE } from "@devthrottle/client-core/fleet/fleetClient";
import { resetCrewExpandedForTests } from "@devthrottle/client-core/sessions/tree";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { Home } from "./Home";

// The phone stylesheet, read as text: jsdom does not lay out, so the rules that make the guides
// visible and keep the state readable are asserted as a contract on the file the build ships.
const STYLESHEET = readFileSync(resolve(__dirname, "../styles.css"), "utf8");

// The declarations for a selector, by its exact selector line. The cascade lets a LATER block for
// the same selector override an earlier one, so a contract that read only the first block would
// certify a stylesheet whose effective rule hides the thing. This therefore demands the selector
// appears exactly ONCE and returns that one block; a second block for the same selector fails the
// contract outright, whatever it says.
function rule(selector: string): string {
  const blocks: string[] = [];
  const needle = `\n${selector} {`;
  let from = 0;
  for (;;) {
    const start = STYLESHEET.indexOf(needle, from);
    if (start < 0) break;
    blocks.push(STYLESHEET.slice(start, STYLESHEET.indexOf("}", start)));
    from = start + needle.length;
  }
  if (blocks.length === 0) throw new Error(`No rule for ${selector} in styles.css`);
  if (blocks.length > 1) throw new Error(`${selector} is declared ${blocks.length} times in styles.css; a later block would win the cascade unseen`);
  return blocks[0];
}

// THE LIST IS ALWAYS THE OWNERSHIP TREE (owner ruling, 2026-09-14). On the phone a parent is a card
// with a band along its bottom; the children expand in place as one-line rows. The All tab opens in
// attention order - longest wait on top, then working, then snoozed - and "My order" groups by machine.

vi.mock("@devthrottle/client-core/restart/RestartRequestsPanel", () => ({
  RestartRequestsPanel: () => null,
}));

beforeEach(() => {
  localStorage.clear();
  resetCrewExpandedForTests();
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function session(fields: Partial<SessionDto> & { sessionId: string; name: string }): SessionDto {
  return {
    directorId: "director-one",
    machineName: "SOREN_NORTH",
    agent: "ClaudeCode",
    agentToolDisplay: "Claude Code",
    createdAt: "2026-09-14T16:00:00Z",
    sortOrder: 0,
    activityState: "Working",
    status: "Running",
    effectiveColor: "blue",
    effectiveColorHex: "#3b82f6",
    triageBucket: "active",
    stateLabel: "Working",
    ...fields,
  } as SessionDto;
}

const architect = session({ sessionId: "108", number: 108, name: "Rule Factory - Architect", sortOrder: 2, effectiveColor: "red", effectiveColorHex: "#ef4444", stateLabel: "Needs you", triageBucket: "needsYou", needsYouSince: "2026-09-14T21:39:20Z", machineReachable: true } as Partial<SessionDto> & { sessionId: string; name: string });
const worker = session({ sessionId: "106", number: 106, name: "Rule Factory - Worker - guard", sortOrder: 5, controllerSessionId: "108" });
const stopped = session({ sessionId: "102", number: 102, name: "Rule Factory - Worker - pipeline", sortOrder: 3, controllerSessionId: "108", effectiveColor: "supporting", effectiveColorHex: "#64748b", stateLabel: "Snoozed", triageBucket: "onHold" });
const snoozed = session({ sessionId: "100", number: 100, name: "ownership and parent - docs", sortOrder: 0, effectiveColor: "grey", effectiveColorHex: "#6b7280", stateLabel: "Snoozed", triageBucket: "onHold" });
const alone = session({ sessionId: "112", number: 112, name: "devthrottle_internal - wingman", sortOrder: 12 });
const laptop = session({ sessionId: "121", number: 121, name: "devthrottle_internal - problems", sortOrder: 0, directorId: "director-two", machineName: "SORENLAPTOP", effectiveColor: "red", effectiveColorHex: "#ef4444", stateLabel: "Needs you", triageBucket: "needsYou", needsYouSince: "2026-09-14T20:08:27Z", machineReachable: true } as Partial<SessionDto> & { sessionId: string; name: string });

function mockFetch(sessions: SessionDto[]) {
  return vi.fn(async (): Promise<Response> => ({
    ok: true,
    status: 200,
    headers: new Headers(),
    json: async () => ({
      sessions,
      machineErrors: [],
      directors: [
        { directorId: "director-one", state: REACHABILITY_ONLINE },
        { directorId: "director-two", state: REACHABILITY_ONLINE },
      ],
      unreachableBanner: null,
    }),
  }) as Response);
}

function cardNames(): string[] {
  return Array.from(document.querySelectorAll(".row-name")).map((el) => (el.textContent ?? "").replace(/^\d+/, ""));
}

async function renderHome(sessions: SessionDto[]) {
  vi.stubGlobal("fetch", mockFetch(sessions));
  render(
    <MemoryRouter>
      <Home />
    </MemoryRouter>,
  );
  await screen.findByText("Rule Factory - Architect", { exact: false });
}

describe("the phone roster is the ownership tree", () => {
  it("opens in attention order with the crew folded under its parent and the band saying what is under it", async () => {
    await renderHome([snoozed, alone, architect, worker, stopped, laptop]);

    const headings = screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(headings).toEqual(["Needs you", "Working", "Snoozed"]);
    // Longest wait first: 121 (since 20:08) before 108 (since 21:39). The two under 108 are not cards.
    expect(cardNames()).toEqual([
      "devthrottle_internal - problems",
      "Rule Factory - Architect",
      "devthrottle_internal - wingman",
      "ownership and parent - docs",
    ]);
    const band = screen.getByRole("button", { name: /Expand the 2 sessions under Rule Factory - Architect/ });
    expect(band.textContent).toContain("2 under it: 1 working, 1 stopped, 0 need you");
    expect(band.querySelectorAll(".crew-strip i")).toHaveLength(2);
  });

  it("draws the calm band under the reds: its title, and the judged calm row as a card inside it", async () => {
    const report = session({ sessionId: "130", number: 130, name: "devthrottle - report", sortOrder: 1, activityState: "WaitingForInput", effectiveColor: "cyan", effectiveColorHex: "#06b6d4", stateLabel: "Done - Pushed the branch", triageBucket: "active", verdictState: "judged" } as Partial<SessionDto> & { sessionId: string; name: string });
    await renderHome([alone, report, architect]);

    const headings = screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(headings).toEqual(["Needs you", "Done or carrying on", "Working"]);
    // The red first, then the calm row in its band, then the working session - the calm row is not under Working.
    expect(cardNames()).toEqual(["Rule Factory - Architect", "devthrottle - report", "devthrottle_internal - wingman"]);
  });

  it("expands the crew in place as one-line rows that open the child session", async () => {
    await renderHome([alone, architect, worker, stopped]);
    fireEvent.click(screen.getByRole("button", { name: /Expand the 2 sessions/ }));

    const kids = screen.getByRole("list", { name: "Sessions under Rule Factory - Architect" });
    const links = within(kids).getAllByRole("link");
    expect(links.map((a) => a.textContent)).toEqual([
      "102Rule Factory - Worker - pipelineSnoozed",
      "106Rule Factory - Worker - guardWorking",
    ]);
    expect(links[1].getAttribute("href")).toBe("/session/106");
    expect(screen.getByRole("button", { name: /Collapse the 2 sessions/ })).toBeTruthy();
  });

  it("switches to my order, grouped by machine, and remembers the choice on this device", async () => {
    await renderHome([snoozed, alone, architect, worker, stopped, laptop]);
    fireEvent.click(screen.getByRole("button", { name: "My order" }));

    const headings = screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(headings).toEqual(["SOREN_NORTH", "SORENLAPTOP"]);
    // Each machine in desktop (drag) order; the crew is one card inside its machine's group.
    expect(cardNames()).toEqual([
      "ownership and parent - docs",
      "Rule Factory - Architect",
      "devthrottle_internal - wingman",
      "devthrottle_internal - problems",
    ]);
    expect(localStorage.getItem("dt.mobile.rosterOrder")).toBe("my-order");
  });

  it("keeps a Manager's Workers reachable, stepped in one level, and counts them on the Architect", async () => {
    const manager = session({ sessionId: "M", number: 200, name: "Rule Factory - Manager", sortOrder: 6, controllerSessionId: "108" });
    const deep = session({ sessionId: "D", number: 201, name: "Rule Factory - Worker - deep", sortOrder: 7, controllerSessionId: "M" });
    await renderHome([architect, manager, deep, worker]);

    const band = screen.getByRole("button", { name: /Expand the 3 sessions under Rule Factory - Architect/ });
    expect(band.textContent).toContain("3 under it: 3 working, 0 stopped, 0 need you");
    fireEvent.click(band);
    const kids = screen.getByRole("list", { name: "Sessions under Rule Factory - Architect" });
    const links = within(kids).getAllByRole("link");
    expect(links.map((a) => a.textContent)).toEqual([
      "106Rule Factory - Worker - guardWorking",
      "200Rule Factory - ManagerWorking",
      "201Rule Factory - Worker - deepWorking",
    ]);
    // No indentation on the phone (the settled design): depth is one guide line per level below the
    // first, inside the gutter, and no row gets an inline padding.
    expect(links.every((a) => (a as HTMLElement).style.paddingLeft === "")).toBe(true);
    expect(links[1].querySelectorAll(".crew-kid-guides i")).toHaveLength(0);
    expect(links[2].querySelectorAll(".crew-kid-guides i")).toHaveLength(1);
  });

  it("nests a Worker on another machine under its Architect and names that machine on its row", async () => {
    const remote = session({ sessionId: "R", number: 300, name: "Rule Factory - Worker - remote", sortOrder: 0, controllerSessionId: "108", directorId: "director-two", machineName: "SORENLAPTOP" });
    await renderHome([alone, architect, remote]);
    fireEvent.click(screen.getByRole("button", { name: "My order" }));

    // Only SOREN_NORTH has a top-level row; the remote Worker is under 108, not a card of its own.
    expect(screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent)).toEqual(["SOREN_NORTH"]);
    fireEvent.click(screen.getByRole("button", { name: /Expand the 1 sessions under Rule Factory - Architect/ }));
    const kids = screen.getByRole("list", { name: "Sessions under Rule Factory - Architect" });
    expect(within(kids).getByText("SORENLAPTOP")).toBeTruthy();
  });

  it("draws the guides with the stylesheet the build ships - hiding them in CSS, or re-declaring the selector later, is caught here", () => {
    // The React half (one <i> per level) is guarded above; this is the visual half. A guide is a
    // 1 pixel bar with a colour, laid out as a flex row inside the row's gutter, and never hidden.
    expect(rule(".crew-kid-guides")).toMatch(/display:\s*flex/);
    expect(rule(".crew-kid-guides")).not.toMatch(/display:\s*none|visibility:\s*hidden|opacity:\s*0\b/);
    expect(rule(".crew-kid-guides i")).toMatch(/width:\s*1px/);
    expect(rule(".crew-kid-guides i")).toMatch(/background:\s*var\(--border\)/);
    expect(rule(".crew-kid-guides i")).not.toMatch(/display:\s*none/);
    expect(rule(".crew-kid-link")).toMatch(/position:\s*relative/);
  });

  it("never shrinks the state or the machine on a phone row - the name wraps instead", () => {
    // A long name used to squeeze "Working" to "W...". The state and machine are fixed-size items;
    // the name is the only flexible one and wraps to at most two lines.
    expect(rule(".crew-kid-state")).toMatch(/flex:\s*0 0 auto/);
    expect(rule(".crew-kid-state")).not.toMatch(/text-overflow|min-width:\s*0/);
    expect(rule(".crew-kid-machine")).toMatch(/flex:\s*0 0 auto/);
    expect(rule(".crew-kid-machine")).not.toMatch(/text-overflow|min-width:\s*0/);
    // The two-line clamp is a co-dependent stack: the clamp count does nothing without the box
    // display, the vertical orientation and hidden overflow. All four are asserted.
    expect(rule(".crew-kid-name")).toMatch(/display:\s*-webkit-box/);
    expect(rule(".crew-kid-name")).toMatch(/-webkit-box-orient:\s*vertical/);
    expect(rule(".crew-kid-name")).toMatch(/-webkit-line-clamp:\s*2/);
    expect(rule(".crew-kid-name")).toMatch(/overflow:\s*hidden/);
    expect(rule(".crew-kid-name")).toMatch(/overflow-wrap:\s*anywhere/);
    expect(rule(".crew-kid-name")).not.toMatch(/white-space:\s*nowrap/);
  });

  it("shows the level number instead of guides once a row is deeper than the guides can draw", async () => {
    const chain = [architect];
    let parent = "108";
    for (let i = 1; i <= 5; i++) {
      chain.push(session({ sessionId: `L${i}`, number: 300 + i, name: `level ${i + 1}`, sortOrder: i, controllerSessionId: parent }));
      parent = `L${i}`;
    }
    await renderHome(chain);
    fireEvent.click(screen.getByRole("button", { name: /Expand the 5 sessions under Rule Factory - Architect/ }));
    const links = within(screen.getByRole("list", { name: "Sessions under Rule Factory - Architect" })).getAllByRole("link");
    // depths 1..5: guides 0,1,2,3 then a level number for depth 5 (and deeper).
    expect(links.map((a) => a.querySelectorAll(".crew-kid-guides i").length)).toEqual([0, 1, 2, 3, 0]);
    expect(links.map((a) => a.querySelector(".crew-kid-depth")?.textContent ?? "")).toEqual(["", "", "", "", "5"]);
  });

  it("names the machine on every edge that crosses one, not just against the crew's root", async () => {
    // Architect on SOREN_NORTH -> Manager on SORENLAPTOP -> Worker back on SOREN_NORTH. The Worker is on
    // the Architect's machine, but not on its OWN parent's, so its row must say SOREN_NORTH.
    const manager = session({ sessionId: "M", number: 200, name: "Rule Factory - Manager", sortOrder: 6, controllerSessionId: "108", directorId: "director-two", machineName: "SORENLAPTOP" });
    const deep = session({ sessionId: "D", number: 201, name: "Rule Factory - Worker - deep", sortOrder: 7, controllerSessionId: "M" });
    await renderHome([architect, manager, deep]);
    fireEvent.click(screen.getByRole("button", { name: /Expand the 2 sessions under Rule Factory - Architect/ }));
    const links = within(screen.getByRole("list", { name: "Sessions under Rule Factory - Architect" })).getAllByRole("link");
    expect(links[0].querySelector(".crew-kid-machine")?.textContent).toBe("SORENLAPTOP");
    expect(links[1].querySelector(".crew-kid-machine")?.textContent).toBe("SOREN_NORTH");
  });

  it("renders both members of an ownership loop as cards", async () => {
    const a = session({ sessionId: "a", number: 1, name: "Rule Factory - Architect loop a", sortOrder: 0, controllerSessionId: "b" });
    const b = session({ sessionId: "b", number: 2, name: "loop b", sortOrder: 1, controllerSessionId: "a" });
    await renderHome([alone, a, b]);
    // Both loop members are working roots, so they take their desktop order (0, 1) ahead of the wingman (12).
    expect(cardNames()).toEqual(["Rule Factory - Architect loop a", "loop b", "devthrottle_internal - wingman"]);
    expect(screen.queryByRole("button", { name: /Expand/ })).toBeNull();
  });
});

describe("the colour legend on the phone roster", () => {
  it("offers the legend under the order switch, and opens it", async () => {
    await renderHome([alone, architect]);

    fireEvent.click(screen.getByRole("button", { name: "What do the colours mean?" }));

    expect(screen.getByRole("dialog", { name: "What the colours mean" })).toBeTruthy();
    expect(rule(".legend-row")).toContain("display: flex");
  });
});

// PINNING (the Fleet Manager mission, step 8): the Gateway's pinned row is the first card in both orders, wearing its
// mark verbatim, with its team folded into its band; the Gateway's heading follows; the rest keep their order.
describe("the phone pins the Fleet Manager first", () => {
  const pin = {
    rank: 0,
    mark: "Fleet Manager (fake mark)",
    title: "Your Fleet Manager. (fake)",
    othersHeading: "Not its own - they ask you (fake heading)",
    handOverLinkLabel: "Hand sessions over... (fake link)",
  };
  const fm = session({ sessionId: "140", number: 140, name: "The Fleet Manager", sortOrder: 9, pin } as Partial<SessionDto> & { sessionId: string; name: string });
  const teamA = session({ sessionId: "141", number: 141, name: "Team member A", sortOrder: 1, controllerSessionId: "140" });
  const teamB = session({ sessionId: "142", number: 142, name: "Team member B", sortOrder: 2, controllerSessionId: "141" });

  it("opens with the pinned card first, marked, its team collapsed, then the heading and the attention order", async () => {
    await renderHome([alone, architect, worker, stopped, teamA, teamB, fm]);

    expect(cardNames()).toEqual([
      "The Fleet ManagerFleet Manager (fake mark)",
      "Rule Factory - Architect",
      "devthrottle_internal - wingman",
    ]);
    const pinned = screen.getByTestId("roster-pinned");
    expect(within(pinned).getByText("Fleet Manager (fake mark)").getAttribute("title")).toBe("Your Fleet Manager. (fake)");
    expect(pinned.querySelector(".row-pinned")).not.toBeNull();
    const band = within(pinned).getByRole("button", { name: /Expand the 2 sessions under The Fleet Manager/ });
    expect(band.textContent).toContain("2 under it: 2 working, 0 stopped, 0 need you");
    const headings = screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(headings).toEqual(["Not its own - they ask you (fake heading)", "Needs you", "Working"]);
    // The phone has no Fleet Manager page, so it offers no hand-over link.
    expect(screen.queryByText("Hand sessions over... (fake link)")).toBeNull();
  });

  it("keeps the pinned card first in my order too, and expands its team in place", async () => {
    await renderHome([alone, architect, teamA, fm]);
    fireEvent.click(screen.getByRole("button", { name: "My order" }));

    expect(cardNames()[0]).toBe("The Fleet ManagerFleet Manager (fake mark)");
    fireEvent.click(screen.getByRole("button", { name: /Expand the 1 sessions under The Fleet Manager/ }));
    const kids = screen.getByRole("list", { name: "Sessions under The Fleet Manager" });
    expect(within(kids).getAllByRole("link").map((a) => a.textContent)).toEqual(["141Team member AWorking"]);
  });

  it("pins nothing the Gateway did not pin", async () => {
    await renderHome([alone, architect, session({ sessionId: "150", name: "Fleet Manager", sortOrder: 1 })]);

    expect(screen.queryByTestId("roster-pinned")).toBeNull();
    expect(document.querySelector(".row-pin-mark")).toBeNull();
  });
});
