// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor, act } from "@testing-library/react";

// DOES THE PHONE SHARE THE COCKPIT'S FINDING I4? It does not, and this is the test that says so rather
// than a paragraph asserting it.
//
// The Cockpit's stop answer was destroyed by the roster refresh the stop itself caused, because the
// dialog was owned by the roster ROW. The phone's stop control is not on a row: there is exactly one
// entry point to a stop on this shell (SessionAppBar's overflow menu), it is mounted by the per-session
// route, and nothing unmounts it when the session leaves the roster. The hook polls the roster every
// four seconds for the held state and, finding no matching session, keeps its last-known values and
// touches nothing else.
//
// That is an argument from reading the code, and this file turns it into a control: stop the session,
// then let the real poll run several times against a fleet that no longer contains it, and require the
// Gateway's answer still to be on screen with its dismiss button. A future change that hangs the phone's
// session screen off the roster reddens this.

const navigateMock = vi.fn();
vi.mock("react-router-dom", () => ({
  useNavigate: () => navigateMock,
  useParams: () => ({ sessionId: "9c41e7a2-0000-4000-8000-000000000000" }),
}));

const stopSessionMock = vi.fn();
// The roster read the hook polls. It answers with a fleet that does NOT contain this session - which is
// what the Gateway returns once the stop has removed the row.
const listSessionsMock = vi.fn(async () => [] as unknown[]);
vi.mock("@devthrottle/client-core/api/client", () => ({
  holdSession: () => Promise.resolve({ onHold: false, pending: false }),
  listSessions: () => listSessionsMock(),
  stopSession: (...args: unknown[]) => stopSessionMock(...args),
}));

import { SessionAppBar } from "./SessionAppBar";
import { useSessionManage } from "./useSessionManage";

const SID = "9c41e7a2-0000-4000-8000-000000000000";
const HEADLINE = "stopped 9c41e7a2 - process 51884 ended, row removed";

function Harness() {
  const manage = useSessionManage(SID);
  return <SessionAppBar title="throwaway" manage={manage} />;
}

beforeEach(() => {
  stopSessionMock.mockReset();
  navigateMock.mockReset();
  listSessionsMock.mockClear();
  // shouldAdvanceTime keeps the testing library's own waiting working while the poll interval is under
  // our control.
  vi.useFakeTimers({ shouldAdvanceTime: true });
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
});

describe("the phone's stop answer is not owned by the roster", () => {
  it("keeps the Gateway's answer through repeated roster polls that no longer list the session", async () => {
    stopSessionMock.mockResolvedValue({
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
      stoppedBy: "device 4f10",
      killed: true,
      removed: true,
    });

    render(<Harness />);
    fireEvent.click(screen.getByLabelText("Session menu"));
    fireEvent.click(screen.getByRole("menuitem", { name: "Stop session" }));
    fireEvent.change(screen.getByPlaceholderText("Spawned into the wrong mode"), {
      target: { value: "spawned into the wrong mode" },
    });
    fireEvent.click(screen.getByRole("button", { name: /^Stop$/ }));

    expect(await screen.findByText(HEADLINE)).toBeTruthy();
    const pollsBefore = listSessionsMock.mock.calls.length;

    // Three poll intervals with the session absent from the fleet - far longer than the two seconds
    // that emptied the Cockpit's dialog.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(13_000);
    });

    expect(listSessionsMock.mock.calls.length).toBeGreaterThan(pollsBefore);
    expect(screen.getByText(HEADLINE)).toBeTruthy();
    expect(screen.getByText("reason: spawned into the wrong mode")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Done" })).toBeTruthy();
    // And it still has not navigated: leaving is the dismissal's job, not the roster's.
    expect(navigateMock).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(navigateMock).toHaveBeenCalledWith("/"));
  });
});
