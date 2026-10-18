// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// THE TERMINAL TAB'S WHEEL BUTTON IS MOUNTED ON THE PHONE TOO, AND FED THE RIGHT ROW (mission #4070, phase 4). The
// button is tested in client-core; this pins the phone's half: the Terminal page hands the shared button the row its
// session hook polled and that hook's re-read, so the phone and the Cockpit draw the same control from one copy.

const captured = vi.hoisted(() => ({ props: [] as unknown[], refreshNow: vi.fn(), session: null as unknown }));

vi.mock("@xterm/xterm/css/xterm.css", () => ({}));
vi.mock("@devthrottle/client-core/terminal/stream", () => ({
  TerminalMirror: class {
    start(): void {}
    dispose(): void {}
  },
}));
vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@devthrottle/client-core/api/client")>()),
  listSessions: () => new Promise<SessionDto[]>(() => {}),
}));
vi.mock("@devthrottle/client-core/dictation/DictationStatusStrip", () => ({ DictationStatusStrip: () => null }));
vi.mock("../components/SessionControls", () => ({ SessionControls: () => null }));
vi.mock("../components/SessionAppBar", () => ({ SessionAppBar: () => null }));
vi.mock("../components/ViewTabs", () => ({ ViewTabs: () => null }));
vi.mock("../components/useSessionManage", () => ({
  useSessionManage: () => ({ session: captured.session, refreshNow: captured.refreshNow }),
}));
vi.mock("@devthrottle/client-core/sessions/TerminalWheelButton", () => ({
  TerminalWheelButton: (props: { session: SessionDto | null; onChanged?: () => void }) => {
    captured.props.push(props);
    return <span data-testid="wheel-button">{props.session?.sessionId ?? "(none)"}</span>;
  },
}));

import { Terminal } from "./Terminal";

const SID = "7d2f9c10-0000-4000-8000-000000000002";

afterEach(() => {
  cleanup();
  captured.props = [];
  captured.refreshNow.mockReset();
  captured.session = null;
});

function renderPage() {
  render(
    <MemoryRouter initialEntries={[`/session/${SID}/terminal`]}>
      <Routes>
        <Route path="/session/:sessionId/terminal" element={<Terminal />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("the phone Terminal page", () => {
  it("Terminal_PolledRow_DrawsTheSharedWheelButtonWithThatRowAndTheReRead", () => {
    captured.session = { sessionId: SID } as SessionDto;
    renderPage();
    expect(screen.getByTestId("wheel-button").textContent).toBe(SID);
    (captured.props.at(-1) as { onChanged?: () => void }).onChanged?.();
    expect(captured.refreshNow).toHaveBeenCalledTimes(1);
  });

  it("Terminal_NoRowYet_HandsTheButtonNothing", () => {
    renderPage();
    expect(screen.getByTestId("wheel-button").textContent).toBe("(none)");
  });
});
