// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";

// Rendered proof of the Cockpit's stop control (mission "Stop a session", Rulings 4 and 5).
//
// What is actually being held down here:
//   * the dialog asks for the reason BEFORE it acts, and will not offer a click the Gateway could only
//     refuse (empty, or whitespace only);
//   * the answer is RENDERED FROM the Gateway's headline and details - the headlines below are sentences
//     no client could have invented, so a view that composed its own words could not pass;
//   * every verdict word renders the same way, including a fifth this client has never heard of;
//   * the dialog does not close on success, and the page is told it may navigate away only once the
//     answer has been dismissed;
//   * a refusal shows the Gateway's own sentence and keeps the typed reason.
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

// A Gateway answer. The headline is deliberately a sentence no client would ever compose.
function answer(over: Record<string, unknown> = {}) {
  return {
    verdict: "stopped",
    headline: "stopped 9c41e7a2 - process 51884 ended, row removed",
    details: [] as string[],
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

// Open the menu and choose Stop session, which is where every test below starts.
function openStopDialog() {
  fireEvent.click(screen.getByLabelText("Session menu"));
  fireEvent.click(screen.getByRole("menuitem", { name: "Stop session" }));
}

function reasonBox() {
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

describe("the Cockpit stop dialog asks for the reason before it acts", () => {
  it("offers Stop session, not Close session", () => {
    render(<SessionMenu session={session()} />);
    fireEvent.click(screen.getByLabelText("Session menu"));
    expect(screen.getByRole("menuitem", { name: "Stop session" })).toBeTruthy();
    expect(screen.queryByRole("menuitem", { name: "Close session" })).toBeNull();
  });

  it("says a reason is required and what it is for", () => {
    render(<SessionMenu session={session()} />);
    openStopDialog();
    expect(screen.getByText(/A reason is required/)).toBeTruthy();
    expect(screen.getByText(/recorded with the stop/)).toBeTruthy();
  });

  it("will not submit an EMPTY reason", () => {
    render(<SessionMenu session={session()} />);
    openStopDialog();
    expect((stopButton() as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(stopButton());
    expect(stopSessionMock).not.toHaveBeenCalled();
  });

  it("will not submit a WHITESPACE-ONLY reason", () => {
    render(<SessionMenu session={session()} />);
    openStopDialog();
    fireEvent.change(reasonBox(), { target: { value: "   \t  " } });
    expect((stopButton() as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(stopButton());
    expect(stopSessionMock).not.toHaveBeenCalled();
  });

  it("enables the control once there is a reason, and sends that reason", async () => {
    stopSessionMock.mockResolvedValue(answer());
    render(<SessionMenu session={session()} />);
    openStopDialog();
    fireEvent.change(reasonBox(), { target: { value: "spawned into the wrong mode" } });
    expect((stopButton() as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(stopButton());

    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));
    expect(stopSessionMock).toHaveBeenCalledWith(
      "9c41e7a2-0000-4000-8000-000000000000",
      "spawned into the wrong mode",
    );
  });

  // Enter is the path the disabled button cannot cover: a disabled control never fires a click, so the
  // "it sends nothing" rule on an empty box is only really exercised through the keyboard.
  it("sends the stop when Enter is pressed with a reason in the box", async () => {
    stopSessionMock.mockResolvedValue(answer());
    render(<SessionMenu session={session()} />);
    openStopDialog();
    fireEvent.change(reasonBox(), { target: { value: "spawned into the wrong mode" } });
    fireEvent.keyDown(reasonBox(), { key: "Enter" });

    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledWith(
      "9c41e7a2-0000-4000-8000-000000000000",
      "spawned into the wrong mode",
    ));
  });

  it("sends nothing when Enter is pressed with an empty box", () => {
    render(<SessionMenu session={session()} />);
    openStopDialog();
    fireEvent.keyDown(reasonBox(), { key: "Enter" });
    expect(stopSessionMock).not.toHaveBeenCalled();
  });

  it("sends nothing when Enter is pressed with only whitespace", () => {
    render(<SessionMenu session={session()} />);
    openStopDialog();
    fireEvent.change(reasonBox(), { target: { value: "   " } });
    fireEvent.keyDown(reasonBox(), { key: "Enter" });
    expect(stopSessionMock).not.toHaveBeenCalled();
  });

  it("trims the reason it sends, so leading and trailing space is not recorded as the reason", async () => {
    stopSessionMock.mockResolvedValue(answer());
    render(<SessionMenu session={session()} />);
    openStopDialog();
    fireEvent.change(reasonBox(), { target: { value: "  doing the wrong work  " } });
    fireEvent.click(stopButton());

    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(1));
    expect(stopSessionMock.mock.calls[0][1]).toBe("doing the wrong work");
  });
});

describe("the Cockpit stop dialog renders the Gateway's answer, verbatim", () => {
  async function stopWith(outcome: Record<string, unknown>, onClosed?: () => void) {
    stopSessionMock.mockResolvedValue(outcome);
    render(<SessionMenu session={session()} onClosed={onClosed} />);
    openStopDialog();
    fireEvent.change(reasonBox(), { target: { value: "spawned into the wrong mode" } });
    fireEvent.click(stopButton());
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalled());
  }

  it("shows a headline no client could have invented", async () => {
    // If this view composed its own sentence, this string could not appear on screen.
    const headline = "the Gateway wrote this exact sentence and the Cockpit did not";
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
      // No failure styling and no error: every one of these is a success.
      expect(document.querySelector(".session-dialog-error")).toBeNull();
    });
  }

  it("keeps the dialog open on success and tells the page nothing until it is dismissed", async () => {
    const onClosed = vi.fn();
    await stopWith(answer(), onClosed);

    // The answer is on screen and the page has NOT been told to navigate away - this is the whole defect.
    expect(await screen.findByText(answer().headline)).toBeTruthy();
    expect(onClosed).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(onClosed).toHaveBeenCalledTimes(1));
    expect(screen.queryByText(answer().headline)).toBeNull();
  });

  it("treats not-on-this-fleet as the success it is - the answer, no error, and the page still leaves", async () => {
    const onClosed = vi.fn();
    const headline =
      "not on this fleet - nothing in this account carries the id 9c41e7a2, so no machine was asked "
      + "and no machine's processes were searched";
    await stopWith(answer({ verdict: "notOnFleet", headline, processId: null, processEnded: false }), onClosed);

    expect(await screen.findByText(headline)).toBeTruthy();
    expect(document.querySelector(".session-dialog-error")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(onClosed).toHaveBeenCalledTimes(1));
  });
});

describe("the Cockpit stop dialog shows a failure and keeps what was typed", () => {
  it("shows the Gateway's own refusal sentence for a stop it would not carry", async () => {
    // A 400 the Gateway would only send if the reason were blank; forced here so the dialog's failure
    // path is exercised with the real sentence rather than a status number.
    const refusal =
      "A stop needs a reason. Say why this session is being stopped - it is recorded with the stop, and "
      + "it is how anyone reading the trail later knows what happened.";
    stopSessionMock.mockRejectedValue(new GatewayError(400, refusal, { reason: refusal }));

    render(<SessionMenu session={session()} />);
    openStopDialog();
    fireEvent.change(reasonBox(), { target: { value: "a reason" } });
    fireEvent.click(stopButton());

    expect(await screen.findByText(refusal)).toBeTruthy();
  });

  it("keeps the dialog open and the reason in the box after an ordinary failure", async () => {
    const onClosed = vi.fn();
    stopSessionMock.mockRejectedValue(
      new GatewayError(502, "the Director on SORENLAPTOP could not be reached", {
        reason: "the Director on SORENLAPTOP could not be reached",
        retryable: true,
      }),
    );

    render(<SessionMenu session={session()} onClosed={onClosed} />);
    openStopDialog();
    fireEvent.change(reasonBox(), { target: { value: "doing the wrong work" } });
    fireEvent.click(stopButton());

    expect(await screen.findByText(/could not be reached/)).toBeTruthy();
    // The reason survives, so a retry does not start by making the user write their sentence again.
    expect((reasonBox() as HTMLInputElement).value).toBe("doing the wrong work");
    // And the page is not told the session ended, because it did not.
    expect(onClosed).not.toHaveBeenCalled();

    stopSessionMock.mockResolvedValue(answer());
    fireEvent.click(stopButton());
    await waitFor(() => expect(stopSessionMock).toHaveBeenCalledTimes(2));
    expect(stopSessionMock.mock.calls[1][1]).toBe("doing the wrong work");
  });
});
