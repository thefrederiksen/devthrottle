// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";

// THE PHONE'S KEY ROW SHOWS THE DIRECTOR'S REFUSAL (API mode phase 4, review of #4234, finding 1). A key the Director
// refused - an API-mode session whose protocol holds the conversation has no terminal - comes back as the Director's
// sentence, and the phone must show that sentence, never "Sent". Through the REAL controls; only the Gateway calls and
// the error boundary are faked.

const { sendPrompt, describeAndReport } = vi.hoisted(() => ({
  sendPrompt: vi.fn(),
  describeAndReport: vi.fn((_surface: string, _action: string, err: unknown) => (err instanceof Error ? err.message : String(err))),
}));

vi.mock("@devthrottle/client-core/errors/reportClientError", () => ({
  describeAndReport,
  reportClientError: vi.fn(),
  errorFacts: () => ({}),
}));

vi.mock("@devthrottle/client-core/api/client", () => ({
  sendPrompt,
  readPromptOutcome: vi.fn(),
  sendEscape: vi.fn(async () => {}),
  sendInterrupt: vi.fn(async () => {}),
  uploadImage: vi.fn(async () => ""),
}));

vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({
  backgroundTranscribeAndSend: vi.fn(async () => {}),
  abandonPendingDictation: vi.fn(async () => {}),
  dismissDictationStatus: vi.fn(async () => {}),
  retryDroppedDictation: vi.fn(async () => {}),
  retryPendingDictation: vi.fn(async () => {}),
  sendDroppedDictationAnyway: vi.fn(async () => {}),
}));

import { SessionControls } from "./SessionControls";

const SID = "sess-42";
const REFUSAL =
  "This session's protocol holds the conversation, so it has no terminal and keystrokes cannot be typed. Take the wheel to type into its terminal, or send a prompt in the chat.";

const onFlash = vi.fn();
const onError = vi.fn();

beforeEach(() => {
  sendPrompt.mockReset();
  onFlash.mockReset();
  onError.mockReset();
});

afterEach(() => cleanup());

describe("SessionControls key row", () => {
  it("a key the Director refused shows its sentence verbatim and never says Sent", async () => {
    sendPrompt.mockResolvedValue({ delivering: false, refusal: REFUSAL });
    render(<SessionControls sessionId={SID} onFlash={onFlash} onError={onError} showKeyRows />);

    fireEvent.click(screen.getByRole("button", { name: "Up" }));

    await waitFor(() => expect(onError).toHaveBeenCalledWith(REFUSAL));
    expect(onFlash).not.toHaveBeenCalled();
    expect(sendPrompt).toHaveBeenCalledWith(SID, "\x1b[A", false);
  });

  it("a key that was typed says Sent", async () => {
    sendPrompt.mockResolvedValue({ delivering: false });
    render(<SessionControls sessionId={SID} onFlash={onFlash} onError={onError} showKeyRows />);

    fireEvent.click(screen.getByRole("button", { name: "Enter" }));

    await waitFor(() => expect(onFlash).toHaveBeenCalledWith("Sent"));
    expect(onError).not.toHaveBeenCalled();
  });
});
