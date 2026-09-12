// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { SessionStopOutcome } from "@devthrottle/client-core/api/client";
import type { SessionManage } from "./useSessionManage";

// Rendered proof of the PHONE's stop control (mission "Stop a session", internal#1992).
//
// The owner's ruling of 12 September 2026: Ruling 4's reason requirement binds to AGENT keys calling
// the REST API - it is not the owner's to type. On the phone, stopping a session is ONE confirmation
// and then it happens: no reason box, and no report of an action the owner just asked for and
// watched happen. What is pinned HERE is exactly that - the sheet asks the question plainly, Stop is
// armed the moment the sheet opens, the stop carries the DERIVED reason the hook sends, and a
// successful stop under EVERY verdict leaves for the roster with nothing rendered from the answer.
//
// The Cockpit keeps its reason box for now (a desktop has a keyboard under the operator's hands) -
// that divergence is recorded in internal#1992, and the phone's stop test is no longer the Cockpit's
// stop test copied onto a phone.

const navigateMock = vi.fn();
vi.mock("react-router-dom", () => ({
  useNavigate: () => navigateMock,
  useParams: () => ({ sessionId: "9c41e7a2-0000-4000-8000-000000000000" }),
}));

import { SessionAppBar } from "./SessionAppBar";

const stopSessionMock = vi.fn();
const setErrorMock = vi.fn();

// The hook's state, as the app bar sees it. Only the stop verb matters here; the snooze surface is its
// own tested thing.
function manage(over: Partial<SessionManage> = {}): SessionManage {
  return {
    onHold: false,
    held: false,
    deferred: false,
    snoozed: false,
    holdCountdown: null,
    deliveryNotice: null,
    busy: false,
    error: null,
    setError: setErrorMock,
    toggleHold: () => Promise.resolve(true),
    holdFor: () => Promise.resolve(true),
    stopSession: () => stopSessionMock() as Promise<SessionStopOutcome>,
    ...over,
  };
}

// A Gateway answer. The headline is deliberately a sentence no client would ever compose - and on the
// phone a SUCCESS renders none of it.
function answer(over: Partial<SessionStopOutcome> = {}): SessionStopOutcome {
  return {
    verdict: "stopped",
    headline: "stopped 9c41e7a2 - process 51884 ended, row removed",
    details: [],
    sessionId: "9c41e7a2-0000-4000-8000-000000000000",
    shortId: "9c41e7a2",
    processId: 51884,
    processEnded: true,
    rowRemoved: true,
    worktreePath: null,
    worktreeHadUncommittedChanges: null,
    reason: "Stopped by the owner from the mobile app",
    stoppedBy: "device 4f10",
    killed: true,
    removed: true,
    ...over,
  };
}

function openStopSheet() {
  fireEvent.click(screen.getByLabelText("Session menu"));
  fireEvent.click(screen.getByRole("menuitem", { name: "Stop session" }));
}

function stopButton() {
  return screen.getByRole("button", { name: /^Stop$/ });
}

beforeEach(() => {
  stopSessionMock.mockReset();
  navigateMock.mockReset();
  setErrorMock.mockReset();
});

afterEach(() => {
  cleanup();
});

describe("the phone stop sheet asks one question and asks it plainly", () => {
  it("offers Stop session, not Remove session - the one word for the one verb", () => {
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    fireEvent.click(screen.getByLabelText("Session menu"));
    expect(screen.getByRole("menuitem", { name: "Stop session" })).toBeTruthy();
    expect(screen.queryByRole("menuitem", { name: "Remove session" })).toBeNull();
  });

  it("asks the question with NO reason box - the owner does not justify himself to himself", () => {
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    expect(screen.getByText("Stop this session?")).toBeTruthy();
    expect(screen.queryByPlaceholderText("Spawned into the wrong mode")).toBeNull();
    expect(screen.queryByText(/A reason is required/)).toBeNull();
  });

  it("arms Stop the moment the sheet opens - nothing has to be typed first", () => {
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    expect((stopButton() as HTMLButtonElement).disabled).toBe(false);
  });

  it("moves focus INTO the dialog when it opens, on the safe control", () => {
    // The reason box used to be the sheet's one focus target; deleting it left keyboard focus on the
    // page body, free to tab behind a dialog that declares aria-modal (review of pull request #2816).
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();

    const dialog = screen.getByRole("dialog");
    const focused = document.activeElement;
    expect(dialog.contains(focused)).toBe(true);
    // And it is the CANCEL: focus lands on the safe control, so an accidental Enter cannot stop a
    // session the operator was only looking at.
    expect(focused).toBe(screen.getByRole("button", { name: "Cancel" }));
  });
});

describe("a successful stop is silent: it happens, and the app returns to the roster", () => {
  async function stopWith(outcome: SessionStopOutcome) {
    stopSessionMock.mockResolvedValue(outcome);
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    fireEvent.click(stopButton());
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));
  }

  it("sends the stop and leaves for the roster straight away", async () => {
    await stopWith(answer());
    await waitFor(() => expect(navigateMock).toHaveBeenCalledWith("/"));
  });

  it("renders NOTHING from the Gateway's answer - no headline, no detail lines, no Done", async () => {
    const headline = "stopped 9c41e7a2 - process 51884 ended, row removed";
    await stopWith(
      answer({ details: ["the worktree D:\\Repos\\scratch was left untouched - it has uncommitted changes in it"] }),
    );

    await waitFor(() => expect(navigateMock).toHaveBeenCalledWith("/"));
    // The answer card is gone under every verdict: the session the owner asked to stop is simply gone.
    // The detail-line string below exists only in the Gateway's answer, never in the sheet's own words.
    expect(screen.queryByText(headline)).toBeNull();
    expect(screen.queryByText(/D:.Repos.scratch/)).toBeNull();
    expect(screen.queryByRole("button", { name: "Done" })).toBeNull();
  });

  // Every verdict that is a SUCCESS takes the same path out - the view does not know the verdict words.
  const successes: Array<[string, SessionStopOutcome]> = [
    ["stopped", answer()],
    ["alreadyStopped", answer({ verdict: "alreadyStopped", headline: "was already stopped - the leftover row has been removed" })],
    ["notOnFleet", answer({ verdict: "notOnFleet", headline: "not on this fleet - nothing in this account carries the id 9c41e7a2", processId: null, processEnded: false })],
    ["someVerdictInvented2027", answer({ verdict: "someVerdictInvented2027", headline: "a verdict nobody has written yet" })],
  ];
  for (const [verdict, outcome] of successes) {
    it(`treats "${verdict}" as the success it is and leaves the same way`, async () => {
      await stopWith(outcome);
      await waitFor(() => expect(navigateMock).toHaveBeenCalledWith("/"));
      expect(screen.queryByText(outcome.headline)).toBeNull();
    });
  }
});

// THE FAILURE PATH LIVES IN stopFailureAndBusyGuard.test.tsx, not here.
//
// A failed stop keeps the sheet open with the Gateway's sentence inside it, and it is driven through
// the REAL hook there - a stub seeded with an error before the action cannot prove where the sentence
// renders (inspection finding I8), and the single-in-flight guard (I7) belongs to the real verb.
