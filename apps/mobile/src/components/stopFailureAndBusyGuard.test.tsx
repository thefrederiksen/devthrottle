// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";

// The phone's stop, driven through the REAL management hook (inspection findings I7 and I8).
//
// Both defects live in the seam between the sheet and the hook, so a hand-rolled stand-in for the hook
// cannot see either of them:
//
//   * I7 - Enter can send repeated stops while the controls say Stopping. The Stop button disables while
//     a request is outstanding, but the reason box stays live and Enter is not a button: a disabled
//     attribute does not block a key press. The guard belongs on the ACTION, which is the hook's stop
//     verb, and only the real hook has it.
//   * I8 - a failed stop used to render on the app bar's sibling banner, UNDER the full-screen overlay
//     of a dialog declaring aria-modal="true", so there was no failure text inside the dialog at all -
//     and backing out with Cancel cleared that banner too, deleting the only explanation. The error has
//     to come from the real hook to be the real sentence in the real place.
//
// The old sheet test could not catch I8 because it seeded an error on its stub BEFORE the action and
// then searched the whole document. These assertions query WITHIN the dialog element.
//
// What is NOT covered here: no browser and no pixels. jsdom has no layout and no stacking, so "the
// overlay covers the banner" is not something anything below could observe - what is proven is that the
// sentence is inside the dialog's own subtree, and that backing out does not destroy it.

const navigateMock = vi.fn();
vi.mock("react-router-dom", () => ({
  useNavigate: () => navigateMock,
  useParams: () => ({ sessionId: "9c41e7a2-0000-4000-8000-000000000000" }),
}));

const stopSessionMock = vi.fn();
vi.mock("@devthrottle/client-core/api/client", () => ({
  holdSession: () => Promise.resolve({ onHold: false, pending: false }),
  // The hook polls the shared roster for the held state. An empty fleet is a real answer and nothing
  // here is about the hold surface.
  listSessions: () => Promise.resolve([]),
  stopSession: (...args: unknown[]) => stopSessionMock(...args),
}));

import { SessionAppBar } from "./SessionAppBar";
import { useSessionManage } from "./useSessionManage";

const SID = "9c41e7a2-0000-4000-8000-000000000000";
const HEADLINE = "stopped 9c41e7a2 - process 51884 ended, row removed";
const FAILURE = "the Director on SORENLAPTOP could not be reached";

// The app bar wired to the REAL hook, exactly as Chat, Terminal and Voice mode wire it.
function Harness() {
  const manage = useSessionManage(SID);
  return <SessionAppBar title="throwaway" manage={manage} />;
}

function answer() {
  return {
    verdict: "stopped",
    headline: HEADLINE,
    details: [] as string[],
    sessionId: SID,
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
  };
}

function openStopSheet() {
  fireEvent.click(screen.getByLabelText("Session menu"));
  fireEvent.click(screen.getByRole("menuitem", { name: "Stop session" }));
}

function reasonBox() {
  return screen.getByPlaceholderText("Spawned into the wrong mode") as HTMLInputElement;
}

function stopButton() {
  return screen.getByRole("button", { name: /^Stop$/ }) as HTMLButtonElement;
}

beforeEach(() => {
  stopSessionMock.mockReset();
  navigateMock.mockReset();
});

afterEach(() => {
  cleanup();
});

