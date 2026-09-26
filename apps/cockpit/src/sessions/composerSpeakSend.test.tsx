// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within, cleanup } from "@testing-library/react";
import { useState } from "react";

// Behavioral regression for the Cockpit Speak Send-direct path - REWIRED to the fire-and-forget
// pipeline (mobile parity), reversing the PR #1975 workaround.
//
// History, in order, because this file has pinned OPPOSITE behaviors at different times and the next
// reader must know why the current one is right:
//
//   1. Originally the recording-stage Send used the phone's durable pipeline
//      (backgroundTranscribeAndSend -> the /dictation/* routes). On the HOSTED Gateway those routes
//      then resolved sessions in the Local tenant partition (blocker #1884), so a hosted Send held
//      forever with no status - "Send basically sent nothing".
//   2. PR #1975 unplugged onSendAudio so Send fell back to the blocking transcribe-then-sendPrompt
//      path, and THIS TEST pinned "the durable pipeline is never touched".
//   3. The dictation upload family is now tenant-partitioned end to end (DictationTenantGate, PR
//      #1945; per-tenant transcript storage #2093) and the phone sends through it on hosted daily.
//      The Cockpit is rewired onto it (onSendAudio passed again), mounts the shared
//      DictationStatusStrip for feedback, and resumes pending dictations on load (AppShell). So the
//      pin flips: a recording-stage Send must hand the CAPTURED AUDIO to the background pipeline and
//      release the screen immediately - it must NOT block on a synchronous transcription.
//
// Revert-proof both ways: remove `onSendAudio={onDictateSendAudio}` from the DictationDialog mount
// (re-introduce the #1975 workaround) and the recording-stage Send blocks through transcribeUtterance
// + sendPrompt instead - every assertion below flips, so the test reddens. The PAUSED-stage test pins
// the path that deliberately did NOT change.

// vi.mock is hoisted above module scope, so the spies it references must be created in the same
// hoisted phase (vi.hoisted) rather than as ordinary consts.
const { sendPrompt, transcribeUtterance, backgroundTranscribeAndSend } = vi.hoisted(() => ({
  // The synchronous POST /prompt path: PAUSED-stage Send (text already in hand) and Insert use it.
  sendPrompt: vi.fn(async () => {}),
  // The synchronous /wingman/utterance/* transcription: the Pause checkpoint and Insert use it.
  transcribeUtterance: vi.fn(async () => ({ text: "the dictated words", deliveryId: "utt-77" })),
  // The durable background pipeline (POST /dictation/*) the recording-stage Send now rides.
  backgroundTranscribeAndSend: vi.fn(async () => {}),
}));

// The Gateway client boundary.
vi.mock("@devthrottle/client-core/api/client", () => ({
  sendPrompt,
  transcribeUtterance,
  enqueuePrompt: vi.fn(async () => []),
  uploadImage: vi.fn(async () => ""),
  gatewayErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
}));

// The durable background pipeline boundary. The recording-stage Send must call
// backgroundTranscribeAndSend; the other exports are what the shared DictationStatusStrip (mounted by
// the composer now) imports from the same module.
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({
  backgroundTranscribeAndSend,
  resumePendingDictations: vi.fn(async () => {}),
  abandonPendingDictation: vi.fn(async () => {}),
  dismissDictationStatus: vi.fn(async () => {}),
  retryDroppedDictation: vi.fn(async () => {}),
  retryPendingDictation: vi.fn(async () => {}),
  sendDroppedDictationAnyway: vi.fn(async () => {}),
}));

// Mic + audio-decode boundaries jsdom cannot provide. The fake recorder fires onCaptureLive on start
// so the dialog flips to RECORDING (the state under test) without a real microphone, and returns a
// fixed clip on stop. A 1000 ms clip that decodes to 1.0 s means zero capture deficit, so the dialog
// commits instead of parking on a dropped-audio warning.
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
    // The liveness clocks the dialog's animation loop reads. Zero = a healthy microphone, so the live
    // capture alarm stays quiet and this file keeps testing what it is about (the Send path). They are
    // here rather than omitted because the loop calls them on every frame: a fake without them is one
    // scheduled animation frame away from throwing, and the failure would look like a Send bug.
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

