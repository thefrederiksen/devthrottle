// @vitest-environment jsdom
import type { ReactNode } from "react";
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { QueueItem, SessionDto } from "@devthrottle/client-core/api/client";

// GIVING THE PAGE ITS WIDTH BACK (issue #3074), on the session screen: the queue dock folds to a strip, and
// an open report can be opened at its own address with nothing around it at all.
//
// Both are about the same measurement. A dev report on this screen had a 220px rail, a 340px session list,
// a 300px dock and its own 340px conversation around it - 1200 pixels of chrome on a 1920 screen, leaving
// the report about 700. The dock gives back 270 of them; the full-screen address gives back all of it.

const queued = vi.hoisted(() => ({ items: [] as QueueItem[] }));
// What the tab actually handed the viewer, so a prop it stopped passing is visible here.
const viewerProps = vi.hoisted(() => ({}) as { onBackToSession?: unknown });

vi.mock("@devthrottle/client-core/api/client", () => ({
  gatewayErrorMessage: (err: unknown) => String(err),
  getQueue: () => Promise.resolve(queued.items),
  gatewayFetch: vi.fn(),
  authHeaders: () => ({}),
  GatewayError: class GatewayError extends Error {},
}));

vi.mock("@devthrottle/client-core/devreports/DevReportList", () => ({
  DevReportList: () => <div data-testid="fake-report-list" />,
}));
// The viewer is client-core's and is tested there. Here it only has to say which report it was handed, and
// to PLACE THE SHELL'S OWN ACTIONS - the tab passes All reports and Full screen into the report's one bar
// now (issue #3077), so a stand-in that dropped them would hide whether the tab still supplies them.
vi.mock("@devthrottle/client-core/devreports/DevReportViewer", () => ({
  DevReportViewer: (props: { reportId: string; leading?: ReactNode; trailing?: ReactNode; onBackToSession?: unknown }) => {
    Object.assign(viewerProps, props);
    return (
      <div data-testid="fake-report-bar">
        {props.leading}
        <span data-testid="fake-report-viewer">{props.reportId}</span>
        {props.trailing}
      </div>
    );
  },
}));
vi.mock("@devthrottle/client-core/devreports/DevReportConversation", () => ({ DevReportConversation: () => null }));

vi.mock("@devthrottle/client-core/sessions/VerdictPanel", () => ({ VerdictPanel: () => null }));
vi.mock("@devthrottle/client-core/sessions/WingmanTab", () => ({ WingmanTab: () => <div /> }));
vi.mock("./StopSessionProvider", () => ({ useStopSession: () => ({ openStop: vi.fn() }) }));
vi.mock("../panes/TerminalPane", () => ({ TerminalPane: () => <div /> }));
vi.mock("./SessionActionBar", () => ({ SessionActionBar: () => <div /> }));
vi.mock("./SessionComposer", () => ({ SessionComposer: () => <div /> }));
vi.mock("./SessionMenu", () => ({ SessionMenu: () => null }));
vi.mock("./ChatTab", () => ({ ChatTab: () => <div /> }));
vi.mock("./VoiceTab", () => ({ VoiceTab: () => <div /> }));
vi.mock("./SourceControlTab", () => ({ SourceControlTab: () => <div /> }));
vi.mock("./QueuePanel", () => ({ QueuePanel: () => <div data-testid="fake-queue-panel" /> }));
vi.mock("./ScreenshotsPanel", () => ({ ScreenshotsPanel: () => <div /> }));

import { SessionDetail } from "./SessionDetail";

const SID = "7d2f9c10-0000-4000-8000-000000000031";
const REPORT = "b41e77a2-0000-4000-8000-0000000000aa";

function mountAt(entry: string) {
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <Routes>
        <Route element={<Outlet context={{ sessions: [{ sessionId: SID } as unknown as SessionDto] }} />}>
          <Route path="/session/:sessionId" element={<SessionDetail />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  window.localStorage.clear();
  queued.items = [];
  delete viewerProps.onBackToSession;
});
afterEach(() => cleanup());

