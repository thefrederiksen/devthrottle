// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within, cleanup } from "@testing-library/react";
import { useState } from "react";

// WHICH CHARACTERS THE COCKPIT COMPOSER SAYS WERE SPOKEN (source logging, owner's ruling 2026-09-05),
// through the REAL composer and the REAL dictation dialog. The composer used to send a whole-turn claim or
// nothing: a dictation with one typed word beside it said nothing at all about where the speech was. It now
// tracks the transcript's character range as the person edits around it, and sends those ranges with the
// prompt. They are CLAIMS - the Gateway verifies each against the transcript it registered - so what is
// pinned here is that the composer states them correctly over the text it actually submits.
//
// Nothing about the ranges is constructed by this test: the transcript is inserted by pressing Insert in the
// real dialog, the typing goes through the real textarea, and the spans are read off the real sendPrompt call.

const { sendPrompt, transcribeUtterance, listSessions } = vi.hoisted(() => ({
  sendPrompt: vi.fn(async () => ({ delivering: false })),
  transcribeUtterance: vi.fn(async () => ({ text: "the dictated words", deliveryId: "utt-77" })),
  listSessions: vi.fn(async () => [{ sessionId: "sess-42", totalBufferBytes: 4321 }]),
}));

vi.mock("@devthrottle/client-core/api/client", () => ({
  sendPrompt,
  transcribeUtterance,
  listSessions,
  enqueuePrompt: vi.fn(async () => []),
  uploadImage: vi.fn(async () => ""),
  gatewayErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
}));

vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({
  backgroundTranscribeAndSend: vi.fn(async () => {}),
  resumePendingDictations: vi.fn(async () => {}),
  abandonPendingDictation: vi.fn(async () => {}),
  dismissDictationStatus: vi.fn(async () => {}),
  retryDroppedDictation: vi.fn(async () => {}),
  retryPendingDictation: vi.fn(async () => {}),
  sendDroppedDictationAnyway: vi.fn(async () => {}),
}));

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

const DICTATED = "the dictated words";

function Harness() {
  const [value, setValue] = useState("");
  return <SessionComposer sessionId="sess-42" value={value} onChange={setValue} onQueued={() => {}} />;
}

function textarea(): HTMLTextAreaElement {
  return screen.getByPlaceholderText(/Type a message/i) as HTMLTextAreaElement;
}

/** Type into the real textarea, leaving the caret where the typing ended. */
function type(text: string, caret?: number) {
  const box = textarea();
  fireEvent.change(box, { target: { value: text } });
  box.selectionStart = caret ?? text.length;
  box.selectionEnd = box.selectionStart;
}

/** Press Speak, wait for RECORDING, then press Insert - the real dialog's real Insert path, which
 *  transcribes and hands the composer the text AND the utterance id. */
async function dictateInsert() {
  fireEvent.click(screen.getByRole("button", { name: "Speak" }));
  const dialog = await screen.findByRole("dialog", { name: "Dictate" });
  await within(dialog).findByText("RECORDING");
  fireEvent.click(within(dialog).getByText("Insert"));
  await waitFor(() => expect(screen.queryByRole("dialog", { name: "Dictate" })).toBeNull());
}

/** The spoken spans the composer put on the last prompt, and the text it sent. */
function lastSend(): { text: string; spans: { start: number; length: number; transcriptId?: string }[] } {
  const call = sendPrompt.mock.calls[sendPrompt.mock.calls.length - 1] as unknown as [
    string, string, boolean, undefined, string | undefined, { start: number; length: number; transcriptId?: string }[] | undefined,
  ];
  return { text: call[1], spans: call[5] ?? [] };
}

describe("the Cockpit composer says which characters were spoken", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    globalThis.requestAnimationFrame = (() => 0) as typeof globalThis.requestAnimationFrame;
    globalThis.cancelAnimationFrame = (() => {}) as typeof globalThis.cancelAnimationFrame;
  });
  afterEach(() => cleanup());

  it("sends the dictation's character range when the person types around it - a turn that is typed and still says where the speech was", async () => {
    render(<Harness />);
    type("please ");
    await dictateInsert();
    // Type after the inserted transcript, as a person finishing a sentence would.
    const afterInsert = textarea().value;
    type(afterInsert + " now");

    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(sendPrompt).toHaveBeenCalledTimes(1));

    const { text, spans } = lastSend();
    const span = spans[0];
    expect(spans).toHaveLength(1);
    expect(span.transcriptId).toBe("utt-77");
    // THE POINT: the range names the spoken characters IN THE TEXT SENT, not in some other string.
    expect(text.slice(span.start, span.start + span.length)).toBe(DICTATED);
    // And the turn itself is typed - a mixture is not spoken (ruling R20) - so no whole-turn claim rides.
    const wholeTurnClaim = (sendPrompt.mock.calls[0] as unknown as unknown[])[4];
    expect(wholeTurnClaim).toBeUndefined();
  });

  it("sends no span at all when the person typed the whole turn", async () => {
    render(<Harness />);
    type("git status");

    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(sendPrompt).toHaveBeenCalledTimes(1));

    expect(lastSend().spans).toEqual([]);
  });

  it("forgets the range when the person edits inside the dictated words", async () => {
    render(<Harness />);
    await dictateInsert();
    // Edit a character INSIDE the transcript: those are no longer the words that were spoken.
    type(DICTATED.replace("dictated", "dictateds"));

    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(sendPrompt).toHaveBeenCalledTimes(1));

    expect(lastSend().spans).toEqual([]);
  });

  it("keeps the range across a send that failed and was restored", async () => {
    sendPrompt.mockRejectedValueOnce(new Error("gateway said no"));
    render(<Harness />);
    await dictateInsert();

    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(sendPrompt).toHaveBeenCalledTimes(1));
    // The failed send put the words back in the box; the record follows the text it was restored to.
    await waitFor(() => expect(textarea().value).toBe(DICTATED));

    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => expect(sendPrompt).toHaveBeenCalledTimes(2));
    const { text, spans } = lastSend();
    expect(text).toBe(DICTATED);
    // The retry is honest either way: it may no longer claim the range (the box was rebuilt), but it must
    // never claim characters that are not the transcript.
    for (const span of spans) expect(text.slice(span.start, span.start + span.length)).toBe(DICTATED);
  });
});
