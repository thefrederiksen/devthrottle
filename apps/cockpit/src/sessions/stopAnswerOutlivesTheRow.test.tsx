// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// THE STOP ANSWER MUST OUTLIVE THE ROW IT DESCRIBES (mission "Stop a session", inspection finding I4).
//
// The defect this pins is a LIFECYCLE defect, and it is invisible from inside the component that had
// it. SessionMenu held the stop's answer in its own state, and both places that mount SessionMenu exist
// only while the session exists: one per roster card (SessionRoster) and one behind `selected && ...`
// on the session page (SessionDetail). A successful stop removes that row; the shared roster poll
// refreshes every two seconds (rosterStore.ROSTER_POLL_MS); the refresh unmounted the menu and its
// portal and destroyed the answer with no Done, no backdrop click and nothing read. A refresh that
// landed before the response arrived meant the answer never became visible at all.
//
// So these tests DRIVE THE REAL PARENTS and then take the row away, which is the only way to see it:
//   * the real SessionRoster, re-rendered with the stopped row absent, the way the shared roster store
//     re-renders it two seconds after the stop;
//   * the real SessionDetail, re-rendered with the session gone from its outlet context, which is what
//     switches off its `selected && <SessionMenu ...>`.
// A test that mounts SessionMenu on its own cannot see this defect and is not the test.
//
// What is NOT covered here, said plainly: no browser, no pixels, no real roster poll and no real
// Gateway. jsdom renders no layout, so "the dialog is on top of the page and readable" is not proven by
// anything below - only that the answer is still mounted and still says the Gateway's words.

const stopSessionMock = vi.fn();

// The Gateway boundary. The provider's stopSession is the only call under test; the rest are what the
// roster, the menu and the restart panel reach for while they render.
vi.mock("@devthrottle/client-core/api/client", () => ({
  gatewayErrorMessage: (err: unknown) => String(err),
  setVoiceModeAllSessions: vi.fn(async () => ({ changed: 0, skipped: 0 })),
  stopSession: (...args: unknown[]) => stopSessionMock(...args),
  holdSession: () => Promise.resolve({ onHold: false, pending: false }),
  getHandover: () => Promise.resolve(null),
  getQueue: () => Promise.resolve([]),
}));

// The lengths cache would otherwise reach for the Gateway on mount. Null is a real state and the menu
// is fully usable in it.
vi.mock("@devthrottle/client-core/settings/snoozeOptions", () => ({
  useSnoozeOptions: () => null,
}));

// The client-error channel posts with a raw keepalive fetch; nothing here is about that channel.
vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  reportClientError: () => {},
  describeAndReport: (_surface: string, _action: string, err: unknown) => String(err),
}));

// The session page's other regions. None of them is the subject: what is under test is SessionDetail's
// own `selected && <SessionMenu ...>` gate and the provider above it, and these are the parts of the
// page that cannot run in jsdom (a terminal engine, a live socket, a microphone) or that would drag in
// half the app to render a header. The gate itself, the outlet context it reads, and the menu it mounts
// are all the real ones.
vi.mock("../panes/TerminalPane", () => ({ TerminalPane: () => <div /> }));
vi.mock("./SessionActionBar", () => ({ SessionActionBar: () => <div /> }));
vi.mock("./SessionComposer", () => ({ SessionComposer: () => <div /> }));
vi.mock("./ChatTab", () => ({ ChatTab: () => <div /> }));
vi.mock("./VoiceTab", () => ({ VoiceTab: () => <div /> }));
vi.mock("./SourceControlTab", () => ({ SourceControlTab: () => <div /> }));
vi.mock("./QueuePanel", () => ({ QueuePanel: () => <div /> }));
vi.mock("./ScreenshotsPanel", () => ({ ScreenshotsPanel: () => <div /> }));

import { SessionRoster } from "./SessionRoster";
import { SessionDetail } from "./SessionDetail";
import { StopSessionProvider } from "./StopSessionProvider";

const SID = "9c41e7a2-0000-4000-8000-000000000000";

// A headline no client could have invented, so a view that composed its own words could not pass.
const HEADLINE = "stopped 9c41e7a2 - process 51884 ended, row removed";

function session(): SessionDto {
  return {
    sessionId: SID,
    directorId: "d1",
    machineName: "SORENLAPTOP",
    repoPath: "D:/Repos/scratch",
    agent: "ClaudeCode",
    activityState: "Waiting",
    stateLabel: "Waiting",
    triageBucket: "active",
    effectiveColorHex: "#3B82F6",
    createdAt: "2026-09-09T10:00:00Z",
    sortOrder: 0,
    name: "throwaway",
  } as unknown as SessionDto;
}

function answer(over: Record<string, unknown> = {}) {
  return {
    verdict: "stopped",
    headline: HEADLINE,
    details: ["reason: spawned into the wrong mode"],
    sessionId: SID,
    shortId: "9c41e7a2",
    processId: 51884,
    processEnded: true,
    rowRemoved: true,
    worktreePath: null,
    worktreeHadUncommittedChanges: null,
    reason: "spawned into the wrong mode",
    stoppedBy: "session 0022aa52",
    killed: true,
    removed: true,
    ...over,
  };
}

/** The real roster, inside the provider AppShell mounts in the product. */
function roster(sessions: SessionDto[]) {
  return (
    <MemoryRouter initialEntries={["/sessions"]}>
      <StopSessionProvider>
        <SessionRoster
          sessions={sessions}
          directors={[]}
          portByDirector={new Map()}
          selectedId={undefined}
          view="my-order"
          onView={() => {}}
          error={null}
          onNewSession={() => {}}
        />
      </StopSessionProvider>
    </MemoryRouter>
  );
}

