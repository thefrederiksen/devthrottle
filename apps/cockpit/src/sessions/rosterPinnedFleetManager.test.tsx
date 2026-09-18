// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, cleanup, fireEvent, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { resetCrewExpandedForTests } from "@devthrottle/client-core/sessions/tree";

// PINNING (the Fleet Manager mission, step 8). The Gateway pins the Fleet Manager (SessionDto.pin) and writes its
// words; the Cockpit draws that row first in both views, wearing the mark, with its team collapsed under it, then the
// Gateway's heading, then every other row exactly where it was. All words below are fixtures, rendered verbatim.

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
    createdAt: "2026-09-16T08:00:00Z",
    sortOrder: 0,
    directorId: "d1",
    machineName: "WORKSTATION-A",
    repoPath: "/src/widgets",
    effectiveColor: "blue",
    effectiveColorHex: "#3B82F6",
    stateLabel: "Working",
    triageBucket: "active",
    ...fields,
  } as unknown as SessionDto;
}

const pin = {
  rank: 0,
  mark: "Fleet Manager (fake mark)",
  title: "Your Fleet Manager. (fake)",
  othersHeading: "Not its own - they ask you (fake heading)",
  handOverLinkLabel: "Hand sessions over... (fake link)",
};
const handTo = { to: "fleet-manager", label: "Hand to the Fleet Manager", title: "t", busyLabel: "b" };

const early = session({ sessionId: "s-early", name: "Early session", sortOrder: 1, ownerChange: handTo } as never);
const red = session({ sessionId: "s-red", name: "Red session", sortOrder: 2, effectiveColor: "red", effectiveColorHex: "#EF4444", stateLabel: "Needs you", triageBucket: "needsYou", needsYouSince: "2026-09-16T09:00:00Z", ownerChange: handTo } as never);
// The Fleet Manager sorts LAST by desktop order and is not red: only the Gateway's pin puts it first.
const fm = session({ sessionId: "s-fm", name: "The Fleet Manager", sortOrder: 9, effectiveColor: "cyan", effectiveColorHex: "#06B6D4", stateLabel: "Done", pin } as never);
const teamA = session({ sessionId: "s-team-a", name: "Team member A", sortOrder: 3, controllerSessionId: "s-fm" });
const teamB = session({ sessionId: "s-team-b", name: "Team member B", sortOrder: 4, controllerSessionId: "s-team-a" });

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

describe("the Cockpit pins the Fleet Manager first", () => {
  for (const view of ["my-order", "attention"] as RosterView[]) {
    it(`draws the pinned row first, marked, with its team collapsed, then the heading and the rest (${view})`, () => {
      const { container } = renderRoster([early, red, teamA, teamB, fm], view);

      expect(rowNames(container)[0]).toBe("The Fleet Manager");
      expect(rowNames(container)).toHaveLength(3);
      expect(rowNames(container).slice(1).sort()).toEqual(["Early session", "Red session"]);
      const pinned = screen.getByTestId("roster-pinned");
      const mark = within(pinned).getByText("Fleet Manager (fake mark)");
      expect(mark.getAttribute("title")).toBe("Your Fleet Manager. (fake)");
      expect(pinned.querySelector(".roster-row-pinned")).not.toBeNull();
      // Its team is under it, collapsed: every level counted, nothing drawn.
      expect(within(pinned).getByRole("button", { name: "Expand the 2 sessions under The Fleet Manager" })).toBeTruthy();
      expect(within(pinned).getByText("2 under it: 2 working, 0 stopped, 0 need you")).toBeTruthy();
      expect(screen.getByTestId("roster-others-head").textContent).toBe("Not its own - they ask you (fake heading)");
      const link = screen.getByTestId("roster-handover-link");
      expect(link.textContent).toBe("Hand sessions over... (fake link)");
      expect(link.getAttribute("href")).toBe("/fleet-manager?handover=1");
      // Only the pinned row wears the mark.
      expect(container.querySelectorAll(".roster-pin-mark")).toHaveLength(1);
    });
  }

  it("keeps the other rows in the order the view gives them", () => {
    const { container } = renderRoster([early, red, fm], "attention");
    // Attention: the red row, then the working one - unchanged by the pin above them.
    expect(rowNames(container)).toEqual(["The Fleet Manager", "Red session", "Early session"]);
  });

  it("expands the team on the chevron", () => {
    renderRoster([early, teamA, teamB, fm], "my-order");

    fireEvent.click(screen.getByRole("button", { name: "Expand the 2 sessions under The Fleet Manager" }));

    const team = screen.getByRole("list", { name: "Sessions under The Fleet Manager" });
    expect(within(team).getByText("Team member A")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Collapse the 2 sessions under The Fleet Manager" })).toBeTruthy();
  });

  it("draws no pin, heading or link when the Gateway pinned nothing, even for a session named Fleet Manager", () => {
    const named = session({ sessionId: "s-named", name: "Fleet Manager", sortOrder: 5 });
    const { container } = renderRoster([early, named], "my-order");

    expect(screen.queryByTestId("roster-pinned")).toBeNull();
    expect(screen.queryByTestId("roster-others-head")).toBeNull();
    expect(screen.queryByTestId("roster-handover-link")).toBeNull();
    expect(container.querySelector(".roster-pin-mark")).toBeNull();
    expect(rowNames(container)).toEqual(["Early session", "Fleet Manager"]);
  });

  it("offers the hand-over link only when a row below offers the hand over", () => {
    const plain = session({ sessionId: "s-plain", name: "Plain", sortOrder: 1 });
    renderRoster([plain, fm], "my-order");

    expect(screen.getByTestId("roster-others-head")).toBeTruthy();
    expect(screen.queryByTestId("roster-handover-link")).toBeNull();
  });
});
