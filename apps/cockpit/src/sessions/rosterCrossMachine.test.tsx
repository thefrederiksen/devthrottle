// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, cleanup, fireEvent, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { resetCrewExpandedForTests } from "@devthrottle/client-core/sessions/tree";

// EVERY MACHINE SHOWS EVERY SESSION ON IT (owner ruling, 2026-10-25). Session 113 ran on the Mac Mini, started by 114
// on devlinux, and the Mac's group did not list it. In "My order" a child on another machine now has its full row
// under its own machine, saying who started it, and its crew keeps a placeholder for it.

vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  ...(({ GatewayError, gatewayErrorMessage, authHeaders }) => ({ GatewayError, gatewayErrorMessage, authHeaders }))(
    await importOriginal<typeof import("@devthrottle/client-core/api/client")>(),
  ),
  gatewayErrorMessage: (err: unknown) => String(err),
  setVoiceModeAllSessions: vi.fn(async () => {}),
}));

vi.mock("./SessionMenu", () => ({
  SessionMenu: () => null,
}));

import { SessionRoster, type RosterView } from "./SessionRoster";

function session(fields: Partial<SessionDto> & { sessionId: string; name: string }): SessionDto {
  return {
    createdAt: "2026-10-25T08:00:00Z",
    sortOrder: 0,
    directorId: "linux",
    machineName: "devlinux",
    repoPath: "/home/soren/ReposFred/devthrottle_internal",
    effectiveColor: "blue",
    effectiveColorHex: "#3B82F6",
    stateLabel: "Working",
    triageBucket: "active",
    ...fields,
  } as unknown as SessionDto;
}

const rob = session({ sessionId: "s114", number: 114, name: "devthrottle_internal - rob" } as never);
const dev = session({ sessionId: "s111", number: 111, name: "Mac Polish - Developer", controllerSessionId: "s114", sortOrder: 1 } as never);
const inv = session({
  sessionId: "s113",
  number: 113,
  name: "Mac Release - Investigator",
  controllerSessionId: "s114",
  directorId: "mac",
  machineName: "devthrottle-mac-mini",
  repoPath: "/Users/soren",
  sortOrder: 1,
} as never);
const mac = session({ sessionId: "s112", number: 112, name: "devthrottle_internal - mac", directorId: "mac", machineName: "devthrottle-mac-mini" } as never);

function renderRoster(sessions: SessionDto[], view: RosterView) {
  return render(
    <MemoryRouter initialEntries={["/sessions"]}>
      <SessionRoster
        sessions={sessions}
        directors={[]}
        portByDirector={new Map()}
        selectedId={undefined}
        view={view}
        onView={() => {}}
        error={null}
        onNewSession={() => {}}
      />
    </MemoryRouter>,
  );
}

function group(container: HTMLElement, machine: string): HTMLElement {
  const head = Array.from(container.querySelectorAll(".roster-group-name")).find((el) => el.textContent === machine);
  if (!head) throw new Error(`no group ${machine}`);
  return head.closest(".roster-group") as HTMLElement;
}

beforeEach(() => resetCrewExpandedForTests());
afterEach(() => cleanup());

describe("a child on another machine than its parent", () => {
  it("has its full row under its own machine, saying who started it and where", () => {
    const { container } = renderRoster([rob, dev, inv, mac], "my-order");
    const macGroup = group(container, "devthrottle-mac-mini");
    expect(within(macGroup).getByText("Mac Release - Investigator")).toBeTruthy();
    expect(within(macGroup).getByTestId("roster-started-by").textContent).toBe("started by 114 on devlinux");
    expect(macGroup.querySelector('[data-linked-row="s113"]')).not.toBeNull();
  });

  it("keeps a placeholder in its parent's crew, and the crew still counts it once", () => {
    const { container } = renderRoster([rob, dev, inv, mac], "my-order");
    expect(screen.getByText("2 under it: 2 working, 0 stopped, 0 need you")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Expand the 2 sessions under devthrottle_internal - rob" }));
    const crew = screen.getByRole("list", { name: "Sessions under devthrottle_internal - rob" });
    const ghost = within(crew).getByTestId("roster-ghost");
    expect(ghost.getAttribute("data-ghost-of")).toBe("s113");
    expect(ghost.textContent).toContain("runs on devthrottle-mac-mini");
    expect(ghost.getAttribute("href")).toBe("/session/s113");
    // The same-machine member is a full row as before; the cross-machine one is drawn in full exactly once.
    expect(within(crew).getByText("Mac Polish - Developer")).toBeTruthy();
    expect(container.querySelectorAll('[data-linked-row="s113"]')).toHaveLength(1);
  });

  it("leaves a crew on one machine exactly as it was", () => {
    const { container } = renderRoster([rob, dev, mac], "my-order");
    expect(container.querySelector("[data-linked-row]")).toBeNull();
    expect(screen.queryByTestId("roster-started-by")).toBeNull();
  });

  it("stays in its crew, with its machine line, in the Attention view, which does not group by machine", () => {
    const { container } = renderRoster([rob, dev, inv, mac], "attention");
    expect(container.querySelector("[data-linked-row]")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Expand the 2 sessions under devthrottle_internal - rob" }));
    const crew = screen.getByRole("list", { name: "Sessions under devthrottle_internal - rob" });
    expect(within(crew).getByText("Mac Release - Investigator")).toBeTruthy();
    expect(within(crew).queryByTestId("roster-ghost")).toBeNull();
  });

  it("draws the dashed line from the placeholder to the full row - on a plain mount, as the production build runs", async () => {
    // Not StrictMode: a layout effect would read the rail's ref before React attached it, and never draw (review of #4479).
    const rect = (top: number, left: number) => ({ top, left, height: 40, width: 200, bottom: top + 40, right: left + 200, x: left, y: top, toJSON: () => ({}) });
    const original = Element.prototype.getBoundingClientRect;
    Element.prototype.getBoundingClientRect = function (this: Element) {
      if (this.hasAttribute("data-ghost-of")) return rect(100, 30) as DOMRect;
      if (this.hasAttribute("data-linked-row")) return rect(300, 10) as DOMRect;
      return rect(0, 0) as DOMRect;
    };
    try {
      renderRoster([rob, dev, inv, mac], "my-order");
      fireEvent.click(screen.getByRole("button", { name: "Expand the 2 sessions under devthrottle_internal - rob" }));
      const svg = await screen.findByTestId("roster-cross-lines");
      expect(svg.querySelector("path")?.getAttribute("d")).toBe("M30 118 H4 V318 H10");
      // Only as tall as the lowest line, so the overlay never holds the rail's scroll height up.
      expect((svg as unknown as SVGElement).style.height).toBe("320px");
      // Closing the crew removes the placeholder, and with it the line.
      fireEvent.click(screen.getByRole("button", { name: "Collapse the 2 sessions under devthrottle_internal - rob" }));
      await vi.waitFor(() => expect(screen.queryByTestId("roster-cross-lines")).toBeNull());
    } finally {
      Element.prototype.getBoundingClientRect = original;
    }
  });
});