/** The real session page, routed and given its outlet context the way SessionsView gives it. */
function detail(sessions: SessionDto[]) {
  return (
    <MemoryRouter initialEntries={[`/session/${SID}`]}>
      <StopSessionProvider>
        <Routes>
          <Route path="/" element={<Outlet context={{ sessions, directors: [] }} />}>
            <Route path="session/:sessionId" element={<SessionDetail />} />
          </Route>
          <Route path="/sessions" element={<div>the roster</div>} />
        </Routes>
      </StopSessionProvider>
    </MemoryRouter>
  );
}

// The names the ROSTER is actually listing, read off the rendered rows rather than off the whole
// document - the stop dialog names the session it is asking about too.
function rosterRowNames(): string[] {
  return Array.from(document.querySelectorAll(".roster-list .roster-name-text")).map(
    (el) => el.textContent ?? "",
  );
}

function openStopDialog() {
  fireEvent.click(screen.getByLabelText("Session menu"));
  fireEvent.click(screen.getByRole("menuitem", { name: "Stop session" }));
}

function reasonBox() {
  return screen.getByPlaceholderText("Spawned into the wrong mode");
}

function confirmStop() {
  fireEvent.change(reasonBox(), { target: { value: "spawned into the wrong mode" } });
  fireEvent.click(screen.getByRole("button", { name: /^Stop session$/ }));
}

beforeEach(() => {
  stopSessionMock.mockReset();
});

afterEach(() => {
  cleanup();
});

describe("the stop answer survives the roster refresh that the stop itself caused", () => {
  it("keeps the Gateway's answer on screen after the stopped row leaves the roster", async () => {
    stopSessionMock.mockResolvedValue(answer());
    const { rerender } = render(roster([session()]));

    openStopDialog();
    confirmStop();
    expect(await screen.findByText(HEADLINE)).toBeTruthy();

    // Two seconds later the shared roster poll returns a fleet without that session, and the row - with
    // the menu that started the stop - is unmounted. The answer is not the row's to take.
    rerender(roster([]));

    expect(rosterRowNames()).toEqual([]);
    expect(screen.getByText(HEADLINE)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Done" })).toBeTruthy();
    // The detail line is the Gateway's too, and it survives with the headline.
    expect(screen.getByText("reason: spawned into the wrong mode")).toBeTruthy();
  });

  it("shows an answer that arrives AFTER the row has already gone", async () => {
    // The refresh can land before the response does, in which case the old dialog was destroyed before
    // the answer it was waiting for ever existed - the operator saw nothing at all.
    let release: (value: unknown) => void = () => {};
    stopSessionMock.mockReturnValue(new Promise((resolve) => { release = resolve; }));
    const { rerender } = render(roster([session()]));

    openStopDialog();
    confirmStop();
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));

    // The row goes while the request is still outstanding. (The name is still on screen - the dialog's
    // own question names the session it is asking about - so the row's absence is read off the roster
    // itself, not off the name.)
    rerender(roster([]));
    expect(rosterRowNames()).toEqual([]);

    release(answer());
    expect(await screen.findByText(HEADLINE)).toBeTruthy();
  });

  it("clears the answer only when it is dismissed, never when the row leaves", async () => {
    stopSessionMock.mockResolvedValue(answer());
    const { rerender } = render(roster([session()]));

    openStopDialog();
    confirmStop();
    expect(await screen.findByText(HEADLINE)).toBeTruthy();
    rerender(roster([]));

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByText(HEADLINE)).toBeNull());
  });
});

describe("the stop answer survives the session page switching its own menu off", () => {
  it("keeps the answer after the session leaves the page's roster context", async () => {
    stopSessionMock.mockResolvedValue(answer());
    const { rerender } = render(detail([session()]));

    openStopDialog();
    confirmStop();
    expect(await screen.findByText(HEADLINE)).toBeTruthy();

    // `selected` goes undefined, so the page stops rendering its SessionMenu entirely.
    rerender(detail([]));

    expect(screen.queryByLabelText("Session menu")).toBeNull();
    expect(screen.getByText(HEADLINE)).toBeTruthy();
  });

  it("navigates away only once the answer has been dismissed", async () => {
    stopSessionMock.mockResolvedValue(answer());
    const { rerender } = render(detail([session()]));

    openStopDialog();
    confirmStop();
    expect(await screen.findByText(HEADLINE)).toBeTruthy();
    rerender(detail([]));

    // The page has NOT been told it may leave: the roster route is not on screen.
    expect(screen.queryByText("the roster")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.getByText("the roster")).toBeTruthy());
  });
});

describe("the stop dialog is one owner above the roster, not one per row", () => {
  it("puts a single dialog on screen no matter how many rows are listed", async () => {
    stopSessionMock.mockResolvedValue(answer());
    const second = { ...session(), sessionId: "aa000000-0000-4000-8000-000000000000", name: "the other one" } as SessionDto;
    render(roster([session(), second]));

    // Open the FIRST row's menu and stop it.
    const menus = screen.getAllByLabelText("Session menu");
    fireEvent.click(menus[0]);
    fireEvent.click(screen.getByRole("menuitem", { name: "Stop session" }));
    confirmStop();

    expect(await screen.findByText(HEADLINE)).toBeTruthy();
    const dialogs = screen.getAllByRole("dialog");
    expect(dialogs.length).toBe(1);
    expect(within(dialogs[0]).getByText(HEADLINE)).toBeTruthy();
    expect(stopSessionMock).toHaveBeenCalledWith(SID, "spawned into the wrong mode");
  });
});
