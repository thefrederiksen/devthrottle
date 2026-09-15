// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// THE COCKPIT MOUNTS THE VERDICT PANEL (the Wingman-on-every-turn mission, slice E). The panel lives once in
// client-core and each shell only mounts it, so the one thing this shell can get wrong is not mounting it. This
// drives the REAL SessionDetail with a judged row in its outlet context, so deleting the mount goes red.

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

describe("the Cockpit session view", () => {
  it("mounts the shared verdict panel for a judged row", () => {
    render(
      <MemoryRouter initialEntries={[`/sessions/${SID}`]}>
        <Routes>
          <Route element={<Shell sessions={[judged()]} />}>
            <Route path="/sessions/:sessionId" element={<SessionDetail />} />
          </Route>
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByRole("region", { name: "Wingman verdict" })).toBeTruthy();
    expect(screen.getByText("Claude Code said")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Apply it" })).toBeTruthy();
  });
});
