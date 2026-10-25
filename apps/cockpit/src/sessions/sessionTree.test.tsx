// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, cleanup, fireEvent, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { resetCrewExpandedForTests } from "@devthrottle/client-core/sessions/tree";
// THE CARD'S DETAIL IS BEHIND THE ONE DENSITY SWITCH (owner ruling, 2026-09-20): a card is three lines
// at "clean", and the machine line is drawn at "everything". Setting the switch here is not a
// workaround - it names the position the fact belongs to, and it is the only thing that proves the
// switch reaches the rail at all.
import { resetDensityForTests, setDensity } from "@devthrottle/client-core/sessions/density";

// THE LIST IS ALWAYS THE OWNERSHIP TREE (owner ruling, 2026-09-14). A session that another session
// started sits UNDER that session, collapsed by default, in both views. The collapsed parent carries
// the crew line; the chevron expands it; a child whose supervisor is not in the roster is a top-level
// row again (the dead-supervisor case).

vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  // The error reporter needs the real error helpers; the rest is faked.
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
    createdAt: "2026-09-14T16:00:00Z",
    sortOrder: 0,
    directorId: "d1",
    machineName: "SOREN_NORTH",
    repoPath: "D:\\ReposFred\\devthrottle",
    effectiveColor: "blue",
    effectiveColorHex: "#3B82F6",
    stateLabel: "Working",
    triageBucket: "active",
    ...fields,
  } as unknown as SessionDto;
}

const architect = session({ sessionId: "108", name: "Rule Factory - Architect", sortOrder: 2, effectiveColor: "red", effectiveColorHex: "#EF4444", stateLabel: "Needs you", triageBucket: "needsYou", needsYouSince: "2026-09-14T21:39:20Z" });
const worker = session({ sessionId: "106", name: "Rule Factory - Worker - guard", sortOrder: 5, controllerSessionId: "108" });
const stopped = session({ sessionId: "102", name: "Rule Factory - Worker - pipeline", sortOrder: 3, controllerSessionId: "108", effectiveColor: "supporting", effectiveColorHex: "#64748B", stateLabel: "Snoozed", triageBucket: "onHold" });
const alone = session({ sessionId: "112", name: "devthrottle_internal - wingman", sortOrder: 12 });

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

function rowNames(root: HTMLElement): string[] {
  return Array.from(root.querySelectorAll(".roster-name-text")).map((el) => el.textContent ?? "");
}

beforeEach(() => resetCrewExpandedForTests());
afterEach(() => cleanup());

beforeEach(() => {
  resetDensityForTests();
  setDensity("everything");
});

