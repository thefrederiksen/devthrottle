// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// Rendered proof of the Cockpit's stop control, after issue internal#1992 turned it from an interrogation
// back into a confirmation.
//
// What is actually being held down here:
//   * the NOTE IS OPTIONAL - the button is live from the moment the dialog opens, and a stop with an empty
//     box still reaches the Gateway;
//   * the audit row is never blank - an empty box sends the derived reason naming this surface, and a
//     written note is sent as written and trimmed;
//   * a SUCCESS IS SILENT - the dialog closes, nothing of the Gateway's answer is rendered under any
//     verdict word including one this client has never heard of, and the page is told it may leave;
//   * a FAILURE SPEAKS - the Gateway's own sentence, the dialog still open, the note still in the box, and
//     the page NOT told to navigate away;
//   * one stop is sent however many times Enter is pressed.
//
// Only the three Gateway calls are replaced; GatewayError and gatewayErrorMessage are the real ones, so
// the refusal sentence is carried by the code that actually carries it in the product.
const stopSessionMock = vi.fn();
vi.mock("@devthrottle/client-core/api/client", async () => {
  const actual = await vi.importActual<Record<string, unknown>>("@devthrottle/client-core/api/client");
  return {
    ...actual,
    stopSession: (...args: unknown[]) => stopSessionMock(...args),
    holdSession: () => Promise.resolve({ onHold: false, pending: false }),
    getHandover: () => Promise.resolve(null),
  };
});

// The lengths cache would otherwise reach for the Gateway on mount. Null is a real state (the browser
// does not know the user's lengths yet) and the menu is fully usable in it.
vi.mock("@devthrottle/client-core/settings/snoozeOptions", () => ({
  useSnoozeOptions: () => null,
}));

// The client-error channel posts with a raw keepalive fetch. Nothing here is about that channel, and the
// message it returns is the one the dialog shows - so it is the REAL sentence builder, unmocked.
vi.mock("@devthrottle/client-core/errors/reportClientError", async () => {
  const actual = await vi.importActual<Record<string, unknown>>(
    "@devthrottle/client-core/errors/reportClientError",
  );
  const { gatewayErrorMessage } = await vi.importActual<{
    gatewayErrorMessage: (err: unknown, what?: string) => string;
  }>("@devthrottle/client-core/api/client");
  return {
    ...actual,
    reportClientError: () => {},
    describeAndReport: (_surface: string, action: string, err: unknown) => gatewayErrorMessage(err, action),
  };
});

import { GatewayError } from "@devthrottle/client-core/api/client";
import { SessionMenu } from "./SessionMenu";
import { StopSessionProvider, STOP_REASON_FROM_THE_COCKPIT, stopReasonToRecord } from "./StopSessionProvider";

function session(): SessionDto {
  return {
    sessionId: "9c41e7a2-0000-4000-8000-000000000000",
    directorId: "d1",
    machineName: "SORENLAPTOP",
    repoPath: "D:/Repos/scratch",
    agent: "ClaudeCode",
    activityState: "Waiting",
    createdAt: "2026-09-09T10:00:00Z",
    sortOrder: 0,
    name: "throwaway",
  } as unknown as SessionDto;
}