import { SessionComposer } from "./SessionComposer";
import { dismissDictationStatus, retryPendingDictation } from "@devthrottle/client-core/dictation/backgroundSend";
import {
  allDictationStatuses,
  clearDictationStatus,
  publishDictationStatus,
} from "@devthrottle/client-core/dictation/status";

// A parent that owns the composer text exactly like SessionDetail does, so onChange updates the value
// the composer reads back when it composes the dictation at the caret.
function Harness({ sessionId }: { sessionId?: string }) {
  const [value, setValue] = useState("");
  return (
    <SessionComposer sessionId={sessionId} value={value} onChange={setValue} onQueued={() => {}} />
  );
}

// Type "AB" into the composer, drop the caret BETWEEN A and B, open Speak, and wait for RECORDING.
// The caret placement is what proves the compose lands the dictation at the caret, not at the end.
async function typeAndOpenRecordingDialog(): Promise<HTMLElement> {
  const textarea = screen.getByPlaceholderText(/Type a message/i) as HTMLTextAreaElement;
  fireEvent.change(textarea, { target: { value: "AB" } });
  textarea.selectionStart = 1;
  textarea.selectionEnd = 1;
  fireEvent.click(screen.getByRole("button", { name: "Speak" }));
  const dialog = await screen.findByRole("dialog", { name: "Dictate" });
  await within(dialog).findByText("RECORDING");
  return dialog;
}