describe("the Cockpit roster is the ownership tree", () => {
  it("nests supervised sessions under their parent, collapsed, with the crew line, in My order", () => {
    const { container } = renderRoster([alone, architect, worker, stopped], "my-order");

    // Only the two top-level rows are visible; the two under 108 are folded away.
    expect(rowNames(container)).toEqual(["Rule Factory - Architect", "devthrottle_internal - wingman"]);
    expect(screen.getByText("2 under it: 1 working, 1 stopped, 0 need you")).toBeTruthy();
    // The strip carries one dot per child in its stamped colour, so collapsing hides no colour.
    const dots = container.querySelectorAll(".roster-crew-strip i");
    expect(dots).toHaveLength(2);
    expect((dots[0] as HTMLElement).style.backgroundColor).toBe("rgb(100, 116, 139)"); // 102 first, desktop order
    expect((dots[1] as HTMLElement).style.backgroundColor).toBe("rgb(59, 130, 246)");
    // An ordinary row has no chevron.
    expect(container.querySelectorAll(".roster-chevron")).toHaveLength(1);
  });

  it("expands on the chevron, shows the children in their own desktop order, and remembers it", () => {
    const first = renderRoster([alone, architect, worker, stopped], "my-order");
    fireEvent.click(screen.getByRole("button", { name: /Expand the 2 sessions under Rule Factory - Architect/ }));

    const kids = screen.getByRole("list", { name: "Sessions under Rule Factory - Architect" });
    expect(rowNames(kids)).toEqual(["Rule Factory - Worker - pipeline", "Rule Factory - Worker - guard"]);
    expect(within(kids).queryByRole("button", { name: /Expand/ })).toBeNull();
    expect(rowNames(first.container)).toEqual([
      "Rule Factory - Architect",
      "Rule Factory - Worker - pipeline",
      "Rule Factory - Worker - guard",
      "devthrottle_internal - wingman",
    ]);
    // The crew line goes away while expanded - the rows themselves say it.
    expect(screen.queryByText(/under it:/)).toBeNull();
    cleanup();

    // A fresh render of the same roster keeps the crew open: it is remembered on this device.
    const second = renderRoster([alone, architect, worker, stopped], "my-order");
    expect(rowNames(second.container)).toHaveLength(4);
    expect(screen.getByRole("button", { name: /Collapse the 2 sessions/ })).toBeTruthy();
  });

  it("orders the top level by attention and keeps the crew as one row under Needs you", () => {
    const { container } = renderRoster([alone, architect, worker, stopped], "attention");

    const heads = Array.from(container.querySelectorAll(".roster-bucket-head")).map((el) => el.textContent?.trim());
    expect(heads).toEqual(["Needs you 1", "Working 1"]);
    // The stopped child is NOT in a Snoozed section of its own: it is 108's, folded under it.
    expect(rowNames(container)).toEqual(["Rule Factory - Architect", "devthrottle_internal - wingman"]);
    expect(screen.getByText("2 under it: 1 working, 1 stopped, 0 need you")).toBeTruthy();
  });

  it("surfaces a child as a top-level row when its supervisor is gone from the roster", () => {
    const { container } = renderRoster([alone, worker], "my-order");
    expect(rowNames(container)).toEqual(["Rule Factory - Worker - guard", "devthrottle_internal - wingman"]);
    expect(container.querySelectorAll(".roster-chevron")).toHaveLength(0);
  });

  it("keeps a Manager's Workers reachable one level down, and counts them on the Architect", () => {
    const manager = session({ sessionId: "M", name: "Rule Factory - Manager", sortOrder: 6, controllerSessionId: "108" });
    const deep = session({ sessionId: "D", name: "Rule Factory - Worker - deep", sortOrder: 7, controllerSessionId: "M" });
    const { container } = renderRoster([architect, manager, deep, worker], "my-order");

    // The Architect's collapsed line counts every level: 3, not 2.
    expect(screen.getByText("3 under it: 3 working, 0 stopped, 0 need you")).toBeTruthy();
    expect(container.querySelectorAll(".roster-crew-strip i")).toHaveLength(3);
    fireEvent.click(screen.getByRole("button", { name: /Expand the 3 sessions under Rule Factory - Architect/ }));
    // The Manager renders with its own chevron and its own crew line.
    const managerBtn = screen.getByRole("button", { name: /Expand the 1 sessions under Rule Factory - Manager/ });
    expect(managerBtn).toBeTruthy();
    expect(screen.getByText("1 under it: 1 working, 0 stopped, 0 need you")).toBeTruthy();
    fireEvent.click(managerBtn);
    expect(rowNames(container)).toEqual([
      "Rule Factory - Architect",
      "Rule Factory - Worker - guard",
      "Rule Factory - Manager",
      "Rule Factory - Worker - deep",
    ]);
  });

  it("shows a Worker on another Director under its own machine in My order, with a placeholder in its Architect's crew", () => {
    // Owner ruling, 2026-10-25: every machine shows every session on it. The tree is unchanged, so the crew counts it once.
    const remote = session({ sessionId: "R", name: "Rule Factory - Worker - remote", sortOrder: 0, controllerSessionId: "108", directorId: "d2", machineName: "SORENLAPTOP" });
    const { container } = renderRoster([alone, architect, remote], "my-order");

    expect(Array.from(container.querySelectorAll(".roster-group-name")).map((el) => el.textContent)).toEqual(["SOREN_NORTH", "SORENLAPTOP"]);
    expect(rowNames(container)).toEqual(["Rule Factory - Architect", "devthrottle_internal - wingman", "Rule Factory - Worker - remote"]);
    fireEvent.click(screen.getByRole("button", { name: /Expand the 1 sessions under Rule Factory - Architect/ }));
    const kids = screen.getByRole("list", { name: "Sessions under Rule Factory - Architect" });
    expect(within(kids).getByTestId("roster-ghost").textContent).toContain("runs on SORENLAPTOP");
    // The Attention view does not group by machine: the Worker stays in its crew, saying which machine it is on.
    cleanup();
    resetCrewExpandedForTests();
    const att = renderRoster([alone, architect, remote], "attention");
    expect(rowNames(att.container)).toEqual(["Rule Factory - Architect", "devthrottle_internal - wingman"]);
    fireEvent.click(screen.getByRole("button", { name: /Expand the 1 sessions under Rule Factory - Architect/ }));
    const attKids = screen.getByRole("list", { name: "Sessions under Rule Factory - Architect" });
    expect(rowNames(attKids)).toEqual(["Rule Factory - Worker - remote"]);
    expect(within(attKids).getByText("SORENLAPTOP")).toBeTruthy();
  });

  it("labels every machine crossing against the row's own parent, not the crew's root (Attention view)", () => {
    const manager = session({ sessionId: "M", name: "Rule Factory - Manager", sortOrder: 6, controllerSessionId: "108", directorId: "d2", machineName: "SORENLAPTOP" });
    const deep = session({ sessionId: "D", name: "Rule Factory - Worker - deep", sortOrder: 7, controllerSessionId: "M" });
    const { container } = renderRoster([architect, manager, deep], "attention");
    fireEvent.click(screen.getByRole("button", { name: /Expand the 2 sessions under Rule Factory - Architect/ }));
    fireEvent.click(screen.getByRole("button", { name: /Expand the 1 sessions under Rule Factory - Manager/ }));
    // The top-level card always says its machine in this view; under it, the Manager crosses to the laptop and the
    // deep Worker crosses back, each labelled against the row directly above it.
    const machines = Array.from(container.querySelectorAll(".roster-machine")).map((el) => el.textContent);
    expect(machines).toEqual(["SOREN_NORTH", "SORENLAPTOP", "SOREN_NORTH"]);
  });

  it("links each crossing to its own parent in My order: a grandchild back on the root's machine says who started it", () => {
    const manager = session({ sessionId: "M", name: "Rule Factory - Manager", sortOrder: 6, controllerSessionId: "108", directorId: "d2", machineName: "SORENLAPTOP" });
    const deep = session({ sessionId: "D", name: "Rule Factory - Worker - deep", sortOrder: 7, controllerSessionId: "M" });
    const { container } = renderRoster([architect, manager, deep], "my-order");
    expect(container.querySelectorAll("[data-linked-row]")).toHaveLength(2);
    // In machine order: the deep Worker under SOREN_NORTH, started by the Manager; the Manager under SORENLAPTOP.
    expect(Array.from(container.querySelectorAll('[data-testid="roster-started-by"]')).map((el) => el.textContent)).toEqual([
      "started by Rule Factory - Manager on SORENLAPTOP",
      "started by Rule Factory - Architect on SOREN_NORTH",
    ]);
  });

  it("renders both members of an ownership loop as top-level rows", () => {
    const a = session({ sessionId: "a", name: "loop a", sortOrder: 0, controllerSessionId: "b" });
    const b = session({ sessionId: "b", name: "loop b", sortOrder: 1, controllerSessionId: "a" });
    const { container } = renderRoster([alone, a, b], "my-order");
    expect(rowNames(container)).toEqual(["loop a", "loop b", "devthrottle_internal - wingman"]);
    expect(container.querySelectorAll(".roster-chevron")).toHaveLength(0);
  });
});
