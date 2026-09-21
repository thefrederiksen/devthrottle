// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// THE COCKPIT MOUNTS THE WINGMAN TAB (the Wingman inspector, phase 3). The tab lives once in client-core and this
// shell only mounts it: a fifth tab after Source Control, rendered only while selected, handed this route's session
// id. This drives the REAL SessionDetail, so dropping the tab, mounting it eagerly, or passing another id goes red.

vi.mock("@devthrottle/client-core/api/client", () => ({
  gatewayErrorMessage: (err: unknown) => String(err),
  getQueue: () => Promise.resolve([]),
  gatewayFetch: vi.fn(),
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {},
}));

vi.mock("@devthrottle/client-core/sessions/WingmanTab", () => ({
  WingmanTab: ({ sessionId }: { sessionId: string }) => <div data-testid="wingman-tab">{sessionId}</div>,
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
vi.mock("./ChatTab", () => ({ ChatTab: () => <div /> }));
vi.mock("./VoiceTab", () => ({ VoiceTab: () => <div /> }));
vi.mock("./SourceControlTab", () => ({ SourceControlTab: () => <div /> }));
vi.mock("./QueuePanel", () => ({ QueuePanel: () => <div /> }));
vi.mock("./ScreenshotsPanel", () => ({ ScreenshotsPanel: () => <div /> }));

import { SessionDetail } from "./SessionDetail";

const SID = "7d2f9c10-0000-4000-8000-000000000031";

function Shell({ sessions }: { sessions: SessionDto[] }) {
  return <Outlet context={{ sessions }} />;
}

afterEach(() => cleanup());

describe("the Cockpit session view", () => {
  it("offers Wingman as the fifth tab and mounts the shared tab only while it is selected", () => {
    render(
      <MemoryRouter initialEntries={[`/sessions/${SID}`]}>
        <Routes>
          <Route element={<Shell sessions={[{ sessionId: SID } as unknown as SessionDto]} />}>
            <Route path="/sessions/:sessionId" element={<SessionDetail />} />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    const tabs = screen.getAllByRole("tab").map((t) => t.textContent);
    expect(tabs).toEqual(["Chat", "Terminal", "Voice", "Source Control", "Wingman", "Reports"]);
    expect(screen.queryByTestId("wingman-tab")).toBeNull();

    fireEvent.click(screen.getByRole("tab", { name: "Wingman" }));
    expect(screen.getByTestId("wingman-tab").textContent).toBe(SID);

    fireEvent.click(screen.getByRole("tab", { name: "Terminal" }));
    expect(screen.queryByTestId("wingman-tab")).toBeNull();
  });
});
