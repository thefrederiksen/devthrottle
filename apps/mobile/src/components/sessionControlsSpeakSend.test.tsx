// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within, cleanup } from "@testing-library/react";

// The MOBILE Speak flow's recording-stage Send: the captured audio goes to the durable background
// pipeline carrying the moment Send was pressed (voice delivery, #3398). This file used to pin the
// Speak-press byte baseline for the old "session moved on" rule (issue #2478); that rule is gone, and the
// hand-off must no longer carry it. It mirrors the cockpit's composerSpeakSend.test.tsx.

// vi.mock is hoisted above module scope, so the spies it references must be created in the same
// hoisted phase (vi.hoisted) rather than as ordinary consts.
const { sendPrompt, transcribeUtterance, backgroundTranscribeAndSend } = vi.hoisted(() => ({
  // The synchronous POST /prompt path (typed Send, Insert-then-Enter).
  sendPrompt: vi.fn(async () => {}),
  // The synchronous /wingman/utterance/* transcription (the Pause checkpoint and Insert).
  transcribeUtterance: vi.fn(async () => ({ text: "the dictated words", deliveryId: "utt-77" })),
  // The durable background pipeline (POST /dictation/*) the recording-stage Send rides.
  backgroundTranscribeAndSend: vi.fn(async () => {}),
}));

// The Gateway client boundary.
vi.mock("@devthrottle/client-core/api/client", () => ({
  sendPrompt,
  transcribeUtterance,
  sendEscape: vi.fn(async () => {}),
  sendInterrupt: vi.fn(async () => {}),
  uploadImage: vi.fn(async () => ""),
}));

// The durable background pipeline boundary.
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({
  backgroundTranscribeAndSend,
}));

// Mic + audio-decode boundaries jsdom cannot provide, byte-for-byte the fakes the cockpit test uses.
// The fake recorder fires onCaptureLive on start so the dialog flips to RECORDING without a real
// microphone, and returns a fixed clip on stop. A 1000 ms clip that decodes to 1.0 s means zero
// capture deficit, so the dialog commits instead of parking on a dropped-audio warning.
vi.mock("@devthrottle/client-core/dictation/recorder", () => {
  class MicRecorder {
    onCaptureLive: (() => void) | null = null;
    lastRecordedMs = 1000;
    deviceLabel = "Fake Microphone";
    deviceId = "fake-mic";
    async start() {
      this.onCaptureLive?.();
    }
    async stop() {
      return new Blob(["clip"], { type: "audio/webm" });
    }
    level() {
      return 0;
    }
    // The liveness clocks the dialog's animation loop reads on every frame; zero = a healthy
    // microphone, so the live capture alarm stays quiet and this file keeps testing the Send path.
    msSinceLastAudio() {
      return 0;
    }
    msSinceMeterMoved() {
      return 0;
    }
    dispose() {}
  }
  return { MicRecorder, rmsLevel: () => 0 };
});
vi.mock("@devthrottle/client-core/dictation/wav", () => ({
  blobToWav16kMono: async () => ({
    wav: new Blob(["wav"]),
    decodedSeconds: 1,
    sourceBytes: 1000,
    nativeSamples: new Float32Array(0),
    nativeSampleRate: 16000,
  }),
}));
vi.mock("@devthrottle/client-core/dictation/readyCue", () => ({
  playReadyCue: () => {},
  primeCueAudio: () => {},
  releaseCueAudio: () => {},
  startThinkingCue: () => () => {},
  playYourTurnCue: () => {},
}));

import { SessionControls } from "./SessionControls";

// Type "AB" into the input, drop the caret BETWEEN A and B, open Speak, and wait for RECORDING. The
// caret placement is what proves the compose lands the dictation at the caret, not at the end.
async function typeAndOpenRecordingDialog(): Promise<HTMLElement> {
  const input = screen.getByPlaceholderText(/type a message/i) as HTMLTextAreaElement;
  fireEvent.change(input, { target: { value: "AB" } });
  input.selectionStart = 1;
  input.selectionEnd = 1;
  fireEvent.click(screen.getByRole("button", { name: "Speak" }));
  const dialog = await screen.findByRole("dialog", { name: "Dictate" });
  await within(dialog).findByText("RECORDING");
  return dialog;
}

