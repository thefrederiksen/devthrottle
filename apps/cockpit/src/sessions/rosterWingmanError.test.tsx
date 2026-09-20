// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The Cockpit roster card shows the Gateway's "Wingman error" (mission "Wingman error and retry", 2026-09-19).
//
// The component itself is proved in client-core. What is proved HERE is the mount: the Cockpit card really shows
// it, for a session that is NOT in voice mode, outside the card's link so a press asks again instead of opening
// the session - and a card the Gateway stamped no error on shows none.

const askWingmanAgain = vi.fn(async () => ({ failed: true, message: "The Wingman could not read this stop just now." }));

vi.mock("@devthrottle/client-core/api/client", () => ({
  gatewayErrorMessage: (err: unknown) => String(err),
  setVoiceModeAllSessions: vi.fn(async () => ({ changed: 0, skipped: 0 })),
  askWingmanAgain: (...args: unknown[]) => askWingmanAgain(...(args as [])),
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
    activityState: "WaitingForInput",
    effectiveColor: "red",
    effectiveColorHex: "#EF4444",
    stateLabel: "Needs you",
    triageBucket: "needsYou",
    voiceMode: false,
    ...overrides,
  };
}

function renderRoster(sessions: RosterSession[]) {
  return render(
    <MemoryRouter>
      <SessionRoster
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

const USED_UP = {
  tag: "Wingman error",
  reason: "The model did not answer in time.",
  nextRetryNumber: 0,
  retriesTotal: 8,
  retriesRemaining: 0,
  nextRetryAtUtc: null,
  exhausted: true,
  retryLabel: "",
  exhaustedText: "The Wingman could not read this stop. Nothing more is scheduled.",
  askAgainLabel: "Ask again",
};

afterEach(() => {
  cleanup();
  askWingmanAgain.mockClear();
});

describe("the Cockpit roster's Wingman error", () => {
  it("shows the tag, the Gateway's sentence and the button on a session that is not in voice mode", () => {
    renderRoster([session({ wingmanError: USED_UP })]);

    expect(screen.getByText("Wingman error")).toBeTruthy();
    expect(screen.getByText("The Wingman could not read this stop. Nothing more is scheduled.")).toBeTruthy();
    const button = screen.getByRole("button", { name: "Ask again" });
    // Outside the card's link: pressing it must not open the session.
    expect(button.closest("a")).toBeNull();
  });

  it("asks again for that session when the button is pressed", async () => {
    renderRoster([session({ wingmanError: USED_UP })]);
    fireEvent.click(screen.getByRole("button", { name: "Ask again" }));
    await waitFor(() => expect(screen.getByText("The Wingman could not read this stop just now.")).toBeTruthy());
    expect(askWingmanAgain).toHaveBeenCalledTimes(1);
    expect(askWingmanAgain.mock.calls[0]).toEqual(["s1"]);
  });

  it("shows nothing on a card the Gateway stamped no error on", () => {
    renderRoster([session({})]);
    expect(screen.queryByText("Wingman error")).toBeNull();
    expect(screen.queryByRole("button", { name: "Ask again" })).toBeNull();
  });
});
