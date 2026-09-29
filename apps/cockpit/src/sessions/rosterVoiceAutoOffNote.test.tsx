// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { __resetVoiceModeAllForTests } from "@devthrottle/client-core/voice/useVoiceModeAll";

// Voice mode auto-off, step 5: the Cockpit shows the Gateway's quiet line under the roster's voice button. The line
// is proved in client-core; this proves the MOUNT - that the roster's button actually carries it, word for word, and
// nothing when the Gateway sends none.

const LINE = "Voice mode switched off at 14:32 - you answered five sessions without listening";
const gateway = vi.hoisted(() => ({ note: null as string | null }));

vi.mock("@devthrottle/client-core/api/client", () => ({
  gatewayErrorMessage: (err: unknown) => String(err),
  setVoiceModeAllSessions: vi.fn(async () => ({ changed: 0, skipped: 0 })),
  getVoiceModeAllState: vi.fn(async () => ({ enabled: false, note: gateway.note })),
}));

vi.mock("./SessionMenu", () => ({
  SessionMenu: () => null,
}));

import { SessionRoster } from "./SessionRoster";

afterEach(() => {
  cleanup();
  __resetVoiceModeAllForTests();
});

async function renderRoster() {
  const view = render(
    <MemoryRouter>
      <SessionRoster
        sessions={[{
          sessionId: "s1", directorId: "dir-A", name: "a session", machineName: "desk", activityState: "WaitingForInput",
          effectiveColor: "red", effectiveColorHex: "#ef4444", stateLabel: "Needs you", triageBucket: "needsYou",
        }] as never}
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
  await act(async () => {});
  return view;
}

describe("the roster's voice button", () => {
  it("shows the Gateway's line word for word beside the button", async () => {
    gateway.note = LINE;
    const { container } = await renderRoster();

    const notes = [...container.querySelectorAll(".roster-voice-all .roster-voice-all-note")].map((n) => n.textContent);
    expect(notes).toContain(LINE);
  });

  it("shows no line when the Gateway sends none", async () => {
    gateway.note = null;
    const { container } = await renderRoster();

    expect(container.querySelector(".roster-voice-all .roster-voice-all-note")).toBeNull();
  });
});
