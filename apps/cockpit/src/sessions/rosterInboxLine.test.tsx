// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The roster's row line (Message Load mission, slice 4): what waits in a session's fleet inbox. The Gateway
// folds the words; the card renders them verbatim and decides nothing. These fail if the line is dropped,
// reworded, or shown for a session the Gateway said nothing about.

vi.mock("@devthrottle/client-core/api/client", () => ({
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

describe("the roster's row line", () => {
  it("renders the Gateway's words verbatim", () => {
    const line = "1 message stuck, unread for 20 minutes; 2 messages waiting";
    const { container } = renderRoster([session({ inboxLine: line })]);

    const el = container.querySelector(".roster-inbox");
    expect(el?.textContent).toBe(line);
    expect(screen.getByText(line)).toBeTruthy();
  });

  it("renders a reply line exactly as sent", () => {
    renderRoster([session({ inboxLine: "1 reply waiting" })]);

    expect(screen.getByText("1 reply waiting").className).toBe("roster-inbox");
  });

  it("renders nothing when the Gateway sent no line", () => {
    const { container } = renderRoster([session({}), session({ sessionId: "s2", inboxLine: null })]);

    expect(container.querySelector(".roster-inbox")).toBeNull();
  });

  it("leaves the colour and the state label alone", () => {
    const { container } = renderRoster([session({ inboxLine: "2 messages waiting" })]);

    const dot = container.querySelector(".roster-dot") as HTMLElement;
    expect(dot.style.backgroundColor).toBe("rgb(59, 130, 246)");
    expect(container.querySelector(".roster-state")?.textContent).toBe("Working");
  });
});