describe("Cockpit Speak Send-direct (recording-stage)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // jsdom has no requestAnimationFrame; the dialog's display-only equalizer loop uses it. A no-op
    // that never calls back keeps the animation out of the test without affecting behaviour.
    globalThis.requestAnimationFrame = (() => 0) as typeof globalThis.requestAnimationFrame;
    globalThis.cancelAnimationFrame = (() => {}) as typeof globalThis.cancelAnimationFrame;
  });

  // Two renders share this file; without vitest globals RTL does not auto-clean between them.
  afterEach(() => cleanup());

  it("hands the captured audio to the background pipeline with the typed text split at the caret, and releases the screen at once", async () => {
    render(<Harness sessionId="sess-42" />);
    const dialog = await typeAndOpenRecordingDialog();

    // Press Send WHILE RECORDING - the fire-and-forget action under test.
    fireEvent.click(within(dialog).getByText("Send"));

    // The captured audio goes to the durable background pipeline for the SELECTED session, with the
    // typed text split at the snapshotted caret so the Gateway submits "A <dictation> B".
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
    // The Send time (voice delivery, #3398): stamped by the dialog at the press, and the byte baseline the
    // old moved-on rule needed is gone from the hand-off.
    expect(typeof captured.sentAt).toBe("number");
    expect(captured.sentAt).toBeGreaterThan(0);
    expect(opts).not.toHaveProperty("baselineBufferBytes");

    // The screen is released immediately: the dialog is gone without waiting for any transcription.
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Dictate" })).toBeNull());

    // Nothing on this path blocks on the synchronous transcription or submits a text prompt itself -
    // the Gateway does both server-side. (Reverting the rewire flips exactly these.)
    expect(transcribeUtterance).not.toHaveBeenCalled();
    expect(sendPrompt).not.toHaveBeenCalled();

    // The typed text was cleared like any other Send (it rides in composeParts instead).
    expect((screen.getByPlaceholderText(/Type a message/i) as HTMLTextAreaElement).value).toBe("");
  });

  it("PAUSED-stage Send still submits the already-transcribed text synchronously via sendPrompt (unchanged path)", async () => {
    render(<Harness sessionId="sess-42" />);
    const dialog = await typeAndOpenRecordingDialog();

    // Pause: the checkpoint transcribes the segment synchronously and parks in PAUSED with the words
    // in the editable box. (The pause button renders a glyph, not text - reach it by its class.)
    const pauseBtn = dialog.querySelector(".dictate-pause") as HTMLButtonElement;
    fireEvent.click(pauseBtn);
    await within(dialog).findByText("PAUSED");
    expect(transcribeUtterance).toHaveBeenCalledTimes(1);

    // Send from PAUSED: the text is already in hand, so it submits via POST /prompt composed at the
    // snapshotted caret - no audio round trip, and the background pipeline stays untouched.
    fireEvent.click(within(dialog).getByText("Send"));
    await waitFor(() =>
      // The fifth argument is the whole-turn SPOKEN claim (ruling R10, "Clean up Your Throttle"), and it
      // is undefined here on purpose: the box held typed text, so this turn is a mixture of speech and
      // typing and is not claimed as spoken. See the test below for the pure-dictation case.
      //
      // The SIXTH is which characters were spoken (source logging, 2026-09-05): a mixture is a typed turn
      // that still says where the speech was, so the transcript's own range rides even though the claim
      // above does not. Both halves of the same honesty.
      expect(sendPrompt).toHaveBeenCalledWith("sess-42", "A the dictated words B", true, undefined, undefined, [{ start: 2, length: 18, transcriptId: "utt-77" }]),
    );
    expect(backgroundTranscribeAndSend).not.toHaveBeenCalled();
  });

  // R10 ("Clean up Your Throttle", 2026-09-05). A dictated sentence submitted through the ordinary
  // prompt door was recorded as TYPED, because the Director reads modality off the send source and an
  // ordinary prompt is a typed one. The delivery id of the utterance the Gateway just transcribed is
  // what says otherwise, and it must reach the send.
  it("a PURE dictation carries the spoken delivery id, so the turn is recorded as speech and not as typing", async () => {
    render(<Harness sessionId="sess-42" />);
    // No typed text this time: open the dialog with an empty box, so what is sent is only the words.
    fireEvent.click(screen.getByRole("button", { name: "Speak" }));
    const dialog = await screen.findByRole("dialog", { name: "Dictate" });
    await within(dialog).findByText("RECORDING");

    const pauseBtn = dialog.querySelector(".dictate-pause") as HTMLButtonElement;
    fireEvent.click(pauseBtn);
    await within(dialog).findByText("PAUSED");

    fireEvent.click(within(dialog).getByText("Send"));
    await waitFor(() =>
      // A pure dictation: the whole-turn claim AND the range covering the whole text.
      expect(sendPrompt).toHaveBeenCalledWith("sess-42", "the dictated words", true, undefined, "utt-77", [{ start: 0, length: 18, transcriptId: "utt-77" }]),
    );
  });

  // The claim is dropped the moment the person edits the words: an edited transcript is composition,
  // not a transcription, and the page's own disclosure already tells the reader it counts as typed.
  it("editing the transcript before sending drops the spoken claim", async () => {
    render(<Harness sessionId="sess-42" />);
    fireEvent.click(screen.getByRole("button", { name: "Speak" }));
    const dialog = await screen.findByRole("dialog", { name: "Dictate" });
    await within(dialog).findByText("RECORDING");

    const pauseBtn = dialog.querySelector(".dictate-pause") as HTMLButtonElement;
    fireEvent.click(pauseBtn);
    await within(dialog).findByText("PAUSED");

    const box = dialog.querySelector("textarea") as HTMLTextAreaElement;
    fireEvent.change(box, { target: { value: "the dictated words, reworded" } });

    fireEvent.click(within(dialog).getByText("Send"));
    await waitFor(() =>
      // Edited in the dialog before it ever reached the box: neither the whole-turn claim nor a range
      // survives, because those are no longer the characters the transcription produced.
      expect(sendPrompt).toHaveBeenCalledWith("sess-42", "the dictated words, reworded", true, undefined, undefined, []),
    );
  });
});

