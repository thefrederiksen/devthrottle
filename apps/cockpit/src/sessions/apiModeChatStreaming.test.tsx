// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { act, render, screen, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  fakeLiveNetwork, receiptItem, replyDelta, settle, workingDisplay, type FakeLiveNetwork,
} from "@devthrottle/client-core/apimode/liveStream.testkit";

// THE COCKPIT'S CHAT STREAMS THE REPLY MID-TURN (mission #4070, phase 6 QA finding 2). The REAL SessionDetail and the
// REAL shared live Chat, with only the network faked: the live stream is the fake Gateway's, and the roster is served
// again every poll, as the Cockpit's roster store does every two seconds while a turn runs.
//
// These are guards, not the proof of a fix: they passed on origin/main as first written, so the finding did not
// reproduce in this code. They go red if the Cockpit's Chat ever stops streaming for either reason they drive.

const net: { current: FakeLiveNetwork | null } = { current: null };

vi.mock("@devthrottle/client-core/api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@devthrottle/client-core/api/client")>()),
  getQueue: () => Promise.resolve([]),
  gatewayFetch: (url: string, init: RequestInit) => net.current!.deps.fetch!(url, init),
  authHeaders: () => ({}),
}));

vi.mock("@devthrottle/client-core/sessions/WingmanTab", () => ({ WingmanTab: () => <div /> }));
vi.mock("./wingmanNowActions", () => ({ wingmanNowActions: () => ({}) }));
vi.mock("./StopSessionProvider", () => ({ useStopSession: () => ({ openStop: vi.fn() }) }));
vi.mock("../panes/TerminalPane", () => ({ TerminalPane: () => <div /> }));
vi.mock("./SessionActionBar", () => ({ SessionActionBar: () => <div /> }));
vi.mock("./SessionComposer", () => ({ SessionComposer: () => <div /> }));
vi.mock("./SessionMenu", () => ({ SessionMenu: () => null }));
vi.mock("./ChatTab", () => ({ ChatTab: () => <div data-testid="history-chat" /> }));
vi.mock("./VoiceTab", () => ({ VoiceTab: () => <div /> }));
vi.mock("./SourceControlTab", () => ({ SourceControlTab: () => <div /> }));
vi.mock("./QueuePanel", () => ({ QueuePanel: () => <div /> }));
vi.mock("./ScreenshotsPanel", () => ({ ScreenshotsPanel: () => <div /> }));
vi.mock("@devthrottle/client-core/sessions/NotDeliveredIndicator", () => ({ NotDeliveredIndicator: () => null }));

import { SessionDetail } from "./SessionDetail";

const SID = "7d2f9c10-0000-4000-8000-000000000042";

function row(turn: number): SessionDto {
  return { sessionId: SID, connectionMode: "api", apiMode: workingDisplay(turn) } as unknown as SessionDto;
}

function Shell({ sessions }: { sessions: SessionDto[] | null }) {
  return <Outlet context={{ sessions, refreshNow: () => {} }} />;
}

function page(sessions: SessionDto[] | null) {
  return (
    <MemoryRouter initialEntries={[`/sessions/${SID}`]}>
      <Routes>
        <Route element={<Shell sessions={sessions} />}>
          <Route path="/sessions/:sessionId" element={<SessionDetail />} />
        </Route>
      </Routes>
    </MemoryRouter>
  );
}

afterEach(() => {
  cleanup();
  net.current = null;
});

describe("the Cockpit's live Chat mid-turn", () => {
  it("SessionDetail_RosterServedAgainEveryPollMidTurn_TheReplyStreamsAsItArrives", async () => {
    // Arrange
    net.current = fakeLiveNetwork();
    const view = render(page([row(1)]));
    await act(settle);
    const conn = () => net.current!.connections[net.current!.connections.length - 1];

    // Act: the reply arrives in pieces, and between them the roster is served again with new objects.
    await act(async () => {
      conn().items("g1:1", [receiptItem("p1", "Delivered", "green", 1, "t1", "Count to three")]);
      await settle();
    });
    for (const [i, piece] of ["One, ", "two, ", "three"].entries()) {
      view.rerender(page([row(1)]));
      await act(async () => {
        conn().items(`g1:${i + 2}`, [replyDelta("m1", "t1", piece, i + 2)]);
        await settle();
      });
    }

    // Assert: the streamed words are on screen mid-turn, on the one stream the Chat opened.
    const log = screen.getByRole("log", { name: "Live conversation" });
    expect(within(log).getByText("One, two, three")).toBeTruthy();
    expect(net.current.connections).toHaveLength(1);
  });

  it("SessionDetail_ColdDeepLinkBeforeTheRosterArrives_StreamsTheReplyOnceTheRowSaysApiMode", async () => {
    // Arrange: the page opens on a deep link before the roster's first answer, as the proof's browser did.
    net.current = fakeLiveNetwork();
    const view = render(page(null));
    await act(settle);
    view.rerender(page([row(1)]));
    await act(settle);
    const conn = () => net.current!.connections[net.current!.connections.length - 1];

    // Act
    await act(async () => {
      conn().items("g1:1", [receiptItem("p1", "Delivered", "green", 1, "t1", "Count to three")]);
      conn().items("g1:2", [replyDelta("m1", "t1", "One, two", 2)]);
      await settle();
    });

    // Assert
    const log = screen.getByRole("log", { name: "Live conversation" });
    expect(within(log).getByText("One, two")).toBeTruthy();
  });
});
