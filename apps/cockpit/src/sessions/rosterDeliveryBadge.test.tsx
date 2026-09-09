// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The roster's "not delivered" badge (issue internal#811): a prompt to this session never reached the
// agent, so the user's words are gone.
//
// The case worth a test is the one that was silent when it was wrong. On 2026-07-15 two spoken prompts
// failed to submit and every screen in the product carried on as if nothing had happened - the only
// witness was a line in a Director log file, and it went unread for two days. A row that says nothing
// about a lost prompt is the defect, so these tests assert the badge APPEARS, and that it stays quiet in
// the two cases where being loud would be noise: a failure that already recovered, and composer retries
// that cost the user nothing.

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

const NOTICE = "Your last prompt was not delivered - the agent never received it. The composer never echoed.";

afterEach(() => cleanup());

describe("the roster's not-delivered badge", () => {
  it("shows the badge when the Gateway says a prompt was lost", () => {
    renderRoster([session({ promptDeliveryNotice: NOTICE })]);

    const badge = screen.getByText("not delivered");
    expect(badge).toBeTruthy();
    expect(badge.className).toContain("not-delivered");
  });

  it("puts the Gateway's sentence in the tooltip, with the history when there is one", () => {
    renderRoster([
      session({ promptDeliveryNotice: NOTICE, failedPromptDeliveries: 2, composerEchoMisses: 6 }),
    ]);

    expect(screen.getByText("not delivered").getAttribute("title")).toBe(
      `${NOTICE} (2 failed deliveries on this session, 6 composer retries)`,
    );
  });

  it("shows nothing for a session that has never lost a prompt", () => {
    renderRoster([session({})]);

    expect(screen.queryByText("not delivered")).toBeNull();
  });

  it("shows nothing once a later prompt got through, even though the counts remain", () => {
    // The alarm answers "are my words gone right now". A retry that landed answers it: no.
    renderRoster([session({ failedPromptDeliveries: 4, composerEchoMisses: 9 })]);

    expect(screen.queryByText("not delivered")).toBeNull();
  });

  it("does not repaint the dot - the agent is doing exactly what it was doing", () => {
    // The colour describes the AGENT. It never heard the prompt, so nothing about it changed; recolouring
    // it would tell a lie about the agent in order to tell the truth about the delivery.
    const { container } = renderRoster([session({ promptDeliveryNotice: NOTICE })]);

    const dot = container.querySelector(".roster-dot") as HTMLElement;
    expect(dot.style.backgroundColor).toBe("rgb(59, 130, 246)");
  });
});
