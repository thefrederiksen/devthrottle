// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";

// THE PHONE MOUNTS THE VERDICT PANEL (the Wingman-on-every-turn mission, slice E). The panel lives once in
// client-core and the phone session screen only mounts it, fed by the roster poll useSessionManage already runs.
// This drives the REAL Chat page and the REAL hook against a roster holding a judged row, so removing either the
// mount or the hook's session goes red.

const SID = "3a8e5d21-0000-4000-8000-000000000003";

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
    verdictId: "tv-phone-1",
    verdict: "needed-you",
    confidence: "high",
    evidence: "Apply the migration to the local database now?",
    label: "Apply the migration now?",
    summary: "The session is asking before it changes the database.",
    answerVia: "keys",
    menu: { question: "Apply the migration now?", selectionMode: "single", submit: "" },
    options: [
      { key: "Yes, apply it", send: "1", recommended: true, note: "Changes the local database." },
      { key: "No, leave it", send: "2", recommended: false, note: "Nothing changes." },
    ],
    risk: "spends-money",
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

// The screen's other regions reach for a microphone, a socket or the navigation drawer and are not the subject.
vi.mock("@devthrottle/client-core/dictation/DictationStatusStrip", () => ({ DictationStatusStrip: () => null }));
vi.mock("../components/SessionAppBar", () => ({ SessionAppBar: () => <div /> }));
vi.mock("../components/SessionControls", () => ({ SessionControls: () => <div /> }));
vi.mock("../components/ViewTabs", () => ({ ViewTabs: () => <div /> }));

import { Chat } from "./Chat";

afterEach(() => cleanup());

describe("the phone session screen", () => {
  it("mounts the shared verdict panel for a judged row from the roster poll", async () => {
    render(
      <MemoryRouter initialEntries={[`/session/${SID}`]}>
        <Routes>
          <Route path="/session/:sessionId" element={<Chat />} />
        </Routes>
      </MemoryRouter>,
    );

    expect(await screen.findByRole("region", { name: "Wingman verdict" })).toBeTruthy();
    expect(screen.getByText("Codex said")).toBeTruthy();
    expect(screen.getByRole("note").textContent).toBe("Risk: spends-money");
    expect(screen.getByRole("button", { name: "Yes, apply it" })).toBeTruthy();
  });
});