// What the shared status strip shows on THIS surface for the two phase 2 states (voice delivery, #3398).
// The status store is real; the Gateway's answer is published exactly as the driver publishes it.
describe("Cockpit composer: the Still delivering and too-old states", () => {
  afterEach(() => {
    cleanup();
    for (const s of allDictationStatuses()) clearDictationStatus(s.uploadId);
  });

  it("Still delivering is calm and offers neither Send anyway nor a fresh-id Retry", async () => {
    publishDictationStatus({
      sessionId: "sess-42",
      uploadId: "up-delivering",
      phase: "held",
      retryable: true,
      delivering: true,
      error: "Still delivering - checking that your words reached the session. They will not be sent twice.",
    });
    render(<Harness sessionId="sess-42" />);
    const strip = (await screen.findByText(/Still delivering/)).closest(".dictate-strip") as HTMLElement;
    // Calm: the in-progress look, never the red alert or the amber held strip.
    expect(strip.className).toContain("dictate-strip-delivering");
    expect(strip.className).not.toContain("dictate-strip-failed");
    expect(strip.className).not.toContain("dictate-strip-held");
    expect(strip.getAttribute("role")).toBe("status");
    // Nothing that could send a second copy.
    expect(within(strip).queryByRole("button", { name: "Send anyway" })).toBeNull();
    expect(within(strip).queryByRole("button", { name: "Retry" })).toBeNull();
    // The Gateway drives this delivery itself (phase 5): the one button is "Check now", which reads what the
    // Gateway ruled for this exact recording and sends nothing.
    expect(within(strip).queryByRole("button", { name: "Upload now" })).toBeNull();
    fireEvent.click(within(strip).getByRole("button", { name: "Check now" }));
    await waitFor(() => expect(retryPendingDictation).toHaveBeenCalledWith("up-delivering"));
  });

  it("too old shows the words back with Send anyway and the age wording", async () => {
    publishDictationStatus({
      sessionId: "sess-42",
      uploadId: "up-too-old",
      phase: "dropped",
      retryable: false,
      recoverableText: "the words from six minutes ago",
      offerSendAnyway: true,
      error: "This recording is more than 5 minutes old, so it was not sent automatically. Here is what you said - send it?",
    });
    render(<Harness sessionId="sess-42" />);
    const strip = (await screen.findByText(/more than 5 minutes old/)).closest(".dictate-strip") as HTMLElement;
    expect(within(strip).getByText("the words from six minutes ago")).toBeTruthy();
    expect(within(strip).getByRole("button", { name: "Send anyway" })).toBeTruthy();
  });

  // Phase 2, change 1: the Gateway could not confirm the words arrived, so it says not to offer them again.
  it("could not confirm shows the words, the label and Dismiss, and no Send anyway or Retry", async () => {
    publishDictationStatus({
      sessionId: "sess-42",
      uploadId: "up-unconfirmed",
      phase: "dropped",
      retryable: false,
      offerSendAnyway: false,
      recoverableText: "the words nobody confirmed",
      error: "We could not confirm this arrived. Here is what you said.",
    });
    render(<Harness sessionId="sess-42" />);
    const strip = (await screen.findByText("We could not confirm this arrived. Here is what you said.")).closest(
      ".dictate-strip",
    ) as HTMLElement;
    expect(within(strip).getByText("the words nobody confirmed")).toBeTruthy();
    expect(within(strip).getByRole("button", { name: "Dismiss" })).toBeTruthy();
    expect(within(strip).queryByRole("button", { name: "Send anyway" })).toBeNull();
    expect(within(strip).queryByRole("button", { name: "Retry" })).toBeNull();
    expect(within(strip).queryByRole("button", { name: "Upload now" })).toBeNull();

    // Dismiss is the one way out, and it goes to the driver's dismiss for this exact recording.
    fireEvent.click(within(strip).getByRole("button", { name: "Dismiss" }));
    await waitFor(() => expect(dismissDictationStatus).toHaveBeenCalledWith("up-unconfirmed"));
  });

  it("a shown-back status that does not carry the Gateway's offer offers no second send", async () => {
    publishDictationStatus({
      sessionId: "sess-42",
      uploadId: "up-no-offer",
      phase: "dropped",
      retryable: false,
      recoverableText: "words with no verdict",
      error: "This recording wasn't sent automatically. Here is what you said - send it?",
    });
    render(<Harness sessionId="sess-42" />);
    const strip = (await screen.findByText("words with no verdict")).closest(".dictate-strip") as HTMLElement;
    expect(within(strip).queryByRole("button", { name: "Send anyway" })).toBeNull();
    expect(within(strip).getByRole("button", { name: "Dismiss" })).toBeTruthy();
  });
});
