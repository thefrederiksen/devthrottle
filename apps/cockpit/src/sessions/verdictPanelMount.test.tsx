// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// ONE STOP, DRAWN ONCE. This drives the REAL SessionDetail with a judged row in its outlet context and proves the
// shell mounts NO verdict panel, on any tab.
//
// Two rulings, one after the other, brought it here. The owner ruled (2026-09-17) that the panel must not sit above
// Terminal, Chat, Voice and Source Control, where it crowded views it has nothing to do with - that half is the four
// cases below. Then Now landed on the Wingman tab and drew the same live stop with more room, so the panel above it
// meant the SAME stop twice on one screen, each copy with its own answer buttons; the approved mockup draws no panel
// there, and the Architect ruled it out. The shared component is untouched and still mounts wherever else it is used.
//
// A shell that brings the panel back anywhere goes red here.

vi.mock("@devthrottle/client-core/api/client", () => ({
  gatewayErrorMessage: (err: unknown) => String(err),
  getQueue: () => Promise.resolve([]),
  gatewayFetch: vi.fn(),
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {},
}));

// The page's other regions cannot run in jsdom (a terminal engine, a live socket, a microphone) and are not the
// subject.
vi.mock("../panes/TerminalPane", () => ({ TerminalPane: () => <div /> }));
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

const SID = "7d2f9c10-0000-4000-8000-000000000002";

function judged(): SessionDto {
  return {
    sessionId: SID,
    directorId: "d1",
    machineName: "SORENLAPTOP",
    repoPath: "D:/Repos/scratch",
    agent: "ClaudeCode",
    agentToolDisplay: "Claude Code",
    activityState: "WaitingForInput",
    effectiveColor: "red",
    stateLabel: "Apply the migration now?",
    triageBucket: "needsYou",
    verdictState: "judged",
    turnVerdict: {
      verdictId: "tv-cockpit-1",
      verdict: "needed-you",
      confidence: "high",
      evidence: "Apply the migration to the local database now?",
      label: "Apply the migration now?",
      summary: "The session is asking before it changes the database.",
      answerVia: "reply",
      menu: null,
      options: [
        { key: "Apply it", send: "yes, apply it", recommended: true, note: "Changes the local database." },
        { key: "Leave it", send: "no", recommended: false, note: "Nothing changes." },
      ],
      risk: "none",
    },
  } as unknown as SessionDto;
}

function Shell({ sessions }: { sessions: SessionDto[] }) {
  return <Outlet context={{ sessions }} />;
}

afterEach(() => cleanup());

function renderDetail() {
  render(
    <MemoryRouter initialEntries={[`/sessions/${SID}`]}>
      <Routes>
        <Route element={<Shell sessions={[judged()]} />}>
          <Route path="/sessions/:sessionId" element={<SessionDetail />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

describe("the Cockpit session view", () => {
  it.each(["Terminal", "Chat", "Voice", "Source Control"])(
    "does not show the verdict panel on the %s tab",
    (name) => {
      renderDetail();
      // Visit Wingman first, so a panel that stayed mounted after leaving it would also go red.
      fireEvent.click(screen.getByRole("tab", { name: "Wingman" }));
      fireEvent.click(screen.getByRole("tab", { name }));

      expect(screen.queryByRole("region", { name: "Wingman verdict" })).toBeNull();
      expect(screen.queryByText("Claude Code said")).toBeNull();
    },
  );

  it("shows no verdict panel on the Wingman tab either, because Now draws that stop", () => {
    renderDetail();
    fireEvent.click(screen.getByRole("tab", { name: "Wingman" }));

    expect(screen.queryByRole("region", { name: "Wingman verdict" })).toBeNull();
    expect(screen.queryByText("Claude Code said")).toBeNull();
    // Two answer buttons for one stop on one screen is the defect this closes.
    expect(screen.queryByRole("button", { name: "Apply it" })).toBeNull();
  });
});
