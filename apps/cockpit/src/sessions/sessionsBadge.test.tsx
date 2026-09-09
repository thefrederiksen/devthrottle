// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, cleanup, waitFor } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { SharedRoster } from "@devthrottle/client-core/fleet/rosterStore";

// Rendered proof of the WIRING behind the desktop "needs you" badge (Epic #1159 step A). The rule itself
// lives once in client-core (needsYouBadgeCount) and is unit tested there; what this pins is that the
// Cockpit actually calls it with the roster it just read, so the desktop and the phone cannot come to
// two different numbers for the same fleet - which is exactly how they drifted before.
//
// The ruling being held down: a session on a machine nobody can reach is SHOWN (the roster still lists
// it, dimmed and dated) and never NAGGED about. A laptop asleep overnight with red sessions must not
// leave the badge lit until morning.

const rosterValue: { current: SharedRoster } = {
  current: { sessions: null, machineErrors: [], directors: [], unreachableBanner: null, error: null, refreshNow: () => {} },
};
vi.mock("@devthrottle/client-core/fleet/rosterStore", () => ({
  useSharedRoster: () => rosterValue.current,
}));

const reconcileBadgeMock = vi.fn();
vi.mock("@devthrottle/client-core/push/register", () => ({
  reconcileBadge: (...args: unknown[]) => reconcileBadgeMock(...args),
}));

// The roster reads GET /directors for its "computer:port" headers; the rest of the module is mocked out
// because this test is about one number, not about the session rail's own behaviour.
vi.mock("@devthrottle/client-core/api/client", () => ({
  getDirectors: () => Promise.resolve([]),
  getRepos: () => Promise.resolve([]),
  getAgents: () => Promise.resolve([]),
  createSession: () => Promise.resolve({ sessionId: "new" }),
  setVoiceModeAllSessions: () => Promise.resolve({ changed: 0, skipped: 0 }),
  holdSession: () => Promise.resolve(),
  stopSession: () => Promise.resolve({ verdict: "stopped", headline: "stopped", details: [] }),
  getHandover: () => Promise.resolve(null),
  gatewayErrorMessage: (e: unknown) => String(e),
}));

vi.mock("react-router-dom", () => ({
  Link: ({ children }: { children?: unknown }) => <span>{children as never}</span>,
  Outlet: () => null,
  useMatch: () => null,
  useNavigate: () => vi.fn(),
}));

import { SessionsView } from "./SessionsView";

function needsYou(sessionId: string, fields: Partial<SessionDto> = {}): SessionDto {
  return {
    sessionId,
    directorId: "d1",
    machineName: "SOREN",
    repoPath: "D:/ReposFred/devthrottle",
    agent: "ClaudeCode",
    activityState: "Waiting",
    createdAt: "2026-07-30T10:00:00Z",
    sortOrder: 0,
    name: sessionId,
    effectiveColor: "red",
    effectiveColorHex: "#ef4444",
    stateLabel: "Needs you",
    triageBucket: "needsYou",
    ...fields,
  } as SessionDto;
}

beforeEach(() => {
  reconcileBadgeMock.mockClear();
  window.localStorage.clear();
});

afterEach(() => cleanup());

describe("Cockpit needs-you badge", () => {
  it("counts only the waiting sessions on machines the owner can act on", async () => {
    rosterValue.current = {
      sessions: [
        needsYou("awake", { machineReachable: true }),
        needsYou("asleep-1", { machineReachable: false }),
        needsYou("asleep-2", { machineReachable: false }),
      ],
      machineErrors: [],
      directors: [],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };

    render(<SessionsView />);

    await waitFor(() => expect(reconcileBadgeMock).toHaveBeenCalled());
    expect(reconcileBadgeMock).toHaveBeenLastCalledWith(1);
  });

  it("still counts a session the Gateway did not stamp at all (older Gateway)", async () => {
    rosterValue.current = {
      sessions: [needsYou("unstamped")],
      machineErrors: [],
      directors: [],
      unreachableBanner: null,
      error: null,
      refreshNow: () => {},
    };

    render(<SessionsView />);

    await waitFor(() => expect(reconcileBadgeMock).toHaveBeenCalled());
    expect(reconcileBadgeMock).toHaveBeenLastCalledWith(1);
  });
});
