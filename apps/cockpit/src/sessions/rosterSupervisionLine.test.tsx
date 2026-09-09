// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The roster card's supervision line (internal#625): started / open / idle / turns from the shared
// client-core formatter. As with the changes badge, the cases worth pinning are the silent-when-wrong
// ones: an older Director that reports no turn count and no idle clock must produce NO stat - never
// "turns 0" or "idle 0m" - and the impossible 0001-01-01 CreatedAt must not render a decades-long
// runtime.

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

function isoAgo(ms: number): string {
  return new Date(Date.now() - ms).toISOString();
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

describe("the roster's supervision line", () => {
  it("shows started, open, idle and turns for a fully-reporting session", () => {
    renderRoster([
      session({
        createdAt: isoAgo(3 * 3600 * 1000),
        turnCount: 14,
        cumulativeIdleSeconds: 42 * 60,
      }),
    ]);

    expect(screen.getByText("started")).toBeTruthy();
    expect(screen.getByText("open")).toBeTruthy();
    expect(screen.getByText("3h 0m")).toBeTruthy();
    expect(screen.getByText("idle")).toBeTruthy();
    expect(screen.getByText("42m")).toBeTruthy();
    expect(screen.getByText("turns")).toBeTruthy();
    expect(screen.getByText("14")).toBeTruthy();
  });

  it("omits idle and turns for an older Director, rather than reporting zeros", () => {
    renderRoster([session({ createdAt: isoAgo(3600 * 1000) })]);

    expect(screen.getByText("started")).toBeTruthy();
    expect(screen.getByText("open")).toBeTruthy();
    expect(screen.queryByText("idle")).toBeNull();
    expect(screen.queryByText("turns")).toBeNull();
  });

  it("renders a measured zero turn count - zero from a live Director is an answer", () => {
    renderRoster([
      session({ createdAt: isoAgo(60 * 1000), turnCount: 0, cumulativeIdleSeconds: 0 }),
    ]);

    expect(screen.getByText("turns")).toBeTruthy();
    expect(screen.getByText("0")).toBeTruthy();
  });

  it("shows no started/open at all for the impossible 0001-01-01 CreatedAt", () => {
    renderRoster([session({ createdAt: "0001-01-01T00:00:00Z" })]);

    expect(screen.queryByText("started")).toBeNull();
    expect(screen.queryByText("open")).toBeNull();
  });

  it("colors a long-neglected session's idle value as an alarm", () => {
    renderRoster([
      session({ createdAt: isoAgo(55 * 3600 * 1000), cumulativeIdleSeconds: 46 * 3600, turnCount: 31 }),
    ]);

    const idleValue = screen.getByText("1d 22h");
    expect(idleValue.className).toContain("hot");
  });
});
