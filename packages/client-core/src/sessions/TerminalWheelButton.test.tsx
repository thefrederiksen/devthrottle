// @vitest-environment jsdom
// The Terminal tab's wheel button (mission #4070, phase 4), shared by the Cockpit and the phone. Pinned: it shows
// exactly the Gateway's verdict (or nothing), goes busy in the same render as the press, calls the right verb,
// shows a refusal's own sentence verbatim, and never writes a reply onto a session the screen has left.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";

const calls = vi.hoisted(() => ({ take: vi.fn(), handBack: vi.fn(), reported: vi.fn() }));

vi.mock("../api/client", async (importOriginal) => {
  const real = await importOriginal<typeof import("../api/client")>();
  return { ...real, takeWheel: calls.take, handBackWheel: calls.handBack };
});
vi.mock("../errors/reportClientError", () => ({
  reportShownError: (_surface: string, _action: string, message: string) => {
    calls.reported(message);
    return message;
  },
}));

import { GatewayError, type SessionDto } from "../api/client";
import { TerminalWheelButton } from "./TerminalWheelButton";

function sessionWith(terminalWheel: unknown, sessionId = "s-1"): SessionDto {
  return { sessionId, apiMode: { kind: "idle", terminalWheel } } as unknown as SessionDto;
}

const TAKE = { kind: "take", label: "Take the wheel", enabled: true, disabledReason: null };
const HAND_BACK = { kind: "handBack", label: "Hand back", enabled: true, disabledReason: null };

