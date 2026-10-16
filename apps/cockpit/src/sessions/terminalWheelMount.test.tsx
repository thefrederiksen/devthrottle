// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// THE TERMINAL TAB'S WHEEL BUTTON IS MOUNTED, AND FED THE RIGHT ROW (mission #4070, phase 4). The button itself is
// tested in client-core; this pins the Cockpit's half: the session view hands the Terminal pane the selected
// session's own roster row and the roster's re-read, and the pane draws the shared button in its bar - or nothing,
// when the Gateway's verdict has no button.

const captured = vi.hoisted(() => ({ props: [] as unknown[] }));

vi.mock("@devthrottle/client-core/terminal/interactive", () => ({
  InteractiveTerminal: class {
    start(): void {}
    dispose(): void {}
  },
}));
vi.mock("@xterm/xterm/css/xterm.css", () => ({}));
// The session view reads the queue on mount; nothing here is about the queue, and no test reaches a network.
vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@devthrottle/client-core/api/client")>()),
  getQueue: () => Promise.resolve([]),
  gatewayFetch: vi.fn(() => new Promise<Response>(() => {})),
}));
vi.mock("../components/FileViewerModal", () => ({ FileViewerModal: () => null }));
vi.mock("@devthrottle/client-core/sessions/TerminalWheelButton", () => ({
  TerminalWheelButton: (props: { session: SessionDto | null; onChanged?: () => void }) => {
    captured.props.push(props);
    return <span data-testid="wheel-button">{props.session?.sessionId ?? "(none)"}</span>;
  },
}));

import { TerminalPane } from "../panes/TerminalPane";

const SID = "7d2f9c10-0000-4000-8000-000000000002";
const OTHER = "7d2f9c10-0000-4000-8000-000000000003";

function row(sessionId: string): SessionDto {
  return { sessionId, apiMode: { kind: "idle", terminalWheel: { kind: "take", label: "Take the wheel", enabled: true } } } as unknown as SessionDto;
}

afterEach(() => {
  cleanup();
  captured.props = [];
});

function renderPane(session: SessionDto | undefined, onWheelChanged?: () => void) {
  render(
    <MemoryRouter initialEntries={[`/sessions/${SID}`]}>
      <Routes>
        <Route path="/sessions/:sessionId" element={<TerminalPane session={session} onWheelChanged={onWheelChanged} />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("the Cockpit Terminal pane", () => {
  it("TerminalPane_SelectedRow_DrawsTheSharedWheelButtonWithThatRow", () => {
    const refresh = vi.fn();
    renderPane(row(SID), refresh);
    expect(screen.getByTestId("wheel-button").textContent).toBe(SID);
    const last = captured.props.at(-1) as { onChanged?: () => void };
    last.onChanged?.();
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it("TerminalPane_RowOfAnotherSession_IsNotHandedToTheButton", () => {
    renderPane(row(OTHER));
    expect(screen.getByTestId("wheel-button").textContent).toBe("(none)");
  });
});

// The session view's half: it passes the selected row and the roster's re-read down to the pane.
vi.mock("./StopSessionProvider", () => ({ useStopSession: () => ({ openStop: vi.fn() }) }));
vi.mock("./SessionActionBar", () => ({ SessionActionBar: () => <div /> }));
vi.mock("./SessionComposer", () => ({ SessionComposer: () => <div /> }));
vi.mock("./SessionMenu", () => ({ SessionMenu: () => null }));
vi.mock("./ChatTab", () => ({ ChatTab: () => <div /> }));
vi.mock("./VoiceTab", () => ({ VoiceTab: () => <div /> }));
vi.mock("./SourceControlTab", () => ({ SourceControlTab: () => <div /> }));
vi.mock("./QueuePanel", () => ({ QueuePanel: () => <div /> }));
vi.mock("./ScreenshotsPanel", () => ({ ScreenshotsPanel: () => <div /> }));
vi.mock("@devthrottle/client-core/sessions/WingmanTab", () => ({ WingmanTab: () => <div /> }));
vi.mock("./wingmanNowActions", () => ({ wingmanNowActions: () => ({}) }));

import { SessionDetail } from "./SessionDetail";

describe("the Cockpit session view", () => {
  it("SessionDetail_TerminalPane_GetsTheSelectedRowAndTheRostersReRead", () => {
    const refreshNow = vi.fn();
    function Shell() {
      return <Outlet context={{ sessions: [row(OTHER), row(SID)], directors: [], refreshNow }} />;
    }
    render(
      <MemoryRouter initialEntries={[`/sessions/${SID}?tab=terminal`]}>
        <Routes>
          <Route element={<Shell />}>
            <Route path="/sessions/:sessionId" element={<SessionDetail />} />
          </Route>
        </Routes>
      </MemoryRouter>,
    );
    expect(screen.getByTestId("wheel-button").textContent).toBe(SID);
    (captured.props.at(-1) as { onChanged?: () => void }).onChanged?.();
    expect(refreshNow).toHaveBeenCalledTimes(1);
  });
});