describe("Mobile Speak Send-direct (recording-stage)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // jsdom has no requestAnimationFrame; the dialog's display-only equalizer loop uses it. A no-op
    // that never calls back keeps the animation out of the test without affecting behaviour.
    globalThis.requestAnimationFrame = (() => 0) as typeof globalThis.requestAnimationFrame;
    globalThis.cancelAnimationFrame = (() => {}) as typeof globalThis.cancelAnimationFrame;
  });

  afterEach(() => cleanup());

  it("hands the captured audio to the background pipeline with the Send time, and no byte baseline", async () => {
    render(<SessionControls sessionId="sess-42" onFlash={() => {}} onError={() => {}} showKeyRows />);
    const dialog = await typeAndOpenRecordingDialog();
    const beforeSend = Date.now();

    // Press Send WHILE RECORDING - the fire-and-forget action under test.
    fireEvent.click(within(dialog).getByText("Send"));

    await waitFor(() => expect(backgroundTranscribeAndSend).toHaveBeenCalledTimes(1));
    const [sid, captured, opts] = backgroundTranscribeAndSend.mock.calls[0] as unknown as [
      string,
      { blob: Blob; recordedMs: number; sentAt: number },
      Record<string, unknown> & { composeParts?: { before: string; after: string } },
    ];
    expect(sid).toBe("sess-42");
    expect(captured.blob).toBeInstanceOf(Blob);
    expect(captured.recordedMs).toBe(1000);
    expect(opts.composeParts).toEqual({ before: "A", after: "B" });
    // The Send time (voice delivery, #3398), stamped at the press.
    expect(captured.sentAt).toBeGreaterThanOrEqual(beforeSend);
    expect(captured.sentAt).toBeLessThanOrEqual(Date.now());
    // The old rule's byte baseline is gone from the hand-off.
    expect(opts).not.toHaveProperty("baselineBufferBytes");

    // The screen is released immediately and nothing on this path blocks on the synchronous
    // transcription or submits a text prompt itself - the Gateway does both server-side.
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Dictate" })).toBeNull());
    expect(transcribeUtterance).not.toHaveBeenCalled();
    expect(sendPrompt).not.toHaveBeenCalled();
  });

  // R10 ("Clean up Your Throttle", 2026-09-05), and the parity half of it: the phone and the Cockpit
  // are the same act on two surfaces, so they must answer "was this spoken" the same way. A dictated
  // sentence submitted through the ordinary prompt door was recorded as TYPED - the Director reads
  // modality off the send source - and the delivery id of the utterance the Gateway just transcribed
  // is what says otherwise. Mirrors composerSpeakSend.test.tsx in the Cockpit workspace.
  it("a PURE dictation carries the spoken delivery id; typed text in the box drops the claim", async () => {
    render(<SessionControls sessionId="sess-42" onFlash={() => {}} onError={() => {}} showKeyRows />);

    // Nothing typed: open the dialog with an empty box, so what is sent is only the words.
    fireEvent.click(screen.getByRole("button", { name: "Speak" }));
    const clean = await screen.findByRole("dialog", { name: "Dictate" });
    await within(clean).findByText("RECORDING");
    fireEvent.click(clean.querySelector(".dictate-pause") as HTMLButtonElement);
    await within(clean).findByText("PAUSED");
    fireEvent.click(within(clean).getByText("Send"));
    await waitFor(() =>
      // The sixth argument is which characters were spoken (source logging, 2026-09-05): a pure dictation
      // claims the whole turn AND names the range that covers it.
      expect(sendPrompt).toHaveBeenCalledWith("sess-42", "the dictated words", true, undefined, "utt-77", [{ start: 0, length: 18, transcriptId: "utt-77" }]),
    );

    // Now with typed text around it: a mixture is not a spoken turn, and the page's own disclosure
    // already tells the reader so.
    sendPrompt.mockClear();
    const mixed = await typeAndOpenRecordingDialog();
    fireEvent.click(mixed.querySelector(".dictate-pause") as HTMLButtonElement);
    await within(mixed).findByText("PAUSED");
    fireEvent.click(within(mixed).getByText("Send"));
    await waitFor(() =>
      // A mixture is a TYPED turn (no whole-turn claim) that still says where the speech was.
      expect(sendPrompt).toHaveBeenCalledWith("sess-42", "A the dictated words B", true, undefined, undefined, [{ start: 2, length: 18, transcriptId: "utt-77" }]),
    );
  });
});
