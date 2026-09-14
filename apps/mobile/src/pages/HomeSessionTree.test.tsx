// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { REACHABILITY_ONLINE } from "@devthrottle/client-core/fleet/fleetClient";
import { resetCrewExpandedForTests } from "@devthrottle/client-core/sessions/tree";
import { Home } from "./Home";

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
    // The deep Worker is stepped in one level further than the Manager.
    expect((links[1] as HTMLElement).style.paddingLeft).toBe("30px");
    expect((links[2] as HTMLElement).style.paddingLeft).toBe("46px");
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

  it("renders both members of an ownership loop as cards", async () => {
    const a = session({ sessionId: "a", number: 1, name: "Rule Factory - Architect loop a", sortOrder: 0, controllerSessionId: "b" });
    const b = session({ sessionId: "b", number: 2, name: "loop b", sortOrder: 1, controllerSessionId: "a" });
    await renderHome([alone, a, b]);
    // Both loop members are working roots, so they take their desktop order (0, 1) ahead of the wingman (12).
    expect(cardNames()).toEqual(["Rule Factory - Architect loop a", "loop b", "devthrottle_internal - wingman"]);
    expect(screen.queryByRole("button", { name: /Expand/ })).toBeNull();
  });
});