describe("the session queue dock", () => {
  it("opens expanded, showing the queue", () => {
    mountAt(`/session/${SID}`);

    expect(screen.getByTestId("fake-queue-panel")).toBeTruthy();
    expect(screen.getByTestId("dock-collapse").getAttribute("aria-expanded")).toBe("true");
    expect(document.querySelector(".session-detail-dock-collapsed")).toBeNull();
  });

  it("folds to a strip that can fold back", () => {
    mountAt(`/session/${SID}`);

    fireEvent.click(screen.getByTestId("dock-collapse"));

    expect(document.querySelector(".session-detail-dock-collapsed")).toBeTruthy();
    expect(screen.queryByTestId("fake-queue-panel")).toBeNull();
    // THE WAY BACK IS STILL ON SCREEN. A dock that folds away without leaving its own control behind is a
    // panel the reader cannot reopen.
    const toggle = screen.getByTestId("dock-collapse");
    expect(toggle.getAttribute("aria-expanded")).toBe("false");

    fireEvent.click(toggle);

    expect(document.querySelector(".session-detail-dock-collapsed")).toBeNull();
    expect(screen.getByTestId("fake-queue-panel")).toBeTruthy();
  });

  it("says on the strip how many prompts are queued behind it", async () => {
    queued.items = [{ id: "1" } as unknown as QueueItem, { id: "2" } as unknown as QueueItem];
    mountAt(`/session/${SID}`);
    await waitFor(() => expect(screen.getByRole("button", { name: /Queue \(2\)/ })).toBeTruthy());

    fireEvent.click(screen.getByTestId("dock-collapse"));

    // Folded away, not out of mind: something arriving in a hidden queue still says so.
    expect(screen.getByTestId("dock-collapse").textContent).toContain("2");
  });

  it("remembers the choice for the next time this browser opens a session", () => {
    mountAt(`/session/${SID}`);
    fireEvent.click(screen.getByTestId("dock-collapse"));
    expect(window.localStorage.getItem("cockpit.dockCollapsed")).toBe("true");

    cleanup();
    mountAt(`/session/${SID}`);

    expect(document.querySelector(".session-detail-dock-collapsed")).toBeTruthy();
  });
});

describe("the open report's way to its own address", () => {
  it("offers the same report full screen, at the address the printed link resolves to, in a new tab", () => {
    mountAt(`/session/${SID}?tab=reports&report=${REPORT}`);

    const link = screen.getByTestId("reports-fullscreen");
    // A real link to a real address: the reader can copy it, and what he opens is what the person he sends
    // it to will see.
    expect(link.getAttribute("href")).toBe(`/report/${REPORT}`);
    expect(link.getAttribute("target")).toBe("_blank");
    // Leaving the session behind is not what he asked for, so the new tab must not be able to reach back
    // into this one.
    expect(link.getAttribute("rel")).toBe("noopener noreferrer");
  });

  it("offers no way back to the session it is already in", () => {
    // ISSUE #3077. Reading a report in session 103's tab, the bar said "back to 103 devthrottle_internal -
    // pe seller" - the screen the reader was standing on. Every report in this list belongs to this session,
    // so the tab hands the viewer no destination and the viewer draws no link. The full-screen page, which
    // is where that link leads somewhere, passes one and keeps it.
    mountAt(`/session/${SID}?tab=reports&report=${REPORT}`);

    expect(viewerProps.onBackToSession).toBeUndefined();
    // Not an empty screen: the actions the tab DOES supply are there.
    expect(screen.getByTestId("reports-back")).toBeTruthy();
    expect(screen.getByTestId("reports-fullscreen")).toBeTruthy();
  });

  it("offers it only when a report is open - the list has no full screen", () => {
    mountAt(`/session/${SID}?tab=reports`);

    expect(screen.getByTestId("fake-report-list")).toBeTruthy();
    expect(screen.queryByTestId("reports-fullscreen")).toBeNull();
  });
});
