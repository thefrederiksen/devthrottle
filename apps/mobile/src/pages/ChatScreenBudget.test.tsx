// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// WHAT THE CHAT SCREEN SPENDS ITS HEIGHT ON.
//
// The phone's session screen is position:fixed and does not scroll, so every row on it is taken from the
// conversation. Measured on the owner's own phone (412x1005 CSS px) with a judged verdict on the row, the
// conversation had 324px of 942 - 34% - while two rows of large keys it had not asked for held 108px and
// the verdict panel restated the reply that was already at the top of the conversation.
//
// His words, with a screenshot: "I can barely see my fucking screen."
//
// These tests pin the two decisions that gave the space back. They assert the DEFAULTS, because the
// defaults are the whole change - every control here already existed and none is removed.

const SID = "3a8e5d21-0000-4000-8000-000000000009";

const judged = {
  sessionId: SID,
  name: "scratch - migration",
  activityState: "WaitingForInput",
  effectiveColor: "red",
  stateLabel: "Apply the migration now?",
  triageBucket: "needsYou",
  agent: "Codex",
  agentToolDisplay: "Codex",
  verdictState: "judged",
  turnVerdict: {
    verdictId: "tv-budget-1",
    verdict: "needed-you",
    confidence: "high",
    evidence: "Apply the migration to the local database now?",
    label: "Apply the migration now?",
    summary: "The session is asking before it changes the database.",
    answerVia: "reply",
    options: [],
    risk: "none",
  },
};

vi.mock("@devthrottle/client-core/api/client", () => ({
  listSessions: () => Promise.resolve([judged]),
  holdSession: vi.fn(),
  stopSession: vi.fn(),
  gatewayErrorMessage: (err: unknown) => String(err),
  gatewayFetch: vi.fn(),
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {},
}));

vi.mock("@devthrottle/client-core/history/useSessionChat", () => ({
  useSessionChat: () => ({
    bubbles: [],
    emptyText: "No conversation yet.",
    staleNotice: null,
    loadFailed: false,
    filter: { showToolCalls: false, showToolResults: false, showThinking: false },
    setFilter: () => {},
  }),
}));

vi.mock("@devthrottle/client-core/dictation/DictationStatusStrip", () => ({ DictationStatusStrip: () => null }));
vi.mock("../components/SessionAppBar", () => ({ SessionAppBar: () => <div /> }));
vi.mock("../components/ViewTabs", () => ({ ViewTabs: () => <div /> }));
// SessionControls is NOT mocked here - it is the subject.

import { Chat } from "./Chat";

afterEach(cleanup);

function renderChat() {
  return render(
    <MemoryRouter initialEntries={[`/session/${SID}`]}>
      <Routes>
        <Route path="/session/:sessionId" element={<Chat />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("what the chat screen spends its height on", () => {
  it("does not open with the Enter/Esc/Stop and arrow rows taking the screen", async () => {
    renderChat();
    // The reply controls are always there - this screen is for replying.
    await waitFor(() => expect(screen.getByRole("button", { name: "Send" })).toBeTruthy());
    expect(screen.getByRole("button", { name: "Speak" })).toBeTruthy();

    // The two key rows are not, until asked for. This is the Terminal's long-standing default, which
    // the Chat passed a hard-coded `showKeyRows` straight past.
    expect(screen.queryByRole("button", { name: "Enter" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Esc" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Up" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Right" })).toBeNull();
  });

  it("gives them back on one tap, and takes them away again", async () => {
    renderChat();
    const keys = await screen.findByRole("button", { name: "Keys" });

    fireEvent.click(keys);
    expect(screen.getByRole("button", { name: "Enter" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Up" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Hide keys" }));
    expect(screen.queryByRole("button", { name: "Enter" })).toBeNull();
  });

  it("opens the verdict receipt COLLAPSED, because it restates the conversation below it", async () => {
    renderChat();
    // The panel is there and says whose words the receipt holds...
    const summary = await screen.findByText("Codex said");
    const receipt = summary.closest("details");
    expect(receipt).not.toBeNull();
    // ...and it is shut, so it costs one line instead of the reply twice over.
    expect((receipt as HTMLDetailsElement).open).toBe(false);
  });

  it("still shows the verdict's own words, which are NOT a duplicate of anything", async () => {
    // The collapse must not be read as "hide the panel". The label and summary are the Wingman's
    // judgement and appear nowhere else on this screen; only the quoted reply is a duplicate.
    renderChat();
    expect(await screen.findByText("Apply the migration now?")).toBeTruthy();
    expect(screen.getByText("The session is asking before it changes the database.")).toBeTruthy();
  });
});