// A Gateway answer. The headline is deliberately a sentence no client would ever compose - so a dialog
// that rendered any of it would be caught by the assertions that it is NOT on screen.
function answer(over: Record<string, unknown> = {}) {
  return {
    verdict: "stopped",
    headline: "stopped 9c41e7a2 - process 51884 ended, row removed",
    details: ["reason: spawned into the wrong mode"] as string[],
    sessionId: "9c41e7a2-0000-4000-8000-000000000000",
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

// The menu is always mounted inside the provider, because that is how it is mounted in the product: the
// stop dialog belongs to StopSessionProvider (AppShell), not to this menu, so that removing the row the
// menu sits in cannot take the outstanding request with it (finding I4).
// stopAnswerOutlivesTheRow.test.tsx is the test that drives that removal through the real parents; this
// file is about what the dialog SAYS and what it SENDS.
function renderMenu(onClosed?: () => void) {
  return render(
    <StopSessionProvider>
      <SessionMenu session={session()} onClosed={onClosed} />
    </StopSessionProvider>,
  );
}

// Open the menu and choose Stop session, which is where every test below starts.
function openStopDialog() {
  fireEvent.click(screen.getByLabelText("Session menu"));
  fireEvent.click(screen.getByRole("menuitem", { name: "Stop session" }));
}

function noteBox() {
  return screen.getByPlaceholderText("Spawned into the wrong mode");
}

function stopButton() {
  return screen.getByRole("button", { name: /^Stop session$/ });
}

beforeEach(() => {
  stopSessionMock.mockReset();
});

afterEach(() => {
  cleanup();
});

describe("the Cockpit stop dialog asks one question and does not demand a reason", () => {
  it("offers Stop session, not Close session", () => {
    renderMenu();
    fireEvent.click(screen.getByLabelText("Session menu"));
    expect(screen.getByRole("menuitem", { name: "Stop session" })).toBeTruthy();
    expect(screen.queryByRole("menuitem", { name: "Close session" })).toBeNull();
  });

  it("asks about THIS session by name, in one question", () => {
    renderMenu();
    openStopDialog();
    expect(screen.getByRole("heading", { name: "Stop throwaway?" })).toBeTruthy();
  });

  // The defect, stated as an absence: the paragraph about the audit trail and the demand for a reason are
  // both gone, and the note says it is optional.
  it("says the note is optional and no longer lectures about the audit trail", () => {
    renderMenu();
    openStopDialog();
    expect(screen.getByText("Note (optional)")).toBeTruthy();
    expect(screen.queryByText(/A reason is required/)).toBeNull();
    expect(screen.queryByText(/recorded with the stop/)).toBeNull();
    expect(screen.queryByText(/Why are you stopping it/)).toBeNull();
  });

  it("offers the control immediately, with nothing typed", () => {
    renderMenu();
    openStopDialog();
    expect((stopButton() as HTMLButtonElement).disabled).toBe(false);
  });

  // THE DECISIVE ONE. A test on the button's state alone would still pass if the click path refused a
  // blank note.
  it("SENDS the stop with an empty note, recording the reason that names this surface", async () => {
    stopSessionMock.mockResolvedValue(answer());
    renderMenu();
    openStopDialog();
    fireEvent.click(stopButton());

    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));
    expect(stopSessionMock).toHaveBeenCalledWith(
      "9c41e7a2-0000-4000-8000-000000000000",
      STOP_REASON_FROM_THE_COCKPIT,
    );
  });

  it("falls through to that same reason when the box holds only whitespace", async () => {
    stopSessionMock.mockResolvedValue(answer());
    renderMenu();
    openStopDialog();
    fireEvent.change(noteBox(), { target: { value: "   \t  " } });
    fireEvent.click(stopButton());

    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));
    expect(stopSessionMock.mock.calls[0][1]).toBe(STOP_REASON_FROM_THE_COCKPIT);
  });

  it("sends a written note as written, and the derived reason does not overwrite it", async () => {
    stopSessionMock.mockResolvedValue(answer());
    renderMenu();
    openStopDialog();
    fireEvent.change(noteBox(), { target: { value: "  doing the wrong work  " } });
    fireEvent.click(stopButton());

    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));
    expect(stopSessionMock.mock.calls[0][1]).toBe("doing the wrong work");
  });

  it("decides the recorded reason the same way whatever calls it", () => {
    expect(stopReasonToRecord("")).toBe(STOP_REASON_FROM_THE_COCKPIT);
    expect(stopReasonToRecord("   ")).toBe(STOP_REASON_FROM_THE_COCKPIT);
    expect(stopReasonToRecord("  it was wedged  ")).toBe("it was wedged");
  });

  it("sends the stop when Enter is pressed, note or no note", async () => {
    stopSessionMock.mockResolvedValue(answer());
    renderMenu();
    openStopDialog();
    fireEvent.keyDown(noteBox(), { key: "Enter" });

    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledWith(
      "9c41e7a2-0000-4000-8000-000000000000",
      STOP_REASON_FROM_THE_COCKPIT,
    ));
  });
});

