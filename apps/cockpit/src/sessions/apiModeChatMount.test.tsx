// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// THE COCKPIT MOUNTS THE SHARED LIVE CHAT FOR AN API-MODE SESSION (mission #4070, phase 3). The live Chat lives once
// in client-core and this shell only mounts it: on the Chat tab, for a row that carries the Gateway's API-mode verdict,
// handed this route's session id and that verdict. A terminal-mode row (no verdict) keeps the history Chat, unchanged.
// This drives the REAL SessionDetail, so mounting the wrong one, or passing another id or verdict, goes red.

vi.mock("@devthrottle/client-core/api/client", () => ({
  gatewayErrorMessage: (err: unknown) => String(err),
  getQueue: () => Promise.resolve([]),
  gatewayFetch: vi.fn(),
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {},
}));

vi.mock("@devthrottle/client-core/sessions/WingmanTab", () => ({ WingmanTab: () => <div /> }));

vi.mock("@devthrottle/client-core/apimode/ApiModeLiveChat", () => ({
  ApiModeLiveChat: ({ sessionId, display }: { sessionId: string; display: { activityLabel: string } }) => (
    <div data-testid="live-chat">{`${sessionId} ${display.activityLabel}`}</div>
  ),
}));

// What Now can do is proven in wingmanNowActions.test.tsx, against a real Gateway answer; this test is only about
// the tab being mounted, so the actions are a stand-in.
vi.mock("./wingmanNowActions", () => ({ wingmanNowActions: () => ({}) }));

// The page's other regions cannot run in jsdom (a terminal engine, a live socket, a microphone) and are not the
// subject.
// The stop lives in one owner above the page (StopSessionProvider). The session view asks it to open the stop
// question for a finished session; nothing here is about stopping, so it is a stand-in.
vi.mock("./StopSessionProvider", () => ({ useStopSession: () => ({ openStop: vi.fn() }) }));

vi.mock("../panes/TerminalPane", () => ({ TerminalPane: () => <div /> }));
vi.mock("./SessionActionBar", () => ({ SessionActionBar: () => <div /> }));
vi.mock("./SessionComposer", () => ({ SessionComposer: () => <div /> }));
vi.mock("./SessionMenu", () => ({ SessionMenu: () => null }));
vi.mock("./ChatTab", () => ({ ChatTab: ({ sessionId }: { sessionId: string }) => <div data-testid="history-chat">{sessionId}</div> }));
vi.mock("./VoiceTab", () => ({ VoiceTab: () => <div /> }));
vi.mock("./SourceControlTab", () => ({ SourceControlTab: () => <div /> }));
vi.mock("./QueuePanel", () => ({ QueuePanel: () => <div /> }));
vi.mock("./ScreenshotsPanel", () => ({ ScreenshotsPanel: () => <div /> }));

import { SessionDetail } from "./SessionDetail";

const SID = "7d2f9c10-0000-4000-8000-000000000041";

function Shell({ sessions }: { sessions: SessionDto[] }) {
  return <Outlet context={{ sessions }} />;
}

function renderWith(row: Record<string, unknown>) {
  render(
    <MemoryRouter initialEntries={[`/sessions/${SID}`]}>
      <Routes>
        <Route element={<Shell sessions={[{ sessionId: SID, ...row } as unknown as SessionDto]} />}>
          <Route path="/sessions/:sessionId" element={<SessionDetail />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
  fireEvent.click(screen.getByRole("tab", { name: "Chat" }));
}

afterEach(() => cleanup());

describe("the Cockpit Chat tab", () => {
  it("ChatTab_ApiModeRow_MountsTheSharedLiveChatWithTheGatewayVerdict", () => {
    renderWith({ connectionMode: "api", apiMode: { kind: "working", activityLabel: "Working - turn 2" } });

    expect(screen.getByTestId("live-chat").textContent).toBe(`${SID} Working - turn 2`);
    expect(screen.queryByTestId("history-chat")).toBeNull();
  });

  it("ChatTab_TerminalModeRow_KeepsTheHistoryChat", () => {
    renderWith({ connectionMode: "terminal" });

    expect(screen.getByTestId("history-chat").textContent).toBe(SID);
    expect(screen.queryByTestId("live-chat")).toBeNull();
  });
});
