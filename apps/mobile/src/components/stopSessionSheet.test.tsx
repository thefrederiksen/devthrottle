// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { SessionStopOutcome } from "@devthrottle/client-core/api/client";
import type { SessionManage } from "./useSessionManage";

// Rendered proof of the PHONE's stop control (mission "Stop a session", Rulings 4 and 5).
//
// It is the Cockpit's stop dialog laid out for a phone, and these tests are deliberately the same
// assertions as the Cockpit's: the reason is asked for before anything happens and an empty one cannot be
// submitted; the answer is rendered FROM the Gateway's headline and details; every verdict goes through
// the same path; and leaving for the roster happens when the user dismisses the answer, never when the
// call returns. Two surfaces that end a session two different ways is exactly what CLAUDE.md rule 8's
// reasoning forbids, and two test files that check different things is how that drift starts.

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
    stopSession: (reason: string) => stopSessionMock(reason) as Promise<SessionStopOutcome>,
    ...over,
  };
}

// A Gateway answer. The headline is deliberately a sentence no client would ever compose.
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
    reason: "spawned into the wrong mode",
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

function reasonBox() {
  return screen.getByPlaceholderText("Spawned into the wrong mode");
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

describe("the phone stop sheet asks for the reason before it acts", () => {
  it("offers Stop session, not Remove session - the same word the Cockpit uses", () => {
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    fireEvent.click(screen.getByLabelText("Session menu"));
    expect(screen.getByRole("menuitem", { name: "Stop session" })).toBeTruthy();
    expect(screen.queryByRole("menuitem", { name: "Remove session" })).toBeNull();
  });

  it("says a reason is required and what it is for", () => {
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    expect(screen.getByText(/A reason is required/)).toBeTruthy();
    expect(screen.getByText(/recorded with the stop/)).toBeTruthy();
  });

  it("will not submit an EMPTY reason", () => {
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    expect((stopButton() as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(stopButton());
    expect(stopSessionMock).not.toHaveBeenCalled();
  });

  it("will not submit a WHITESPACE-ONLY reason", () => {
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    fireEvent.change(reasonBox(), { target: { value: "   \t  " } });
    expect((stopButton() as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(stopButton());
    expect(stopSessionMock).not.toHaveBeenCalled();
  });

  it("sends the trimmed reason once there is one", async () => {
    stopSessionMock.mockResolvedValue(answer());
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    fireEvent.change(reasonBox(), { target: { value: "  spawned into the wrong mode  " } });
    fireEvent.click(stopButton());

    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));
    expect(stopSessionMock).toHaveBeenCalledWith("spawned into the wrong mode");
  });
});

describe("the phone stop sheet renders the Gateway's answer, verbatim", () => {
  async function stopWith(outcome: SessionStopOutcome) {
    stopSessionMock.mockResolvedValue(outcome);
    render(<SessionAppBar title="throwaway" manage={manage()} />);
    openStopSheet();
    fireEvent.change(reasonBox(), { target: { value: "spawned into the wrong mode" } });
    fireEvent.click(stopButton());
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalled());
  }

  it("shows a headline no client could have invented", async () => {
    const headline = "the Gateway wrote this exact sentence and the phone did not";
    await stopWith(answer({ headline }));
    expect(await screen.findByText(headline)).toBeTruthy();
  });

  it("shows every detail line, in the order the Gateway sent them", async () => {
    await stopWith(
      answer({
        details: [
          "the worktree D:\\Repos\\scratch was left untouched - it has uncommitted changes in it",
          "reason: spawned into the wrong mode",
        ],
      }),
    );
    const items = await screen.findAllByRole("listitem");
    expect(items.map((li) => li.textContent)).toEqual([
      "the worktree D:\\Repos\\scratch was left untouched - it has uncommitted changes in it",
      "reason: spawned into the wrong mode",
    ]);
  });

  // Four verdict words today, and a fifth that does not exist - the view must not know how many there are.
  const verdicts = ["stopped", "alreadyStopped", "notOnFleet", "stoppedNotDescribed", "someVerdictInvented2027"];
  for (const verdict of verdicts) {
    it(`renders "${verdict}" through the same path, with no special case`, async () => {
      const headline = `the Gateway's own words for ${verdict}`;
      await stopWith(answer({ verdict, headline }));
      expect(await screen.findByText(headline)).toBeTruthy();
    });
  }

  it("stays on the session until the answer is dismissed, then leaves for the roster", async () => {
    await stopWith(answer());

    expect(await screen.findByText(answer().headline)).toBeTruthy();
    // The old hook navigated the instant the call returned, which threw the answer away unread.
    expect(navigateMock).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(navigateMock).toHaveBeenCalledWith("/"));
  });

  it("treats not-on-this-fleet as the success it is", async () => {
    const headline =
      "not on this fleet - nothing in this account carries the id 9c41e7a2, so no machine was asked "
      + "and no machine's processes were searched";
    await stopWith(answer({ verdict: "notOnFleet", headline, processId: null, processEnded: false }));

    expect(await screen.findByText(headline)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(navigateMock).toHaveBeenCalledWith("/"));
  });
});

// THE FAILURE PATH LIVES IN stopFailureAndBusyGuard.test.tsx, not here.
//
// It used to live here, and it could not catch inspection finding I8: it seeded an error on the manage
// STUB before the action and then searched the whole document, so it passed just as happily when the
// only copy of the sentence was on the app bar's banner, outside the modal and underneath its overlay.
// The replacement drives the REAL management hook and queries WITHIN the dialog element.