describe("the Cockpit stop dialog sends ONE stop, however many times Enter is pressed", () => {
  // Inspection finding I7, and it survives the simplification. The Stop button disables while a request
  // is outstanding, but the note box stays live and Enter is not a button - a disabled attribute does not
  // block a key press. Two presses sent two stops and two rows in the audit trail for one intention. The
  // guard is on the ACTION.
  //
  // None of these tests AWAITS the second press. A test that does deadlocks under its own mutation - with
  // the guard gone, the second call waits on the same outstanding answer the first is waiting on - and a
  // test that hangs is a test that cannot go red.
  function pendingStop(): (value: unknown) => void {
    let release: (value: unknown) => void = () => {};
    stopSessionMock.mockReturnValue(new Promise((resolve) => { release = resolve; }));
    return (value: unknown) => release(value);
  }

  it("refuses a second Enter while the first stop is still outstanding", async () => {
    const release = pendingStop();
    renderMenu();
    openStopDialog();
    fireEvent.change(noteBox(), { target: { value: "spawned into the wrong mode" } });
    fireEvent.keyDown(noteBox(), { key: "Enter" });
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));

    // The controls say Stopping... and the button is disabled - and the note box is not.
    expect((screen.getByRole("button", { name: "Stopping..." }) as HTMLButtonElement).disabled).toBe(true);
    expect((noteBox() as HTMLInputElement).disabled).toBe(false);

    fireEvent.keyDown(noteBox(), { key: "Enter" });
    fireEvent.keyDown(noteBox(), { key: "Enter" });
    expect(stopSessionMock).toHaveBeenCalledTimes(1);

    release(answer());
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(stopSessionMock).toHaveBeenCalledTimes(1);
  });

  it("sends one stop for two presses landing together", async () => {
    const release = pendingStop();
    renderMenu();
    openStopDialog();
    fireEvent.keyDown(noteBox(), { key: "Enter" });
    fireEvent.keyDown(noteBox(), { key: "Enter" });

    expect(stopSessionMock).toHaveBeenCalledTimes(1);
    release(answer());
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("offers no way out of the dialog while the stop is outstanding", async () => {
    const release = pendingStop();
    renderMenu();
    openStopDialog();
    fireEvent.click(stopButton());
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));

    expect((screen.getByRole("button", { name: "Cancel" }) as HTMLButtonElement).disabled).toBe(true);
    // The backdrop is the other way out, and it is refused too while a request is in flight - a dialog
    // that vanished mid-flight would leave a failure with nowhere to land.
    const overlay = document.querySelector(".session-dialog-overlay");
    if (overlay === null) throw new Error("the stop dialog rendered no overlay");
    fireEvent.mouseDown(overlay);
    fireEvent.click(overlay);
    expect(screen.getByRole("dialog")).toBeTruthy();

    release(answer());
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

describe("a successful stop is silent", () => {
  async function stopWith(outcome: Record<string, unknown>, onClosed?: () => void) {
    stopSessionMock.mockResolvedValue(outcome);
    renderMenu(onClosed);
    openStopDialog();
    fireEvent.click(stopButton());
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalled());
  }

  it("closes the dialog and renders NOTHING of the Gateway's answer", async () => {
    const headline = "the Gateway wrote this exact sentence and the Cockpit did not";
    await stopWith(answer({ headline, details: ["and a detail line with it"] }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(screen.queryByText(headline)).toBeNull();
    expect(screen.queryByText("and a detail line with it")).toBeNull();
    expect(screen.queryByRole("button", { name: "Done" })).toBeNull();
  });

  it("tells the page it may navigate away", async () => {
    const onClosed = vi.fn();
    await stopWith(answer(), onClosed);
    await waitFor(() => expect(onClosed).toHaveBeenCalledTimes(1));
  });

  // Four verdict words today, and a fifth that does not exist - the view must not know how many there
  // are, and must not treat any of them differently (CLAUDE.md rule 7).
  const verdicts = ["stopped", "alreadyStopped", "notOnFleet", "stoppedNotDescribed", "someVerdictInvented2027"];
  for (const verdict of verdicts) {
    it(`treats "${verdict}" exactly like every other verdict`, async () => {
      const onClosed = vi.fn();
      await stopWith(answer({ verdict, headline: `the Gateway's own words for ${verdict}` }), onClosed);

      await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
      expect(screen.queryByText(`the Gateway's own words for ${verdict}`)).toBeNull();
      expect(document.querySelector(".session-dialog-error")).toBeNull();
      expect(onClosed).toHaveBeenCalledTimes(1);
    });
  }
});

describe("only a failure speaks", () => {
  it("shows the Gateway's own refusal sentence for a stop it would not carry", async () => {
    const refusal =
      "A stop needs a reason. Say why this session is being stopped - it is recorded with the stop, and "
      + "it is how anyone reading the trail later knows what happened.";
    stopSessionMock.mockRejectedValue(new GatewayError(400, refusal, { reason: refusal }));

    renderMenu();
    openStopDialog();
    fireEvent.click(stopButton());

    expect(await screen.findByText(refusal)).toBeTruthy();
  });

  it("keeps the dialog open and the note in the box after an ordinary failure", async () => {
    const onClosed = vi.fn();
    stopSessionMock.mockRejectedValue(
      new GatewayError(502, "the Director on SORENLAPTOP could not be reached", {
        reason: "the Director on SORENLAPTOP could not be reached",
        retryable: true,
      }),
    );

    renderMenu(onClosed);
    openStopDialog();
    fireEvent.change(noteBox(), { target: { value: "doing the wrong work" } });
    fireEvent.click(stopButton());

    expect(await screen.findByText(/could not be reached/)).toBeTruthy();
    // The note survives, so a retry does not start by making the user write their sentence again.
    expect((noteBox() as HTMLInputElement).value).toBe("doing the wrong work");
    // And the page is NOT told the session ended, because it did not - it may well still be running.
    expect(onClosed).not.toHaveBeenCalled();

    stopSessionMock.mockResolvedValue(answer());
    fireEvent.click(stopButton());
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(2));
    expect(stopSessionMock.mock.calls[1][1]).toBe("doing the wrong work");
    await waitFor(() => expect(onClosed).toHaveBeenCalledTimes(1));
  });

  it("clears a stale failure when the stop is retried", async () => {
    stopSessionMock.mockRejectedValueOnce(
      new GatewayError(502, "the Director on SORENLAPTOP could not be reached", {
        reason: "the Director on SORENLAPTOP could not be reached",
      }),
    );
    let release: (value: unknown) => void = () => {};
    stopSessionMock.mockReturnValueOnce(new Promise((resolve) => { release = resolve; }));

    renderMenu();
    openStopDialog();
    fireEvent.click(stopButton());
    expect(await screen.findByText(/could not be reached/)).toBeTruthy();

    fireEvent.click(stopButton());
    // A window showing an old error beside an outstanding request describes two states at once.
    await waitFor(() => expect(screen.queryByText(/could not be reached/)).toBeNull());
    release(answer());
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});
