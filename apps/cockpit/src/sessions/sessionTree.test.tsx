// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, cleanup, fireEvent, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { resetCrewExpandedForTests } from "@devthrottle/client-core/sessions/tree";

// THE LIST IS ALWAYS THE OWNERSHIP TREE (owner ruling, 2026-09-14). A session that another session
// started sits UNDER that session, collapsed by default, in both views. The collapsed parent carries
// the crew line; the chevron expands it; a child whose supervisor is not in the roster is a top-level
// row again (the dead-supervisor case).

vi.mock("@devthrottle/client-core/api/client", () => ({
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
});
