// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The roster's connection tag (API mode mission #4070, phase 5): "API mode" on a session driven in API mode, in the
// Gateway's words. The Gateway stamps `connectionTag`; the card renders it verbatim and never reads the mode word,
// so a row that says connectionMode "api" but carries no stamp (an older Gateway) draws no tag.

vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  // The error reporter needs the real error helpers; the rest is faked.
  ...(({ GatewayError, gatewayErrorMessage, authHeaders }) => ({ GatewayError, gatewayErrorMessage, authHeaders }))(
    await importOriginal<typeof import("@devthrottle/client-core/api/client")>(),
  ),
  // The restart-requests panel polls inside this roster and reaches for gatewayErrorMessage when a
  // read fails. Without it here, every poll threw an unhandled rejection - the tests still passed, but
  // the run reported errors and exited non-zero, which is how a real failure would come to be ignored.
  gatewayErrorMessage: (err: unknown) => String(err),
  setVoiceModeAllSessions: vi.fn(async () => ({ changed: 0, skipped: 0 })),
}));

vi.mock("./SessionMenu", () => ({
  SessionMenu: () => null,
}));


// THE CARD'S DETAIL IS BEHIND THE ONE DENSITY SWITCH (owner ruling, 2026-09-20): a card is three lines
// at "clean", and these facts are drawn at the positions below. Setting the switch here is not a
// workaround - it names the position each fact belongs to, and it is the only thing that proves the
// switch reaches the rail at all.
import { resetDensityForTests, setDensity } from "@devthrottle/client-core/sessions/density";
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

beforeEach(() => {
  resetDensityForTests();
  setDensity("everything");
});

describe("the roster's connection tag", () => {
  it("shows the Gateway's words on an API-mode session", () => {
    renderRoster([session({ connectionMode: "api", connectionTag: "API mode" })]);

    const tag = screen.getByText("API mode");
    expect(tag.className).toContain("roster-tag");
    expect(tag.className).toContain("connection");
    expect(tag.getAttribute("title")).toBe("Connection");
  });

  it("shows no tag on a terminal session", () => {
    renderRoster([session({ connectionMode: "terminal", connectionTag: null })]);

    expect(screen.queryByTitle("Connection")).toBeNull();
  });

  it("decides nothing from the mode word: no stamp, no tag", () => {
    renderRoster([session({ connectionMode: "api" })]);

    expect(screen.queryByTitle("Connection")).toBeNull();
  });

  it("is detail, drawn with the model and the voice tags, so a card at clean stays three lines", () => {
    setDensity("clean");

    renderRoster([session({ connectionMode: "api", connectionTag: "API mode" })]);

    expect(screen.queryByText("API mode")).toBeNull();
  });
});
