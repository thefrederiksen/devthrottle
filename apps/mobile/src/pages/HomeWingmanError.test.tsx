// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { SessionRow } from "./Home";

// The phone's roster card shows the Gateway's "Wingman error" (mission "Wingman error and retry", 2026-09-19).
// The component is proved in client-core; this proves the MOUNT - the same block the Cockpit shows, on a session
// that is not in voice mode, outside the card's link - and that an unstamped card shows none.

vi.mock("@devthrottle/client-core/restart/RestartRequestsPanel", () => ({
  RestartRequestsPanel: () => null,
}));

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function session(overrides: Record<string, unknown> = {}): SessionDto {
  return {
    sessionId: "session-one",
    directorId: "director-one",
    machineName: "SOREN_NORTH",
    name: "the retention sweep",
    agentToolDisplay: "Pi",
    activityState: "WaitingForInput",
    status: "Running",
    effectiveColor: "red",
    effectiveColorHex: "#ef4444",
    triageBucket: "needsYou",
    stateLabel: "Needs you",
    voiceMode: false,
    ...overrides,
  } as unknown as SessionDto;
}

function renderRow(value: SessionDto) {
  return render(
    <MemoryRouter>
      <ul>
        <SessionRow session={value} />
      </ul>
    </MemoryRouter>,
  );
}

describe("the phone roster's Wingman error", () => {
  it("shows the tag, a countdown to the Gateway's booked time, and the button", () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(Date.parse("2026-09-19T09:00:00Z"));
    renderRow(session({
      wingmanError: {
        tag: "Wingman error",
        reason: "The model did not answer in time.",
        nextRetryNumber: 2,
        retriesTotal: 8,
        retriesRemaining: 7,
        nextRetryAtUtc: "2026-09-19T09:00:40Z",
        exhausted: false,
        retryLabel: "retry 2 of 8",
        exhaustedText: "",
        askAgainLabel: "Ask again",
      },
    }));

    expect(screen.getByText("Wingman error")).toBeTruthy();
    expect(screen.getByText("retry 2 of 8 in 40 seconds")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Ask again" }).closest("a")).toBeNull();
  });

  it("shows nothing on a card the Gateway stamped no error on", () => {
    renderRow(session());
    expect(screen.queryByText("Wingman error")).toBeNull();
    expect(screen.queryByRole("button", { name: "Ask again" })).toBeNull();
  });
});
