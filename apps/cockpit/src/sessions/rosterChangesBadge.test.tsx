// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The roster's uncommitted-work badge - the "12 chg" pill the desktop rail has always shown and the
// Cockpit roster did not, because the count never left the desktop window.
//
// The case worth a test is the one that is silent when it is wrong: a session whose git probe has not
// succeeded reports NO count, and that must render as no badge - never as "0 chg", and never as a clean
// tree. An older Director that does not know the field is the same case.

vi.mock("@devthrottle/client-core/api/client", () => ({
  // The restart-requests panel polls inside this roster and reaches for gatewayErrorMessage when a
  // read fails. Without it here, every poll threw an unhandled rejection - the tests still passed, but
  // the run reported errors and exited non-zero, which is how a real failure would come to be ignored.
  gatewayErrorMessage: (err: unknown) => String(err),
  setVoiceModeAllSessions: vi.fn(async () => ({ changed: 0, skipped: 0 })),
}));

vi.mock("./SessionMenu", () => ({
  SessionMenu: () => null,
}));

import { SessionRoster } from "./SessionRoster";

type RosterSession = Record<string, unknown>;

function session(overrides: RosterSession): RosterSession {
  return {
    sessionId: "s1",
    directorId: "dir-A",
    name: "a session",
    machineName: "desk",
    activityState: "Working",
    effectiveColor: "blue",
    effectiveColorHex: "#3B82F6",
    stateLabel: "Working",
    triageBucket: "active",
    ...overrides,
  };
}

function renderRoster(sessions: RosterSession[]) {
  return render(
    <MemoryRouter>
      <SessionRoster
        // The component takes SessionDto[]; the test builds the subset of fields the roster reads.
        sessions={sessions as never}
        directors={[]}
        portByDirector={new Map()}
        selectedId={undefined}
        view="my-order"
        error={null}
        onView={() => {}}
        onNewSession={() => {}}
      />
    </MemoryRouter>,
  );
}

afterEach(() => cleanup());

describe("the roster's uncommitted-changes badge", () => {
  it("shows the count for a session with uncommitted work", () => {
    renderRoster([session({ uncommittedCount: 12 })]);

    const badge = screen.getByText("12 chg");
    expect(badge).toBeTruthy();
    expect(badge.className).toContain("changes");
    expect(badge.getAttribute("title")).toBe("12 uncommitted files in this session's working tree");
  });

  it("shows no badge for a verified-clean tree", () => {
    renderRoster([session({ uncommittedCount: 0 })]);

    expect(screen.queryByText(/chg$/)).toBeNull();
  });

  it("shows no badge when the count is unknown, rather than reporting zero", () => {
    // uncommittedCount null: the git probe has not succeeded. "We could not tell" is not "clean", and it
    // is certainly not "0 chg" - so the row says nothing about git at all.
    renderRoster([session({ uncommittedCount: null })]);

    expect(screen.queryByText(/chg$/)).toBeNull();
    expect(screen.queryByText("0 chg")).toBeNull();
  });

  it("shows no badge for a Director too old to send the field", () => {
    renderRoster([session({})]);

    expect(screen.queryByText(/chg$/)).toBeNull();
  });

  it("badges each session with its own count", () => {
    renderRoster([
      session({ sessionId: "s1", name: "dirty one", uncommittedCount: 3 }),
      session({ sessionId: "s2", name: "clean one", uncommittedCount: 0 }),
      session({ sessionId: "s3", name: "busy one", uncommittedCount: 41 }),
    ]);

    expect(screen.getByText("3 chg")).toBeTruthy();
    expect(screen.getByText("41 chg")).toBeTruthy();
    expect(screen.queryByText("0 chg")).toBeNull();
  });
});