describe("the phone sends ONE stop, however many times Enter is pressed", () => {
  it("does not send a second stop while the first is still outstanding", async () => {
    let release: (value: unknown) => void = () => {};
    stopSessionMock.mockReturnValue(new Promise((resolve) => { release = resolve; }));

    render(<Harness />);
    openStopSheet();
    fireEvent.change(reasonBox(), { target: { value: "spawned into the wrong mode" } });
    fireEvent.keyDown(reasonBox(), { key: "Enter" });
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));

    // The controls now say Stopping... and the button is disabled - and the reason box is not.
    expect(screen.getByRole("button", { name: "Stopping..." })).toBeTruthy();
    expect(reasonBox().disabled).toBe(false);

    fireEvent.keyDown(reasonBox(), { key: "Enter" });
    fireEvent.keyDown(reasonBox(), { key: "Enter" });

    // Asserted WITHOUT awaiting the second press. A test that awaits it deadlocks under its own
    // mutation - with the guard gone, the second call waits on the same outstanding answer the first is
    // waiting on - and a test that hangs is a test that cannot go red. (Phase B recorded exactly that.)
    expect(stopSessionMock).toHaveBeenCalledTimes(1);

    release(answer());
    expect(await screen.findByText(HEADLINE)).toBeTruthy();
    expect(stopSessionMock).toHaveBeenCalledTimes(1);
  });

  it("sends one stop for two presses landing together, before anything has re-rendered", async () => {
    let release: (value: unknown) => void = () => {};
    stopSessionMock.mockReturnValue(new Promise((resolve) => { release = resolve; }));

    render(<Harness />);
    openStopSheet();
    fireEvent.change(reasonBox(), { target: { value: "spawned into the wrong mode" } });
    fireEvent.keyDown(reasonBox(), { key: "Enter" });
    fireEvent.keyDown(reasonBox(), { key: "Enter" });

    expect(stopSessionMock).toHaveBeenCalledTimes(1);
    release(answer());
    await screen.findByText(HEADLINE);
  });

  it("does not send a second stop when the button is clicked while one is outstanding", async () => {
    let release: (value: unknown) => void = () => {};
    stopSessionMock.mockReturnValue(new Promise((resolve) => { release = resolve; }));

    render(<Harness />);
    openStopSheet();
    fireEvent.change(reasonBox(), { target: { value: "spawned into the wrong mode" } });
    fireEvent.click(stopButton());
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));

    fireEvent.click(screen.getByRole("button", { name: "Stopping..." }));
    expect(stopSessionMock).toHaveBeenCalledTimes(1);

    release(answer());
    await screen.findByText(HEADLINE);
  });
});

describe("a failed stop is explained INSIDE the sheet the operator is looking at", () => {
  async function failTheStop() {
    stopSessionMock.mockRejectedValue(new Error(FAILURE));
    render(<Harness />);
    openStopSheet();
    fireEvent.change(reasonBox(), { target: { value: "doing the wrong work" } });
    fireEvent.click(stopButton());
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));
  }

  it("puts the Gateway's own sentence inside the dialog, not on a banner underneath it", async () => {
    await failTheStop();

    const dialog = await screen.findByRole("dialog");
    // The whole finding: this query is scoped to the dialog. Searching the document found the sentence
    // on the app bar's banner - outside the modal, under its overlay - and called that a pass.
    expect(within(dialog).getByText(FAILURE)).toBeTruthy();
    // And it is said ONCE. Two copies of one event, one of them unreadable, is what I8 described.
    expect(screen.getAllByText(FAILURE).length).toBe(1);
  });

  it("keeps the sheet open with the typed reason, and retries with it", async () => {
    await failTheStop();

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(FAILURE)).toBeTruthy();
    expect(reasonBox().value).toBe("doing the wrong work");
    expect(navigateMock).not.toHaveBeenCalled();

    stopSessionMock.mockResolvedValue(answer());
    fireEvent.click(stopButton());
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(2));
    expect(stopSessionMock.mock.calls[1][1]).toBe("doing the wrong work");
  });

  it("does not delete the explanation when the operator backs out to the screen behind", async () => {
    await failTheStop();
    await screen.findByRole("dialog");

    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    // Cancel used to clear the error as well, so backing out to read the explanation destroyed it.
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByText(FAILURE)).toBeTruthy();
    expect(navigateMock).not.toHaveBeenCalled();
  });

  it("starts the next question with a clean slate - the old failure is not left hanging over it", async () => {
    await failTheStop();
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.getByText(FAILURE)).toBeTruthy();

    // Opening the sheet again is a fresh question about a session that is still here. There is nothing
    // to explain yet, so the previous failure goes.
    openStopSheet();
    expect(screen.queryByText(FAILURE)).toBeNull();
  });
});