/** A promise the test resolves or rejects by hand, so nothing waits on a clock. */
function deferred(): { promise: Promise<void>; resolve: () => void; reject: (e: unknown) => void } {
  let resolve!: () => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<void>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

describe("TerminalWheelButton", () => {
  beforeEach(() => {
    calls.take.mockReset();
    calls.handBack.mockReset();
    calls.reported.mockReset();
  });
  afterEach(cleanup);

  it("TerminalWheelButton_NoVerdict_RendersNothing", () => {
    const { container } = render(<TerminalWheelButton session={{ sessionId: "s-1" } as SessionDto} />);
    expect(container.innerHTML).toBe("");
  });

  it("TerminalWheelButton_VerdictSaysNone_RendersNothingEvenForAnApiModeRow", () => {
    const row = { ...sessionWith({ kind: "none" }), connectionMode: "api", wheelHolder: "protocol" } as unknown as SessionDto;
    const { container } = render(<TerminalWheelButton session={row} />);
    expect(container.innerHTML).toBe("");
  });

  it("TerminalWheelButton_NoSession_RendersNothing", () => {
    const { container } = render(<TerminalWheelButton session={null} />);
    expect(container.innerHTML).toBe("");
  });

  it("TerminalWheelButton_Take_ShowsTheGatewaysLabel", () => {
    render(<TerminalWheelButton session={sessionWith(TAKE)} />);
    const button = screen.getByRole("button", { name: "Take the wheel" });
    expect(button).toHaveProperty("disabled", false);
  });

  it("TerminalWheelButton_HandBack_ShowsTheGatewaysLabel", () => {
    render(<TerminalWheelButton session={sessionWith(HAND_BACK)} />);
    expect(screen.getByRole("button", { name: "Hand back" })).toBeTruthy();
  });

  it("TerminalWheelButton_NotEnabled_IsDisabledAndShowsTheGatewaysReason", () => {
    const reason = "The terminal is in the middle of a turn. Hand back when it has finished.";
    render(<TerminalWheelButton session={sessionWith({ ...HAND_BACK, enabled: false, disabledReason: reason })} />);
    const button = screen.getByRole("button", { name: "Hand back" });
    expect(button).toHaveProperty("disabled", true);
    expect(screen.getByText(reason)).toBeTruthy();
    expect(button.getAttribute("aria-describedby")).toBe(screen.getByText(reason).id);
    fireEvent.click(button);
    expect(calls.handBack).not.toHaveBeenCalled();
  });

  it("TerminalWheelButton_Press_GoesBusyAtOnceAndCallsTake", async () => {
    const reply = deferred();
    calls.take.mockReturnValue(reply.promise);
    render(<TerminalWheelButton session={sessionWith(TAKE)} />);
    const button = screen.getByRole("button", { name: "Take the wheel" });

    fireEvent.click(button);

    // Busy in the render that followed the press - before the Gateway answered.
    expect(button.getAttribute("aria-busy")).toBe("true");
    expect(button).toHaveProperty("disabled", true);
    expect(calls.take).toHaveBeenCalledWith("s-1");
    await act(async () => { reply.resolve(); await reply.promise; });
    expect(button.getAttribute("aria-busy")).toBe("false");
  });

  it("TerminalWheelButton_PressHandBack_CallsHandBack", async () => {
    calls.handBack.mockResolvedValue(undefined);
    render(<TerminalWheelButton session={sessionWith(HAND_BACK)} />);
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Hand back" })); });
    expect(calls.handBack).toHaveBeenCalledWith("s-1");
    expect(calls.take).not.toHaveBeenCalled();
  });

  it("TerminalWheelButton_Accepted_TellsTheShellToReRead", async () => {
    calls.take.mockResolvedValue(undefined);
    const onChanged = vi.fn();
    render(<TerminalWheelButton session={sessionWith(TAKE)} onChanged={onChanged} />);
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Take the wheel" })); });
    expect(onChanged).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("TerminalWheelButton_DoublePress_CallsTheVerbOnce", async () => {
    const reply = deferred();
    calls.take.mockReturnValue(reply.promise);
    render(<TerminalWheelButton session={sessionWith(TAKE)} />);
    const button = screen.getByRole("button", { name: "Take the wheel" });
    fireEvent.click(button);
    fireEvent.click(button);
    expect(calls.take).toHaveBeenCalledTimes(1);
    await act(async () => { reply.resolve(); await reply.promise; });
  });

  it("TerminalWheelButton_Refused_ShowsTheGatewaysSentenceVerbatim", async () => {
    const reason = "You already have the wheel of this session: its terminal is open on the conversation. Hand it back first if you want the session to continue in API mode.";
    calls.take.mockRejectedValue(new GatewayError(409, reason, { reason, code: "wheel_already_held" }));
    const onChanged = vi.fn();
    render(<TerminalWheelButton session={sessionWith(TAKE)} onChanged={onChanged} />);
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Take the wheel" })); });
    expect(screen.getByRole("alert").textContent).toBe(reason);
    expect(calls.reported).toHaveBeenCalledWith(reason);
    expect(onChanged).not.toHaveBeenCalled();
    // The button can be pressed again.
    expect(screen.getByRole("button", { name: "Take the wheel" })).toHaveProperty("disabled", false);
  });

  it("TerminalWheelButton_RetryableRefusal_ShowsTheSentenceWithoutAddedWords", async () => {
    const reason = "The machine running this session reported it in API mode but did not say who holds the wheel, so nothing was sent.";
    calls.take.mockRejectedValue(new GatewayError(502, reason, { reason, code: "wheel_state_unreadable" }));
    render(<TerminalWheelButton session={sessionWith(TAKE)} />);
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Take the wheel" })); });
    expect(screen.getByRole("alert").textContent).toBe(reason);
  });

  it("TerminalWheelButton_FailureWithNoSentence_ShowsTheSharedWording", async () => {
    calls.take.mockRejectedValue(new GatewayError(500, "Could not take the wheel (error 500)."));
    render(<TerminalWheelButton session={sessionWith(TAKE)} />);
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Take the wheel" })); });
    const shown = screen.getByRole("alert").textContent ?? "";
    expect(shown).toMatch(/take the wheel/);
    expect(shown).not.toMatch(/^\d+$/);
  });

  it("TerminalWheelButton_VerdictChanges_ClearsTheOldRefusal", async () => {
    const reason = "You do not have the wheel of this session, so there is nothing to hand back.";
    calls.handBack.mockRejectedValue(new GatewayError(409, reason, { reason }));
    const { rerender } = render(<TerminalWheelButton session={sessionWith(HAND_BACK)} />);
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Hand back" })); });
    expect(screen.getByRole("alert").textContent).toBe(reason);

    rerender(<TerminalWheelButton session={sessionWith(TAKE)} />);
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByRole("button", { name: "Take the wheel" })).toBeTruthy();
  });

  it("TerminalWheelButton_ReplyAfterTheScreenMovedSession_IsNotShownOnTheNewSession", async () => {
    const reply = deferred();
    calls.take.mockReturnValue(reply.promise);
    const onChanged = vi.fn();
    const { rerender } = render(<TerminalWheelButton session={sessionWith(TAKE, "s-1")} onChanged={onChanged} />);
    fireEvent.click(screen.getByRole("button", { name: "Take the wheel" }));

    rerender(<TerminalWheelButton session={sessionWith(TAKE, "s-2")} onChanged={onChanged} />);
    await act(async () => { reply.reject(new GatewayError(409, "refused", { reason: "refused" })); await reply.promise.catch(() => {}); });

    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByRole("button", { name: "Take the wheel" }).getAttribute("aria-busy")).toBe("false");
    expect(onChanged).not.toHaveBeenCalled();
  });

  it("TerminalWheelButton_UnreadableVerdict_ShowsTheProblemAndNoButton", () => {
    render(<TerminalWheelButton session={sessionWith({ kind: "grab", label: "Grab", enabled: true })} />);
    expect(screen.queryByRole("button")).toBeNull();
    expect(screen.getByRole("alert").textContent).toMatch(/does not know \("grab"\)/);
    expect(calls.reported).toHaveBeenCalledTimes(1);
  });
});
