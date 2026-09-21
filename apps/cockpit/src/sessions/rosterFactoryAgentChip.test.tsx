// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, cleanup, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { resetCrewExpandedForTests } from "@devthrottle/client-core/sessions/tree";

// Website Business Factory, Screen 6: a session a factory agent started wears a "factory agent" chip in the Sessions
// list, in the Gateway's words (SessionDto.factoryAgent). Every other row wears nothing. Words are fixtures.

vi.mock("@devthrottle/client-core/api/client", () => ({
  gatewayErrorMessage: (err: unknown) => String(err),
  setVoiceModeAllSessions: vi.fn(async () => {}),
}));

vi.mock("./SessionMenu", () => ({
  SessionMenu: () => null,
}));

import { SessionRoster } from "./SessionRoster";

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

const chip = {
  label: "Factory agent (fake)",
  text: "Scout v2 - Website Business (fake)",
  title: "Started as the factory agent Scout v2 (fake title)",
  href: "/factory-agents/website-business/scout",
};

beforeEach(() => resetCrewExpandedForTests());
afterEach(() => cleanup());

describe("the factory agent chip on a session row", () => {
  it("draws the Gateway's chip on the session a factory agent started, and nothing on the others", () => {
    const scout = session({ sessionId: "s-scout", name: "Scout - Morning batch", factoryAgent: chip } as never);
    const plain = session({ sessionId: "s-plain", name: "Plain session", sortOrder: 1 });
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <SessionRoster
          sessions={[scout, plain]}
          directors={[]}
          portByDirector={new Map()}
          selectedId={undefined}
          view="my-order"
          onView={() => {}}
          error={null}
          onNewSession={() => {}}
        />
      </MemoryRouter>,
    );

    const chips = screen.getAllByTestId("roster-factory-agent");
    expect(chips).toHaveLength(1);
    expect(chips[0].getAttribute("title")).toBe("Started as the factory agent Scout v2 (fake title)");
    expect(within(chips[0]).getByText("Factory agent (fake)")).toBeTruthy();
    expect(within(chips[0]).getByText("Scout v2 - Website Business (fake)")).toBeTruthy();
    expect(chips[0].closest(".roster-row")?.textContent).toContain("Scout - Morning batch");
  });
});
