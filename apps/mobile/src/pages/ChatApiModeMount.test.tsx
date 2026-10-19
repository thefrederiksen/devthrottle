// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// THE PHONE MOUNTS THE SHARED LIVE CHAT FOR AN API-MODE SESSION (mission #4070, phase 3). The live Chat lives once in
// client-core; this screen only mounts it in place of the history conversation, for a row that carries the Gateway's
// API-mode verdict, and the history read stands down for it. A terminal-mode row keeps the history Chat, unchanged.

const SID = "3a8e5d21-0000-4000-8000-000000000041";

let roster: unknown[] = [];
// How the roster read answers; by default with `roster`. A test may hold it pending or make it fail.
let rosterRead: () => Promise<unknown[]> = () => Promise.resolve(roster);

// The fields the roster read requires of every row; the subject here is only the connection mode and the verdict.
const row = { activityState: "Idle", effectiveColor: "grey", stateLabel: "Idle", triageBucket: "idle", agent: "Pi", agentToolDisplay: "Pi" };

vi.mock("@devthrottle/client-core/api/client", () => ({
  listSessions: () => rosterRead(),
  holdSession: vi.fn(),
  stopSession: vi.fn(),
  gatewayErrorMessage: (err: unknown) => String(err),
  gatewayFetch: vi.fn(),
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {},
}));

const historyReadFor = vi.fn();
vi.mock("@devthrottle/client-core/history/useSessionChat", () => ({
  useSessionChat: (sessionId: string | undefined) => {
    historyReadFor(sessionId);
    return {
      bubbles: [],
      emptyText: "No conversation yet.",
      staleNotice: null,
      loadFailed: false,
      filter: { showToolCalls: false, showToolResults: false, showThinking: false, myPromptsOnly: false },
      setFilter: () => {},
    };
  },
}));

vi.mock("@devthrottle/client-core/apimode/ApiModeLiveChat", () => ({
  ApiModeLiveChat: ({ sessionId, display }: { sessionId: string; display: { activityLabel: string } }) => (
    <div data-testid="live-chat">{`${sessionId} ${display.activityLabel}`}</div>
  ),
}));

vi.mock("@devthrottle/client-core/dictation/DictationStatusStrip", () => ({ DictationStatusStrip: () => null }));
vi.mock("../components/SessionAppBar", () => ({ SessionAppBar: () => <div /> }));
vi.mock("../components/ViewTabs", () => ({ ViewTabs: () => <div /> }));
vi.mock("../components/SessionControls", () => ({ SessionControls: () => <div /> }));

import { Chat } from "./Chat";

afterEach(() => {
  cleanup();
  historyReadFor.mockClear();
  rosterRead = () => Promise.resolve(roster);
});

function renderChat() {
  return render(
    <MemoryRouter initialEntries={[`/session/${SID}/chat`]}>
      <Routes>
        <Route path="/session/:sessionId/chat" element={<Chat />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("the phone Chat screen", () => {
  it("Chat_ApiModeRow_MountsTheSharedLiveChatAndTheHistoryReadStandsDown", async () => {
    roster = [{ ...row, sessionId: SID, name: "pi", connectionMode: "api", apiMode: { kind: "idle", activityLabel: "Idle after turn 1" } }];

    renderChat();

    await waitFor(() => expect(screen.getByTestId("live-chat").textContent).toBe(`${SID} Idle after turn 1`));
    expect(historyReadFor).toHaveBeenLastCalledWith(undefined);
    expect(screen.queryByText("No conversation yet.")).toBeNull();
    expect(screen.queryByText("Show:")).toBeNull();
  });

  it("Chat_ApiModeRow_NeverStartsTheHistoryReadEvenBeforeTheRosterAnswers", async () => {
    roster = [{ ...row, sessionId: SID, name: "pi", connectionMode: "api", apiMode: { kind: "idle", activityLabel: "Idle after turn 1" } }];

    renderChat();

    await waitFor(() => expect(screen.getByTestId("live-chat")).toBeTruthy());
    expect(historyReadFor).not.toHaveBeenCalledWith(SID);
  });

  it("Chat_RosterNotAnsweredYet_ShowsLoadingAndReadsNoHistory", async () => {
    rosterRead = () => new Promise(() => {});

    renderChat();
    await act(async () => { await Promise.resolve(); });

    expect(screen.getByText("Loading session...")).toBeTruthy();
    expect(screen.queryByText("No conversation yet.")).toBeNull();
    expect(screen.queryByText("Show:")).toBeNull();
    expect(historyReadFor).not.toHaveBeenCalledWith(SID);
  });

  it("Chat_RosterReadFails_TheHistoryChatStillLoads", async () => {
    // A failed roster read is an answer: no row is coming, so the history Chat works as it did before.
    rosterRead = () => Promise.reject(new Error("the Gateway could not be reached"));

    renderChat();

    await waitFor(() => expect(historyReadFor).toHaveBeenCalledWith(SID));
    expect(screen.queryByTestId("live-chat")).toBeNull();
  });

  it("Chat_TerminalModeRow_KeepsTheHistoryChat", async () => {
    roster = [{ ...row, sessionId: SID, name: "codex", connectionMode: "terminal" }];

    renderChat();

    await waitFor(() => expect(screen.getByText("No conversation yet.")).toBeTruthy());
    expect(screen.queryByTestId("live-chat")).toBeNull();
    expect(historyReadFor).toHaveBeenLastCalledWith(SID);
  });
});
